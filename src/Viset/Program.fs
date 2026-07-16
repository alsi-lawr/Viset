namespace Viset

open System
open System.Globalization
open System.Threading

module Program =
    let private writeErrors errors =
        errors
        |> List.iter (fun message -> Console.Error.WriteLine(String.Concat("error: ", message)))

    let private writePlan (plan: CapturePlan) =
        Console.Out.WriteLine(String.Concat("output: ", plan.OutputPath))

        Console.Out.WriteLine(String.Concat("captures: ", plan.Captures.Length.ToString(CultureInfo.InvariantCulture)))

        plan.Captures
        |> List.iter (fun capture ->
            Console.Out.WriteLine(String.Concat(capture.Format.ToString(), ": ", capture.OutputRelativePath)))

    let private percentile percentileValue (durations: TimeSpan list) =
        match durations |> List.sort with
        | [] -> TimeSpan.Zero
        | sorted ->
            let index =
                int (Math.Ceiling(percentileValue * double sorted.Length)) - 1
                |> max 0
                |> min (sorted.Length - 1)

            sorted[index]

    let private writePerformance framesPerSecond (output: CaptureOutputResult) =
        match output.Performance with
        | None -> ()
        | Some metrics ->
            let p95 = percentile 0.95 metrics.CaptureDurations
            let p99 = percentile 0.99 metrics.CaptureDurations
            let maximum = metrics.CaptureDurations |> List.fold max TimeSpan.Zero

            Console.Out.WriteLine(
                String.Concat(
                    "metrics: ",
                    output.Path,
                    " backend=",
                    metrics.Backend.ToString(),
                    " frames=",
                    metrics.FrameCount.ToString(CultureInfo.InvariantCulture),
                    " unique=",
                    metrics.UniqueFrameCount.ToString(CultureInfo.InvariantCulture),
                    " p95_ms=",
                    p95.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture),
                    " p99_ms=",
                    p99.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture),
                    " max_ms=",
                    maximum.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture),
                    " missed=",
                    metrics.MissedSlots.ToString(CultureInfo.InvariantCulture),
                    " duplicated=",
                    metrics.DuplicatedFrames.ToString(CultureInfo.InvariantCulture),
                    " dropped=",
                    metrics.DroppedFrames.ToString(CultureInfo.InvariantCulture)
                )
            )

            let budget = TimeSpan.FromSeconds(1.0 / double framesPerSecond)

            let acquisitionOverruns =
                metrics.CaptureDurations |> List.filter (fun duration -> duration > budget)

            if not (List.isEmpty acquisitionOverruns) || metrics.MissedSlots > 0 then
                Console.Error.WriteLine(
                    String.Concat(
                        "warning: frame acquisition exceeded the ",
                        budget.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture),
                        " ms budget for ",
                        output.Path,
                        ": overruns=",
                        acquisitionOverruns.Length.ToString(CultureInfo.InvariantCulture),
                        ", missed=",
                        metrics.MissedSlots.ToString(CultureInfo.InvariantCulture),
                        ", duplicated=",
                        metrics.DuplicatedFrames.ToString(CultureInfo.InvariantCulture),
                        ", dropped=",
                        metrics.DroppedFrames.ToString(CultureInfo.InvariantCulture),
                        ", p95_ms=",
                        p95.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture),
                        ", p99_ms=",
                        p99.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture),
                        ", max_ms=",
                        maximum.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)
                    )
                )

            let animationOverruns =
                output.AnimationUpdateDurations
                |> List.filter (fun duration -> duration > budget)

            if not (List.isEmpty animationOverruns) then
                let animationP95 = percentile 0.95 output.AnimationUpdateDurations
                let animationP99 = percentile 0.99 output.AnimationUpdateDurations
                let animationMax = output.AnimationUpdateDurations |> List.fold max TimeSpan.Zero

                Console.Error.WriteLine(
                    String.Concat(
                        "warning: page.animate updates exceeded the ",
                        budget.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture),
                        " ms budget for ",
                        output.Path,
                        ": overruns=",
                        animationOverruns.Length.ToString(CultureInfo.InvariantCulture),
                        ", p95_ms=",
                        animationP95.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture),
                        ", p99_ms=",
                        animationP99.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture),
                        ", max_ms=",
                        animationMax.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)
                    )
                )

    let private installBrowser () =
        let sidecar = BrowserInstall.findBrowserLockSidecar AppContext.BaseDirectory

        match
            BrowserInstall.installAsync sidecar CancellationToken.None
            |> fun work -> work.GetAwaiter().GetResult()
        with
        | Error message ->
            writeErrors [ message ]
            3
        | Ok browser ->
            Console.Out.WriteLine(String.Concat("installed browser: ", browser.ExecutablePath))
            Console.Out.WriteLine(String.Concat("version: ", browser.Version))
            0

    let private initializeProject request =
        match Scaffold.run request with
        | Error message ->
            writeErrors [ message ]
            1
        | Ok result ->
            Console.Out.WriteLine(String.Concat("initialized: ", result.DirectoryPath))
            Console.Out.WriteLine(String.Concat("next: viset capture ", result.CapturePath))
            0

    let private capture (plan: CapturePlan) =
        use cancellation = new CancellationTokenSource()

        let cancelHandler =
            ConsoleCancelEventHandler(fun _ arguments ->
                arguments.Cancel <- true
                cancellation.Cancel())

        Console.CancelKeyPress.AddHandler cancelHandler

        try
            writePlan plan
            let sidecar = BrowserInstall.findBrowserLockSidecar AppContext.BaseDirectory

            match
                BrowserResolution.resolveAsync plan.BrowserPath sidecar cancellation.Token
                |> fun work -> work.GetAwaiter().GetResult()
            with
            | Error message ->
                writeErrors [ message ]
                3
            | Ok browser ->
                try
                    let result =
                        LuaHost.runAsync Cli.version plan browser cancellation.Token
                        |> fun work -> work.GetAwaiter().GetResult()

                    for output in result.Outputs do
                        Console.Out.WriteLine(String.Concat("written: ", output.Path))
                        writePerformance plan.FramesPerSecond output

                    0
                with
                | :? OperationCanceledException ->
                    writeErrors [ "Capture was cancelled." ]
                    130
                | error ->
                    writeErrors [ error.Message ]
                    1
        finally
            Console.CancelKeyPress.RemoveHandler cancelHandler

    [<EntryPoint>]
    let main arguments =
        match Cli.parse Environment.CurrentDirectory arguments with
        | Error message ->
            writeErrors [ message ]
            2
        | Ok Help ->
            Console.Out.WriteLine Cli.usage
            0
        | Ok Version ->
            Console.Out.WriteLine Cli.versionText
            0
        | Ok(Init request) -> initializeProject request
        | Ok BrowserInstall -> installBrowser ()
        | Ok(Capture request) ->
            match CaptureScript.plan request with
            | Error errors ->
                writeErrors errors
                2
            | Ok plan -> capture plan
