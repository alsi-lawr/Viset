namespace Viset

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Threading
open System.Threading.Tasks

module private FrameInternals =
    let private utf8 = UTF8Encoding(false)

    let javascriptString value =
        String.Concat("\"", JavaScriptEncoder.Default.Encode value, "\"")

    let bootstrapScript token (device: Device) =
        let imagePath = String.Concat("/", token, "/image.png")
        let builder = StringBuilder()
        builder.Append("(() => {\n") |> ignore
        builder.Append("const subscribers = new Set();\n") |> ignore
        builder.Append("let generation = 0;\n") |> ignore
        builder.Append("const device = Object.freeze({") |> ignore

        builder.Append("name:").Append(javascriptString device.Name).Append(',')
        |> ignore

        builder.Append("mobile:").Append(if device.Mobile then "true" else "false").Append(',')
        |> ignore

        builder.Append("touch:").Append(if device.Touch then "true" else "false").Append(',')
        |> ignore

        builder
            .Append("device_scale:")
            .Append(device.DeviceScale.ToString("R", Globalization.CultureInfo.InvariantCulture))
            .Append(',')
        |> ignore

        builder.Append("viewport_width:").Append(device.Viewport.Width).Append(',')
        |> ignore

        builder.Append("viewport_height:").Append(device.Viewport.Height).Append(',')
        |> ignore

        match device.Frame with
        | Some frame ->
            builder.Append("frame_width:").Append(frame.Width).Append(',') |> ignore
            builder.Append("frame_height:").Append(frame.Height) |> ignore
        | None ->
            builder.Append("frame_width:null,") |> ignore
            builder.Append("frame_height:null") |> ignore

        builder.Append("});\n") |> ignore

        builder.Append("const snapshot = () => Object.freeze({generation,device,image_url:")
        |> ignore

        builder.Append(javascriptString imagePath).Append(" + '?generation=' + generation});\n")
        |> ignore

        builder.Append("const notify = async () => {const value=snapshot();") |> ignore

        builder.Append("await Promise.all(Array.from(subscribers, callback => callback(value))); ")
        |> ignore

        builder.Append("window.dispatchEvent(new CustomEvent('viset-frame-update',{detail:value})); return value;};\n")
        |> ignore

        builder.Append("window.visetFrame = Object.freeze({device,get current(){return snapshot();},")
        |> ignore

        builder.Append(
            "subscribe(callback){if(typeof callback!=='function'){throw new TypeError('callback must be a function');}"
        )
        |> ignore

        builder.Append(
            "subscribers.add(callback); Promise.resolve(callback(snapshot())); return () => subscribers.delete(callback);},"
        )
        |> ignore

        builder.Append("async update(){generation += 1; return await notify();}});\n")
        |> ignore

        builder.Append("window.addEventListener('DOMContentLoaded', () => {window.dispatchEvent(")
        |> ignore

        builder.Append("new CustomEvent('viset-frame-ready',{detail:window.visetFrame.current}));}, {once:true});\n")
        |> ignore

        builder.Append("})();\n") |> ignore
        utf8.GetBytes(builder.ToString())

    let injectBootstrap token (html: string) =
        let script = String.Concat("<script src=\"/", token, "/viset-frame.js\"></script>")
        let openingHead = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase)

        if openingHead >= 0 then
            let closingBracket = html.IndexOf('>', openingHead)

            if closingBracket >= 0 then
                html.Insert(closingBracket + 1, script)
            else
                String.Concat(script, html)
        else
            String.Concat(script, html)

    let writeResponseAsync (stream: NetworkStream) status contentType (body: byte array) cancellationToken =
        task {
            let header =
                String.Concat(
                    "HTTP/1.1 ",
                    status,
                    "\r\nContent-Type: ",
                    contentType,
                    "\r\nContent-Length: ",
                    body.Length.ToString(Globalization.CultureInfo.InvariantCulture),
                    "\r\nCache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n"
                )

            let headerBytes = utf8.GetBytes header
            do! stream.WriteAsync(headerBytes, cancellationToken)
            do! stream.WriteAsync(body, cancellationToken)
            do! stream.FlushAsync cancellationToken
        }

    let readRequestPathAsync (stream: NetworkStream) (cancellationToken: CancellationToken) =
        task {
            use reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true)
            let! requestLine = reader.ReadLineAsync(cancellationToken)

            match Option.ofObj requestLine with
            | None -> return None
            | Some line when String.IsNullOrWhiteSpace line -> return None
            | Some line ->
                let parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let mutable headersComplete = false

                while not headersComplete do
                    let! headerLine = reader.ReadLineAsync(cancellationToken)

                    headersComplete <-
                        match Option.ofObj headerLine with
                        | None -> true
                        | Some value -> String.IsNullOrEmpty value

                if
                    parts.Length <> 3
                    || not (String.Equals(parts[0], "GET", StringComparison.Ordinal))
                then
                    return None
                else
                    let path = parts[1].Split('?', 2)[0]
                    return Some path
        }

