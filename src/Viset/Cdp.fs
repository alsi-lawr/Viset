namespace Viset

open System
open System.Buffers
open System.Collections.Concurrent
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net.WebSockets
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Viset.Serialization

type CdpProtocolError(code: int, message: string, data: JsonElement option) =
    member _.Code = code
    member _.Message = message
    member _.Data = data

    override _.ToString() =
        String.Concat(code.ToString(CultureInfo.InvariantCulture), ": ", message)

[<DebuggerDisplay("CdpEvaluationValue")>]
type CdpEvaluationValue =
    | Undefined
    | Null
    | Boolean of bool
    | Number of double
    | String of string
    | Json of JsonElement

    override value.ToString() =
        match value with
        | Undefined -> "undefined"
        | Null -> "null"
        | Boolean flag -> if flag then "true" else "false"
        | Number number -> number.ToString("R", CultureInfo.InvariantCulture)
        | String text -> text
        | Json json -> json.GetRawText()

[<DebuggerDisplay("CdpEvaluationError")>]
type CdpEvaluationError =
    | Protocol of CdpProtocolError
    | JavaScript of string

    override error.ToString() =
        match error with
        | Protocol protocol -> protocol.ToString()
        | JavaScript message -> message

type CdpProtocolException(error: CdpProtocolError) =
    inherit Exception(String.Concat("CDP protocol error ", error.ToString()))
    member _.Error = error

type CdpTimeoutException(methodName: string, timeout: TimeSpan) =
    inherit
        TimeoutException(
            String.Concat(
                "CDP method '",
                methodName,
                "' exceeded timeout ",
                timeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                " ms."
            )
        )

type CdpConnectionException(message: string, innerException: Exception) =
    inherit Exception(message, innerException)

type CdpScreencastFrame =
    { Bytes: byte array
      Timestamp: TimeSpan
      SessionId: int }

    override frame.ToString() =
        frame.Timestamp.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)

