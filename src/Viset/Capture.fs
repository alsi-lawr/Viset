namespace Viset

open System
open System.IO
open System.Threading
open System.Threading.Tasks

type CaptureSession
    private
    (primarySession: BrowserSession, browserOptions: BrowserSessionOptions, device: Device, framePath: string option) =
    let mutable frameBrowser: BrowserSession option = None
    let mutable frameRenderer: FrameRenderer option = None
    let mutable disposed = 0

    member _.Page = primarySession

    member _.CapturePngAsync(cancellationToken: CancellationToken) =
        task {
            let! raw = primarySession.CapturePngAsync cancellationToken
            Media.validatePng raw |> ignore

            match framePath with
            | None -> return raw
            | Some path ->
                let! renderer =
                    task {
                        match frameRenderer with
                        | Some renderer ->
                            do! renderer.UpdateAsync(raw, cancellationToken)
                            return renderer
                        | None ->
                            let! browser = BrowserSession.LaunchAsync(browserOptions, cancellationToken)

                            try
                                let! renderer =
                                    FrameRenderer.StartAsync(
                                        browser,
                                        path,
                                        device,
                                        raw,
                                        browserOptions.CommandTimeout,
                                        cancellationToken
                                    )

                                frameBrowser <- Some browser
                                frameRenderer <- Some renderer
                                return renderer
                            with error ->
                                do! (browser :> IAsyncDisposable).DisposeAsync().AsTask()
                                return raise error
                    }

                let! framed = renderer.CapturePngAsync cancellationToken
                Media.validatePng framed |> ignore
                return framed
        }

    member private _.DisposeCoreAsync() =
        task {
            if Interlocked.Exchange(&disposed, 1) = 0 then
                let failures = ResizeArray<string>()

                match frameRenderer with
                | Some renderer ->
                    try
                        do! (renderer :> IAsyncDisposable).DisposeAsync().AsTask()
                    with error ->
                        failures.Add(String.Concat("Failed to stop frame server: ", error.Message))
                | None -> ()

                match frameBrowser with
                | Some browser ->
                    try
                        do! (browser :> IAsyncDisposable).DisposeAsync().AsTask()
                    with error ->
                        failures.Add(String.Concat("Failed to stop frame browser: ", error.Message))
                | None -> ()

                try
                    do! (primarySession :> IAsyncDisposable).DisposeAsync().AsTask()
                with error ->
                    failures.Add(String.Concat("Failed to stop capture browser: ", error.Message))

                if failures.Count > 0 then
                    raise (InvalidOperationException(String.Join(" ", failures)))
        }

    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask(this.DisposeCoreAsync())

    static member LaunchAsync
        (
            browserOptions: BrowserSessionOptions,
            device: Device,
            framePath: string option,
            cancellationToken: CancellationToken
        ) =
        task {
            ArgumentNullException.ThrowIfNull browserOptions

            framePath
            |> Option.iter (fun path ->
                if String.IsNullOrWhiteSpace path then
                    invalidArg (nameof framePath) "Frame path must not be empty."

                if not (File.Exists path) then
                    invalidArg (nameof framePath) (String.Concat("Frame HTML does not exist: ", path))

                if device.Frame.IsNone then
                    invalidArg (nameof device) "A framed capture requires device frame dimensions.")

            let! browser = BrowserSession.LaunchAsync(browserOptions, cancellationToken)

            try
                do!
                    browser.ConfigureEmulationAsync(
                        device.Viewport.Width,
                        device.Viewport.Height,
                        device.DeviceScale,
                        device.Mobile,
                        device.Touch,
                        cancellationToken
                    )

                do! browser.SetTransparentBackgroundAsync cancellationToken
                return CaptureSession(browser, browserOptions, device, framePath)
            with error ->
                do! (browser :> IAsyncDisposable).DisposeAsync().AsTask()
                return raise error
        }