type FrameServer private (listener: TcpListener, token: string, html: byte array, script: byte array) =
    let cancellation = new CancellationTokenSource()
    let imageLock = obj ()
    let mutable image = Array.empty<byte>
    let mutable disposed = 0

    let currentImage () =
        lock imageLock (fun () -> Array.copy image)

    let handleClientAsync (client: TcpClient) =
        task {
            use client = client
            use stream = client.GetStream()
            let! path = FrameInternals.readRequestPathAsync stream cancellation.Token
            let rootPath = String.Concat("/", token, "/")

            match path with
            | Some value when String.Equals(value, rootPath, StringComparison.Ordinal) ->
                do! FrameInternals.writeResponseAsync stream "200 OK" "text/html; charset=utf-8" html cancellation.Token
            | Some value when String.Equals(value, String.Concat(rootPath, "viset-frame.js"), StringComparison.Ordinal) ->
                do!
                    FrameInternals.writeResponseAsync
                        stream
                        "200 OK"
                        "text/javascript; charset=utf-8"
                        script
                        cancellation.Token
            | Some value when String.Equals(value, String.Concat(rootPath, "image.png"), StringComparison.Ordinal) ->
                let bytes = currentImage ()

                if bytes.Length = 0 then
                    do!
                        FrameInternals.writeResponseAsync
                            stream
                            "404 Not Found"
                            "text/plain; charset=utf-8"
                            (Encoding.UTF8.GetBytes "Frame image is not available.")
                            cancellation.Token
                else
                    do! FrameInternals.writeResponseAsync stream "200 OK" "image/png" bytes cancellation.Token
            | _ ->
                do!
                    FrameInternals.writeResponseAsync
                        stream
                        "404 Not Found"
                        "text/plain; charset=utf-8"
                        (Encoding.UTF8.GetBytes "Not found.")
                        cancellation.Token
        }

    let rec serveAsync () =
        task {
            try
                let! client = listener.AcceptTcpClientAsync cancellation.Token

                try
                    do! handleClientAsync client
                with
                | :? IOException
                | :? SocketException -> ()

                return! serveAsync ()
            with
            | :? OperationCanceledException when cancellation.IsCancellationRequested -> ()
            | :? ObjectDisposedException when cancellation.IsCancellationRequested -> ()
            | :? SocketException when cancellation.IsCancellationRequested -> ()
        }

    let serverTask = serveAsync ()

    member _.Url =
        let endpoint = listener.LocalEndpoint :?> IPEndPoint
        Uri(String.Concat("http://127.0.0.1:", endpoint.Port, "/", token, "/"))

    member _.UpdateImage(bytes: byte array) =
        ArgumentNullException.ThrowIfNull bytes

        if bytes.Length = 0 then
            invalidArg (nameof bytes) "Frame image bytes must not be empty."

        lock imageLock (fun () -> image <- Array.copy bytes)

    member private _.DisposeCoreAsync() =
        task {
            if Interlocked.Exchange(&disposed, 1) = 0 then
                cancellation.Cancel()
                listener.Stop()

                try
                    do! serverTask
                finally
                    cancellation.Dispose()
        }

    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask(this.DisposeCoreAsync())

    static member Start(frameSource: FrameSource, device: Device, initialImage: byte array) =
        ArgumentNullException.ThrowIfNull initialImage

        let source =
            match frameSource with
            | CustomFrame path ->
                if not (File.Exists path) then
                    invalidArg (nameof frameSource) (String.Concat("Frame HTML does not exist: ", path))

                File.ReadAllText path
            | BuiltInFrame style -> BuiltInFrames.html style device

        let token =
            RandomNumberGenerator.GetBytes 32
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()

        let html = FrameInternals.injectBootstrap token source |> Encoding.UTF8.GetBytes
        let script = FrameInternals.bootstrapScript token device
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let server = FrameServer(listener, token, html, script)
        server.UpdateImage initialImage
        server

