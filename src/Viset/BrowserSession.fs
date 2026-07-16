namespace Viset

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Viset.Serialization

type BrowserSessionException(message: string, innerException: Exception) =
    inherit Exception(message, innerException)

type BrowserSessionOptions
    (executablePath: string, browserArguments: IReadOnlyList<string>, startupTimeout: TimeSpan, commandTimeout: TimeSpan)
    =
    do
        ArgumentException.ThrowIfNullOrWhiteSpace executablePath
        ArgumentNullException.ThrowIfNull browserArguments

        if startupTimeout <= TimeSpan.Zero then
            invalidArg (nameof startupTimeout) "Browser startup timeout must be positive."

        if commandTimeout <= TimeSpan.Zero then
            invalidArg (nameof commandTimeout) "CDP command timeout must be positive."

    member _.ExecutablePath = executablePath
    member _.BrowserArguments = browserArguments
    member _.StartupTimeout = startupTimeout
    member _.CommandTimeout = commandTimeout

    override _.ToString() = executablePath

    new(executablePath: string, browserArguments: IReadOnlyList<string>) =
        BrowserSessionOptions(executablePath, browserArguments, TimeSpan.FromSeconds 10.0, TimeSpan.FromSeconds 10.0)

module private BrowserSessionInternals =
    let private diagnosticReadTimeout = TimeSpan.FromMilliseconds 500.0
    let private processExitTimeout = TimeSpan.FromSeconds 5.0

    let private conflictingArguments =
        [| "--remote-debugging-port"; "--remote-debugging-pipe"; "--user-data-dir" |]

    let validateBrowserArguments (arguments: IReadOnlyList<string>) =
        arguments
        |> Seq.tryFind (fun argument ->
            String.IsNullOrWhiteSpace argument
            || conflictingArguments
               |> Array.exists (fun required ->
                   argument.Equals(required, StringComparison.OrdinalIgnoreCase)
                   || argument.StartsWith(String.Concat(required, "="), StringComparison.OrdinalIgnoreCase)))
        |> function
            | Some argument when String.IsNullOrWhiteSpace argument ->
                invalidArg (nameof arguments) "Browser arguments must not contain empty values."
            | Some argument ->
                invalidArg
                    (nameof arguments)
                    (String.Concat(
                        "Browser argument '",
                        argument,
                        "' conflicts with the mandatory isolated CDP launch arguments."
                    ))
            | None -> ()

    let createStartInfo (options: BrowserSessionOptions) profilePath =
        let startInfo = ProcessStartInfo(options.ExecutablePath)
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.ArgumentList.Add "--headless=new"
        startInfo.ArgumentList.Add "--remote-debugging-port=0"
        startInfo.ArgumentList.Add(String.Concat("--user-data-dir=", profilePath))
        startInfo.ArgumentList.Add "--no-first-run"
        startInfo.ArgumentList.Add "--no-default-browser-check"

        for argument in options.BrowserArguments do
            startInfo.ArgumentList.Add argument

        startInfo.ArgumentList.Add "about:blank"
        startInfo

    let readProcessDiagnosticsAsync (standardError: Task<string>) (standardOutput: Task<string>) =
        task {
            try
                let! diagnostics = Task.WhenAll([| standardError; standardOutput |]).WaitAsync diagnosticReadTimeout

                let errorText = diagnostics[0]
                let outputText = diagnostics[1]

                if not (String.IsNullOrWhiteSpace errorText) then
                    return Ok(errorText.Trim())
                elif not (String.IsNullOrWhiteSpace outputText) then
                    return Ok(outputText.Trim())
                else
                    return Ok "The browser produced no diagnostic output."
            with
            | :? TimeoutException ->
                return
                    Error(
                        String.Concat(
                            "Browser diagnostic streams did not close within ",
                            diagnosticReadTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                            " ms."
                        )
                    )
            | error -> return Error(String.Concat("Failed to read browser diagnostics: ", error.Message))
        }

    let private invariantInt (value: int) =
        value.ToString(CultureInfo.InvariantCulture)

    let waitForDevToolsPortAsync
        (browserProcess: Process)
        (profilePath: string)
        (standardError: Task<string>)
        (standardOutput: Task<string>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        =
        task {
            let activePortPath = Path.Combine(profilePath, "DevToolsActivePort")

            use timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            timeoutCancellation.CancelAfter timeout

            try
                let mutable port = None

                while port.IsNone do
                    if browserProcess.HasExited then
                        let! diagnosticsResult = readProcessDiagnosticsAsync standardError standardOutput

                        let diagnostics =
                            match diagnosticsResult with
                            | Ok value -> value
                            | Error diagnosticError -> diagnosticError

                        raise (
                            InvalidOperationException(
                                String.Concat("Browser exited before DevToolsActivePort was available: ", diagnostics)
                            )
                        )

                    if File.Exists activePortPath then
                        let lines = File.ReadAllLines activePortPath

                        if lines.Length >= 2 then
                            match Int32.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture) with
                            | true, parsed when parsed > 0 -> port <- Some parsed
                            | _ ->
                                raise (
                                    InvalidDataException(
                                        String.Concat("DevToolsActivePort contained an invalid port: ", lines[0])
                                    )
                                )

                    if port.IsNone then
                        do! Task.Delay(50, timeoutCancellation.Token)

                return port.Value
            with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                return
                    raise (
                        TimeoutException(
                            String.Concat(
                                "Browser did not create DevToolsActivePort within ",
                                timeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                                " ms. Profile: ",
                                profilePath
                            )
                        )
                    )
        }

    let findPageEndpointAsync (port: int) (timeout: TimeSpan) (cancellationToken: CancellationToken) =
        task {
            use httpClient = new HttpClient()

            use timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            timeoutCancellation.CancelAfter timeout

            let targetListUri =
                Uri(String.Concat("http://127.0.0.1:", invariantInt port, "/json/list"))

            try
                let mutable endpoint = None

                while endpoint.IsNone do
                    let! json = httpClient.GetStringAsync(targetListUri, timeoutCancellation.Token)
                    let targets = CdpJsonModels.DeserializeTargets json

                    endpoint <-
                        targets
                        |> Seq.tryFind (fun target ->
                            String.Equals(target.Type, "page", StringComparison.Ordinal)
                            && not (String.IsNullOrWhiteSpace target.WebSocketDebuggerUrl))
                        |> Option.map (fun target -> Uri target.WebSocketDebuggerUrl)

                    if endpoint.IsNone then
                        do! Task.Delay(50, timeoutCancellation.Token)

                return endpoint.Value
            with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                return
                    raise (
                        TimeoutException(
                            String.Concat(
                                "No page target appeared at ",
                                targetListUri.AbsoluteUri,
                                " within ",
                                timeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                                " ms."
                            )
                        )
                    )
        }

    let deleteProfileAsync profilePath =
        task {
            let mutable lastError = None
            let mutable attempt = 0

            while Directory.Exists profilePath && attempt < 5 do
                attempt <- attempt + 1

                try
                    Directory.Delete(profilePath, true)
                    lastError <- None
                with error ->
                    lastError <- Some error

                    if attempt < 5 then
                        do! Task.Delay 100

            match lastError with
            | Some error when Directory.Exists profilePath ->
                return Some(String.Concat("Failed to remove browser profile '", profilePath, "': ", error.Message))
            | _ -> return None
        }

    let cleanupProcessAsync (browserProcess: Process) =
        task {
            try
                if not browserProcess.HasExited then
                    browserProcess.Kill true

                use waitCancellation = new CancellationTokenSource(processExitTimeout)
                do! browserProcess.WaitForExitAsync waitCancellation.Token
                return true, None
            with error ->
                let processExited =
                    try
                        browserProcess.HasExited
                    with _ ->
                        false

                return
                    processExited,
                    Some(
                        String.Concat(
                            "Failed to terminate browser process ",
                            browserProcess.Id.ToString(CultureInfo.InvariantCulture),
                            ": ",
                            error.Message
                        )
                    )
        }