type CdpClient private (socket: ClientWebSocket, commandTimeout: TimeSpan) =
    let pending =
        ConcurrentDictionary<int64, TaskCompletionSource<CdpIncomingMessageModel>>()

    let pageLoadEvents = Channel.CreateUnbounded<CdpIncomingMessageModel>()
    let screencastEvents = Channel.CreateUnbounded<CdpIncomingMessageModel>()
    let sendLock = new SemaphoreSlim(1, 1)
    let readerCancellation = new CancellationTokenSource()
    let mutable nextId = 0L
    let mutable disposed = 0

    let protocolError (model: CdpErrorModel) =
        let data =
            if model.Data.ValueKind = JsonValueKind.Undefined then
                None
            else
                Some(model.Data.Clone())

        CdpProtocolError(model.Code, model.Message, data)

    let failPending (error: Exception) =
        for entry in pending do
            match pending.TryRemove entry.Key with
            | true, completion -> completion.TrySetException error |> ignore
            | false, _ -> ()

        pageLoadEvents.Writer.TryComplete error |> ignore
        screencastEvents.Writer.TryComplete error |> ignore

    let eventChannel methodName =
        match methodName with
        | "Page.loadEventFired" -> pageLoadEvents
        | "Page.screencastFrame" -> screencastEvents
        | _ -> invalidArg (nameof methodName) (String.Concat("Unsupported CDP event: ", methodName))

    let clearEvents methodName =
        let channel = eventChannel methodName
        let mutable ignored = Unchecked.defaultof<CdpIncomingMessageModel>

        while channel.Reader.TryRead(&ignored) do
            ()

    let readLoop () =
        task {
            let buffer = ArrayPool<byte>.Shared.Rent 8192

            try
                try
                    while not readerCancellation.IsCancellationRequested do
                        use message = new MemoryStream()
                        let mutable complete = false

                        while not complete do
                            let! result = socket.ReceiveAsync(ArraySegment<byte>(buffer), readerCancellation.Token)

                            if result.MessageType = WebSocketMessageType.Close then
                                raise (WebSocketException "The CDP WebSocket closed unexpectedly.")

                            message.Write(buffer, 0, result.Count)

                            if message.Length > 16L * 1024L * 1024L then
                                raise (InvalidDataException "A CDP message exceeded 16 MiB.")

                            complete <- result.EndOfMessage

                        let incoming = CdpJsonModels.DeserializeIncoming(message.ToArray())

                        if incoming.Id.HasValue then
                            match pending.TryRemove incoming.Id.Value with
                            | true, completion -> completion.TrySetResult incoming |> ignore
                            | false, _ -> ()
                        else
                            match incoming.Method with
                            | "Page.loadEventFired" -> pageLoadEvents.Writer.TryWrite incoming |> ignore
                            | "Page.screencastFrame" -> screencastEvents.Writer.TryWrite incoming |> ignore
                            | _ -> ()
                with
                | :? OperationCanceledException when readerCancellation.IsCancellationRequested ->
                    failPending (OperationCanceledException "The CDP reader was stopped.")
                | error -> failPending error
            finally
                ArrayPool<byte>.Shared.Return buffer
        }

    let readerTask = readLoop ()

    member private _.SendCommandAsync
        (methodName: string, serialize: int64 -> byte array, cancellationToken: CancellationToken)
        =
        task {
            if Volatile.Read(&disposed) <> 0 then
                raise (ObjectDisposedException(nameof CdpClient))

            let id = Interlocked.Increment(&nextId)

            let completion =
                TaskCompletionSource<CdpIncomingMessageModel>(TaskCreationOptions.RunContinuationsAsynchronously)

            if not (pending.TryAdd(id, completion)) then
                raise (InvalidOperationException "A duplicate CDP command ID was generated.")

            use timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            timeoutCancellation.CancelAfter commandTimeout

            try
                let payload = serialize id
                do! sendLock.WaitAsync timeoutCancellation.Token

                try
                    do!
                        socket.SendAsync(
                            ArraySegment<byte>(payload),
                            WebSocketMessageType.Text,
                            true,
                            timeoutCancellation.Token
                        )
                finally
                    sendLock.Release() |> ignore

                let! incoming = completion.Task.WaitAsync timeoutCancellation.Token
                pending.TryRemove id |> ignore

                match Option.ofObj incoming.Error with
                | None -> return Ok incoming.Result
                | Some incomingError -> return Error(protocolError incomingError)
            with
            | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                pending.TryRemove id |> ignore
                return raise (CdpTimeoutException(methodName, commandTimeout))
            | error ->
                pending.TryRemove id |> ignore
                return raise error
        }

    member private this.SendEmptyAsync(methodName: string, cancellationToken: CancellationToken) =
        this.SendCommandAsync(
            methodName,
            (fun id -> CdpJsonModels.SerializeCommand(id, methodName, CdpEmptyParameters())),
            cancellationToken
        )

    member private _.RequireSuccess(result: Result<JsonElement, CdpProtocolError>) =
        match result with
        | Ok value -> value
        | Error error -> raise (CdpProtocolException error)

    member this.EnablePageAndRuntimeAsync(cancellationToken: CancellationToken) =
        task {
            let! page = this.SendEmptyAsync("Page.enable", cancellationToken)
            this.RequireSuccess page |> ignore
            let! runtime = this.SendEmptyAsync("Runtime.enable", cancellationToken)
            this.RequireSuccess runtime |> ignore
        }

    member private _.ReadEventAsync(methodName: string, cancellationToken: CancellationToken) =
        task {
            let channel = eventChannel methodName

            use timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            timeoutCancellation.CancelAfter commandTimeout

            try
                return! channel.Reader.ReadAsync(timeoutCancellation.Token).AsTask()
            with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                return raise (CdpTimeoutException(methodName, commandTimeout))
        }

    member this.WaitForEventAsync(methodName: string, cancellationToken: CancellationToken) =
        task {
            let! _ = this.ReadEventAsync(methodName, cancellationToken)
            return ()
        }

    member this.NavigateAsync(url: Uri, cancellationToken: CancellationToken) =
        task {
            ArgumentNullException.ThrowIfNull url
            clearEvents "Page.loadEventFired"

            use loadCancellation =
                CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            let loadTask = this.WaitForEventAsync("Page.loadEventFired", loadCancellation.Token)

            try
                let parameters = CdpNavigateParameters(Url = url.AbsoluteUri)

                let! response =
                    this.SendCommandAsync(
                        "Page.navigate",
                        (fun id -> CdpJsonModels.SerializeCommand(id, "Page.navigate", parameters)),
                        cancellationToken
                    )

                let result = this.RequireSuccess response |> CdpJsonModels.DeserializeNavigateResult

                if not (String.IsNullOrWhiteSpace result.ErrorText) then
                    raise (InvalidOperationException(String.Concat("Page navigation failed: ", result.ErrorText)))

                do! loadTask
            with error ->
                loadCancellation.Cancel()
                return raise error
        }

    member this.EvaluateAsync(expression: string, cancellationToken: CancellationToken) =
        task {
            ArgumentException.ThrowIfNullOrWhiteSpace expression
            let parameters = CdpEvaluateParameters(Expression = expression)

            let! response =
                this.SendCommandAsync(
                    "Runtime.evaluate",
                    (fun id -> CdpJsonModels.SerializeCommand(id, "Runtime.evaluate", parameters)),
                    cancellationToken
                )

            match response with
            | Error error -> return Error(CdpEvaluationError.Protocol error)
            | Ok resultElement ->
                let result = CdpJsonModels.DeserializeEvaluateResult resultElement

                match Option.ofObj result.ExceptionDetails with
                | Some exceptionDetails ->
                    let description =
                        match Option.ofObj exceptionDetails.Exception with
                        | Some exceptionObject ->
                            exceptionObject.Description
                            |> Option.ofObj
                            |> Option.filter (String.IsNullOrWhiteSpace >> not)
                            |> Option.defaultValue exceptionDetails.Text
                        | None -> exceptionDetails.Text

                    return Error(CdpEvaluationError.JavaScript description)
                | None ->
                    let remote = result.Result

                    match remote.Type with
                    | "undefined" -> return Ok CdpEvaluationValue.Undefined
                    | "boolean" -> return Ok(CdpEvaluationValue.Boolean(remote.Value.GetBoolean()))
                    | "number" -> return Ok(CdpEvaluationValue.Number(remote.Value.GetDouble()))
                    | "string" ->
                        return
                            Ok(
                                CdpEvaluationValue.String(
                                    remote.Value.GetString() |> Option.ofObj |> Option.defaultValue String.Empty
                                )
                            )
                    | "object" when String.Equals(remote.Subtype, "null", StringComparison.Ordinal) ->
                        return Ok CdpEvaluationValue.Null
                    | _ when remote.Value.ValueKind <> JsonValueKind.Undefined ->
                        return Ok(CdpEvaluationValue.Json(remote.Value.Clone()))
                    | unsupported ->
                        return
                            Error(
                                CdpEvaluationError.JavaScript(
                                    String.Concat("Runtime.evaluate returned unsupported type '", unsupported, "'.")
                                )
                            )
        }

    member this.ConfigureEmulationAsync
        (
            width: int,
            height: int,
            deviceScaleFactor: double,
            mobile: bool,
            touch: bool,
            cancellationToken: CancellationToken
        ) =
        task {
            if width <= 0 || height <= 0 then
                invalidArg (nameof width) "Emulation dimensions must be positive."

            if not (Double.IsFinite deviceScaleFactor) || deviceScaleFactor <= 0.0 then
                invalidArg (nameof deviceScaleFactor) "Device scale factor must be positive and finite."

            let metrics =
                CdpDeviceMetricsParameters(
                    Width = width,
                    Height = height,
                    DeviceScaleFactor = deviceScaleFactor,
                    Mobile = mobile
                )

            let! metricsResponse =
                this.SendCommandAsync(
                    "Emulation.setDeviceMetricsOverride",
                    (fun id -> CdpJsonModels.SerializeCommand(id, "Emulation.setDeviceMetricsOverride", metrics)),
                    cancellationToken
                )

            this.RequireSuccess metricsResponse |> ignore

            let touchParameters =
                CdpTouchEmulationParameters(Enabled = touch, MaxTouchPoints = 1)

            let! touchResponse =
                this.SendCommandAsync(
                    "Emulation.setTouchEmulationEnabled",
                    (fun id ->
                        CdpJsonModels.SerializeCommand(id, "Emulation.setTouchEmulationEnabled", touchParameters)),
                    cancellationToken
                )

            this.RequireSuccess touchResponse |> ignore
        }

    member this.SetTransparentBackgroundAsync(cancellationToken: CancellationToken) =
        task {
            let parameters =
                CdpBackgroundParameters(Color = CdpColorModel(R = 0, G = 0, B = 0, A = 0.0))

            let! response =
                this.SendCommandAsync(
                    "Emulation.setDefaultBackgroundColorOverride",
                    (fun id ->
                        CdpJsonModels.SerializeCommand(id, "Emulation.setDefaultBackgroundColorOverride", parameters)),
                    cancellationToken
                )

            this.RequireSuccess response |> ignore
        }

    member this.TouchAsync(x: double, y: double, cancellationToken: CancellationToken) =
        task {
            if not (Double.IsFinite x) || not (Double.IsFinite y) || x < 0.0 || y < 0.0 then
                invalidArg (nameof x) "Touch coordinates must be non-negative finite numbers."

            let point = CdpTouchPointModel(X = x, Y = y)

            let startParameters =
                CdpDispatchTouchEventParameters(Type = "touchStart", TouchPoints = ResizeArray [ point ])

            let! startResponse =
                this.SendCommandAsync(
                    "Input.dispatchTouchEvent",
                    (fun id -> CdpJsonModels.SerializeCommand(id, "Input.dispatchTouchEvent", startParameters)),
                    cancellationToken
                )

            this.RequireSuccess startResponse |> ignore

            let endParameters = CdpDispatchTouchEventParameters(Type = "touchEnd")

            let! endResponse =
                this.SendCommandAsync(
                    "Input.dispatchTouchEvent",
                    (fun id -> CdpJsonModels.SerializeCommand(id, "Input.dispatchTouchEvent", endParameters)),
                    cancellationToken
                )

            this.RequireSuccess endResponse |> ignore
        }

    member this.CapturePngAsync(cancellationToken: CancellationToken) =
        task {
            let parameters = CdpScreenshotParameters()

            let! response =
                this.SendCommandAsync(
                    "Page.captureScreenshot",
                    (fun id -> CdpJsonModels.SerializeCommand(id, "Page.captureScreenshot", parameters)),
                    cancellationToken
                )

            let result =
                this.RequireSuccess response |> CdpJsonModels.DeserializeScreenshotResult

            return Convert.FromBase64String result.Data
        }

    member this.StartScreencastAsync
        (source: WebPSource, width: int, height: int, cancellationToken: CancellationToken)
        =
        task {
            if width <= 0 || height <= 0 then
                invalidArg (nameof width) "Screencast dimensions must be positive."

            let parameters =
                CdpScreencastParameters(MaxWidth = width, MaxHeight = height, EveryNthFrame = 1)

            match source with
            | PngScreencast -> parameters.Format <- "png"
            | JpegScreencast quality ->
                parameters.Format <- "jpeg"
                parameters.Quality <- quality

            clearEvents "Page.screencastFrame"

            let! response =
                this.SendCommandAsync(
                    "Page.startScreencast",
                    (fun id -> CdpJsonModels.SerializeCommand(id, "Page.startScreencast", parameters)),
                    cancellationToken
                )

            this.RequireSuccess response |> ignore
        }

    member this.StartScreencastAsync(source: WebPSource, cancellationToken: CancellationToken) =
        task {
            clearEvents "Page.screencastFrame"
            let parameters = CdpScreencastParameters(EveryNthFrame = 1)

            match source with
            | PngScreencast -> parameters.Format <- "png"
            | JpegScreencast quality ->
                parameters.Format <- "jpeg"
                parameters.Quality <- quality

            let! response =
                this.SendCommandAsync(
                    "Page.startScreencast",
                    (fun id -> CdpJsonModels.SerializeCommand(id, "Page.startScreencast", parameters)),
                    cancellationToken
                )

            this.RequireSuccess response |> ignore
        }

    member this.ReadScreencastFrameAsync(cancellationToken: CancellationToken) =
        task {
            let! incoming = this.ReadEventAsync("Page.screencastFrame", cancellationToken)
            let frame = CdpJsonModels.DeserializeScreencastFrame incoming.Parameters

            return
                { Bytes = Convert.FromBase64String frame.Data
                  Timestamp = TimeSpan.FromSeconds frame.Metadata.Timestamp
                  SessionId = frame.SessionId }
        }

    member this.AcknowledgeScreencastFrameAsync(sessionId: int, cancellationToken: CancellationToken) =
        task {
            if sessionId < 0 then
                invalidArg (nameof sessionId) "Screencast session ID must be non-negative."

            let parameters = CdpScreencastFrameAckParameters(SessionId = sessionId)

            let! response =
                this.SendCommandAsync(
                    "Page.screencastFrameAck",
                    (fun id -> CdpJsonModels.SerializeCommand(id, "Page.screencastFrameAck", parameters)),
                    cancellationToken
                )

            this.RequireSuccess response |> ignore
        }

    member this.StopScreencastAsync(cancellationToken: CancellationToken) =
        task {
            let! response = this.SendEmptyAsync("Page.stopScreencast", cancellationToken)
            this.RequireSuccess response |> ignore
        }

    member private _.DisposeCoreAsync() =
        task {
            if Interlocked.Exchange(&disposed, 1) = 0 then
                readerCancellation.Cancel()

                try
                    if
                        socket.State = WebSocketState.Open
                        || socket.State = WebSocketState.CloseReceived
                    then
                        use closeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds 1.0)

                        do!
                            socket.CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Viset session closed.",
                                closeCancellation.Token
                            )
                with _ ->
                    ()

                socket.Dispose()

                try
                    do! readerTask
                with _ ->
                    ()

                sendLock.Dispose()
                readerCancellation.Dispose()
        }

    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask(this.DisposeCoreAsync())

    static member ConnectAsync(endpoint: Uri, commandTimeout: TimeSpan, cancellationToken: CancellationToken) =
        task {
            ArgumentNullException.ThrowIfNull endpoint

            if commandTimeout <= TimeSpan.Zero then
                invalidArg (nameof commandTimeout) "CDP command timeout must be positive."

            let socket = new ClientWebSocket()

            use connectCancellation =
                CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            connectCancellation.CancelAfter commandTimeout

            try
                do! socket.ConnectAsync(endpoint, connectCancellation.Token)
                return CdpClient(socket, commandTimeout)
            with error ->
                socket.Dispose()

                if
                    error :? OperationCanceledException
                    && not cancellationToken.IsCancellationRequested
                then
                    return
                        raise (
                            CdpConnectionException(
                                String.Concat("Timed out connecting to CDP endpoint ", endpoint.AbsoluteUri, "."),
                                error
                            )
                        )
                else
                    return
                        raise (
                            CdpConnectionException(
                                String.Concat("Failed to connect to CDP endpoint ", endpoint.AbsoluteUri, "."),
                                error
                            )
                        )
        }