type FrameRenderer private (session: BrowserSession, server: FrameServer, readinessTimeout: TimeSpan) =
    let readyExpression = "document.querySelector('[data-frame-ready]') !== null"

    let waitUntilReadyAsync (cancellationToken: CancellationToken) =
        task {
            use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
            timeout.CancelAfter readinessTimeout
            let mutable ready = false

            try
                while not ready do
                    let! result = session.EvaluateAsync(readyExpression, timeout.Token)

                    match result with
                    | Ok(CdpEvaluationValue.Boolean value) -> ready <- value
                    | Ok _ -> ready <- false
                    | Error error ->
                        raise (
                            InvalidOperationException(
                                String.Concat("Frame readiness evaluation failed: ", error.ToString())
                            )
                        )

                    if not ready then
                        do! Task.Delay(20, timeout.Token)
            with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                raise (
                    TimeoutException(
                        String.Concat(
                            "Frame did not signal data-frame-ready within ",
                            readinessTimeout.TotalMilliseconds,
                            " ms."
                        )
                    )
                )
        }

    member _.CapturePngAsync(cancellationToken: CancellationToken) =
        session.CapturePngAsync cancellationToken

    member private _.WaitUntilReadyAsync(cancellationToken: CancellationToken) = waitUntilReadyAsync cancellationToken

    member _.UpdateAsync(image: byte array, cancellationToken: CancellationToken) =
        task {
            Media.validatePng image |> ignore

            let! clearResult =
                session.EvaluateAsync(
                    "document.querySelectorAll('[data-frame-ready]').forEach(element => element.removeAttribute('data-frame-ready')); true",
                    cancellationToken
                )

            match clearResult with
            | Error error ->
                raise (InvalidOperationException(String.Concat("Frame readiness reset failed: ", error.ToString())))
            | Ok _ -> ()

            server.UpdateImage image
            let! updateResult = session.EvaluateAsync("window.visetFrame.update().then(() => true)", cancellationToken)

            match updateResult with
            | Error error -> raise (InvalidOperationException(String.Concat("Frame update failed: ", error.ToString())))
            | Ok _ -> do! waitUntilReadyAsync cancellationToken
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            (server :> IAsyncDisposable).DisposeAsync()

    static member StartAsync
        (
            session: BrowserSession,
            frameSource: FrameSource,
            device: Device,
            initialImage: byte array,
            readinessTimeout: TimeSpan,
            cancellationToken: CancellationToken
        ) =
        task {
            ArgumentNullException.ThrowIfNull session

            if readinessTimeout <= TimeSpan.Zero then
                invalidArg (nameof readinessTimeout) "Frame readiness timeout must be positive."

            let frame =
                device.Frame
                |> Option.defaultWith (fun () ->
                    invalidArg (nameof device) "The selected device has no frame dimensions.")

            Media.validatePng initialImage |> ignore
            let server = FrameServer.Start(frameSource, device, initialImage)

            try
                do!
                    session.ConfigureEmulationAsync(
                        frame.Width,
                        frame.Height,
                        device.DeviceScale,
                        false,
                        false,
                        cancellationToken
                    )

                do! session.SetTransparentBackgroundAsync cancellationToken
                do! session.NavigateAsync(server.Url, cancellationToken)
                let renderer = FrameRenderer(session, server, readinessTimeout)
                do! renderer.WaitUntilReadyAsync cancellationToken
                return renderer
            with error ->
                do! (server :> IAsyncDisposable).DisposeAsync().AsTask()
                return raise error
        }