type BrowserSession
    private
    (
        browserProcess: Process,
        profilePath: string,
        client: CdpClient,
        standardOutput: Task<string>,
        standardError: Task<string>
    ) as this =
    let mutable disposed = 0

    member _.ProfilePath = profilePath
    member _.ProcessId = browserProcess.Id
    member _.IsDisposed = Volatile.Read(&disposed) <> 0

    member private _.DisposeCoreAsync() =
        task {
            if Interlocked.Exchange(&disposed, 1) = 0 then
                let failures = ResizeArray<string>()

                try
                    do! (client :> IAsyncDisposable).DisposeAsync().AsTask()
                with error ->
                    failures.Add(String.Concat("Failed to close CDP: ", error.Message))

                let! processExited, processFailure = BrowserSessionInternals.cleanupProcessAsync browserProcess
                processFailure |> Option.iter failures.Add

                if processExited then
                    let! diagnosticsResult =
                        BrowserSessionInternals.readProcessDiagnosticsAsync standardError standardOutput

                    match diagnosticsResult with
                    | Ok _ -> ()
                    | Error diagnosticError -> failures.Add diagnosticError

                try
                    browserProcess.Dispose()
                with error ->
                    failures.Add(String.Concat("Failed to dispose browser process: ", error.Message))

                let! profileFailure = BrowserSessionInternals.deleteProfileAsync profilePath
                profileFailure |> Option.iter failures.Add

                if failures.Count > 0 then
                    raise (InvalidOperationException(String.Join(" ", failures)))
        }

    member private this.RunAsync<'T>(operationName: string, operation: unit -> Task<'T>) =
        task {
            try
                return! operation ()
            with error ->
                let mutable cleanupFailure = None

                try
                    do! this.DisposeCoreAsync()
                with cleanupError ->
                    cleanupFailure <- Some cleanupError

                let message =
                    match cleanupFailure with
                    | None ->
                        String.Concat(
                            "Browser session operation '",
                            operationName,
                            "' failed; browser process and profile were cleaned up."
                        )
                    | Some cleanupError ->
                        String.Concat(
                            "Browser session operation '",
                            operationName,
                            "' failed and cleanup also failed: ",
                            cleanupError.Message
                        )

                return raise (BrowserSessionException(message, error))
        }

    member this.NavigateAsync(url: Uri, cancellationToken: CancellationToken) =
        this.RunAsync("navigate", fun () -> client.NavigateAsync(url, cancellationToken))

    member this.EvaluateAsync(expression: string, cancellationToken: CancellationToken) =
        this.RunAsync("evaluate", fun () -> client.EvaluateAsync(expression, cancellationToken))

    member this.ConfigureEmulationAsync
        (
            width: int,
            height: int,
            deviceScaleFactor: double,
            mobile: bool,
            touch: bool,
            cancellationToken: CancellationToken
        ) =
        this.RunAsync(
            "configure emulation",
            fun () -> client.ConfigureEmulationAsync(width, height, deviceScaleFactor, mobile, touch, cancellationToken)
        )

    member this.SetTransparentBackgroundAsync(cancellationToken: CancellationToken) =
        this.RunAsync("set transparent background", fun () -> client.SetTransparentBackgroundAsync cancellationToken)

    member this.TouchAsync(x: double, y: double, cancellationToken: CancellationToken) =
        this.RunAsync("dispatch touch", fun () -> client.TouchAsync(x, y, cancellationToken))

    member this.CapturePngAsync(cancellationToken: CancellationToken) =
        this.RunAsync("capture PNG", fun () -> client.CapturePngAsync cancellationToken)

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(this.DisposeCoreAsync())

    static member LaunchAsync(options: BrowserSessionOptions, cancellationToken: CancellationToken) =
        task {
            ArgumentNullException.ThrowIfNull options
            BrowserSessionInternals.validateBrowserArguments options.BrowserArguments

            if not (File.Exists options.ExecutablePath) then
                invalidArg
                    (nameof options)
                    (String.Concat("Browser executable does not exist: ", options.ExecutablePath))

            let profilePath =
                Path.Combine(Path.GetTempPath(), String.Concat("viset-browser-", Guid.NewGuid().ToString("N")))

            Directory.CreateDirectory profilePath |> ignore
            let mutable browserProcess = None
            let mutable client = None
            let mutable standardOutput = Task.FromResult String.Empty
            let mutable standardError = Task.FromResult String.Empty

            try
                let started =
                    Process.Start(BrowserSessionInternals.createStartInfo options profilePath)
                    |> Option.ofObj
                    |> Option.defaultWith (fun () ->
                        raise (InvalidOperationException "The browser process could not be started."))

                browserProcess <- Some started
                standardOutput <- started.StandardOutput.ReadToEndAsync()
                standardError <- started.StandardError.ReadToEndAsync()

                let! port =
                    BrowserSessionInternals.waitForDevToolsPortAsync
                        started
                        profilePath
                        standardError
                        standardOutput
                        options.StartupTimeout
                        cancellationToken

                let! endpoint =
                    BrowserSessionInternals.findPageEndpointAsync port options.StartupTimeout cancellationToken

                let! connected = CdpClient.ConnectAsync(endpoint, options.CommandTimeout, cancellationToken)
                client <- Some connected
                do! connected.EnablePageAndRuntimeAsync cancellationToken

                return BrowserSession(started, profilePath, connected, standardOutput, standardError)
            with error ->
                let failures = ResizeArray<string>()

                match client with
                | Some connected ->
                    try
                        do! (connected :> IAsyncDisposable).DisposeAsync().AsTask()
                    with cleanupError ->
                        failures.Add(String.Concat("Failed to close CDP: ", cleanupError.Message))
                | None -> ()

                match browserProcess with
                | Some started ->
                    let! processExited, processFailure = BrowserSessionInternals.cleanupProcessAsync started
                    processFailure |> Option.iter failures.Add

                    if processExited then
                        let! diagnosticsResult =
                            BrowserSessionInternals.readProcessDiagnosticsAsync standardError standardOutput

                        match diagnosticsResult with
                        | Ok _ -> ()
                        | Error diagnosticError -> failures.Add diagnosticError

                    try
                        started.Dispose()
                    with cleanupError ->
                        failures.Add(String.Concat("Failed to dispose browser process: ", cleanupError.Message))
                | None -> ()

                let! profileFailure = BrowserSessionInternals.deleteProfileAsync profilePath
                profileFailure |> Option.iter failures.Add

                let message =
                    if failures.Count = 0 then
                        String.Concat(
                            "Browser launch failed; process and temporary profile were cleaned up. Profile: ",
                            profilePath
                        )
                    else
                        String.Concat(
                            "Browser launch failed and cleanup also failed: ",
                            String.Join(" ", failures),
                            " Profile: ",
                            profilePath
                        )

                return raise (BrowserSessionException(message, error))
        }
