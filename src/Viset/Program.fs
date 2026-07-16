namespace Viset

open System
open System.Globalization
open System.Threading

module Program =
    let private writeErrors errors =
        errors
        |> List.iter (fun message -> Console.Error.WriteLine(String.Concat("error: ", message)))

    let private writePlan (plan: CapturePlan) =
        plan.Warnings
        |> List.iter (fun warning -> Console.Error.WriteLine(String.Concat("warning: ", warning)))

        Console.Out.WriteLine(String.Concat("output: ", plan.OutputPath))

        Console.Out.WriteLine(String.Concat("captures: ", plan.Captures.Length.ToString(CultureInfo.InvariantCulture)))

        plan.Captures
        |> List.iter (fun capture ->
            let kind =
                match capture.Kind with
                | Still -> "still"
                | Animation _ -> "animation"

            Console.Out.WriteLine(String.Concat(kind, ": ", capture.OutputRelativePath)))

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

                    Console.Out.WriteLine(String.Concat("manifest: ", result.ManifestPath))
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
        | Ok BrowserInstall -> installBrowser ()
        | Ok(Capture request) ->
            match Matrix.plan request with
            | Error errors ->
                writeErrors errors
                2
            | Ok plan -> capture plan
