namespace Viset

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Lua
open Lua.Standard

type private AdapterModuleLoader(adapterDirectory: string) =
    let root = Path.GetFullPath adapterDirectory

    let comparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let modulePath moduleName =
        if String.IsNullOrWhiteSpace moduleName then
            None
        else
            let relative =
                String.Concat(moduleName.Replace('.', Path.DirectorySeparatorChar), ".lua")

            let candidate = Path.GetFullPath(relative, root)

            let prefix =
                String.Concat(root.TrimEnd(Path.DirectorySeparatorChar), Path.DirectorySeparatorChar)

            if candidate.StartsWith(prefix, comparison) then
                Some candidate
            else
                None

    interface ILuaModuleLoader with
        member _.Exists(moduleName) =
            modulePath moduleName |> Option.exists File.Exists

        member _.LoadAsync(moduleName, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()

            match modulePath moduleName with
            | Some path when File.Exists path -> ValueTask<LuaModule>(new LuaModule(moduleName, File.ReadAllBytes path))
            | _ -> ValueTask.FromException<LuaModule>(LuaModuleNotFoundException moduleName)

type private ManagedProcess(childProcess: Process, standardOutput: Task<string>, standardError: Task<string>) =
    member _.Process = childProcess
    member _.StandardOutput = standardOutput
    member _.StandardError = standardError

type private ActiveCase =
    { Planned: PlannedCapture
      Session: CaptureSession
      Captures: ResizeArray<byte array> }

    override activeCase.ToString() = activeCase.Planned.LogicalName

module private LuaHostInternals =
    let setValue (table: LuaTable) (key: string) (value: LuaValue) = table[LuaValue key] <- value

    let getValue (table: LuaTable) (key: string) = table[LuaValue key]

    let tryRead<'T> (value: LuaValue) =
        let mutable result = Unchecked.defaultof<'T>

        if value.TryRead<'T>(&result) then Some result else None

    let requiredString (table: LuaTable) (key: string) =
        match getValue table key |> tryRead<string> with
        | Some value when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> invalidArg key (String.Concat(key, " is required and must be a non-empty string."))

    let optionalString (table: LuaTable) (key: string) =
        match getValue table key with
        | value when value.Type = LuaValueType.Nil -> None
        | value ->
            match tryRead<string> value with
            | Some text when not (String.IsNullOrWhiteSpace text) -> Some text
            | _ -> invalidArg key (String.Concat(key, " must be a non-empty string."))

    let optionalNumber (table: LuaTable) (key: string) (defaultValue: double) =
        match getValue table key with
        | value when value.Type = LuaValueType.Nil -> defaultValue
        | value ->
            match tryRead<double> value with
            | Some number when Double.IsFinite number -> number
            | _ -> invalidArg key (String.Concat(key, " must be a finite number."))

    let numberToInt (label: string) (value: double) =
        if
            not (Double.IsFinite value)
            || value < double Int32.MinValue
            || value > double Int32.MaxValue
            || Math.Truncate value <> value
        then
            invalidArg label (String.Concat(label, " must be an integer."))

        int value

    let resultTable (values: (string * LuaValue) list) =
        let table = LuaTable()
        setValue table "ok" (LuaValue true)
        values |> List.iter (fun (key, value) -> setValue table key value)
        LuaValue table

    let errorTable (code: string) (message: string) =
        let error = LuaTable()
        setValue error "code" (LuaValue code)
        setValue error "message" (LuaValue message)
        let result = LuaTable()
        setValue result "ok" (LuaValue false)
        setValue result "error" (LuaValue error)
        LuaValue result

    let hostFunction name operation =
        new LuaFunction(
            name,
            Func<LuaFunctionExecutionContext, CancellationToken, ValueTask<int>>(fun context cancellationToken ->
                ValueTask<int>(
                    task {
                        try
                            return! operation context cancellationToken
                        with
                        | :? OperationCanceledException -> return raise (OperationCanceledException cancellationToken)
                        | error -> return context.Return(errorTable "host_error" error.Message)
                    }
                ))
        )

    let rec tomlValue value =
        match value with
        | TomlValue.String text -> LuaValue text
        | TomlValue.Integer number -> LuaValue(double number)
        | TomlValue.Float number -> LuaValue number
        | TomlValue.Boolean flag -> LuaValue flag
        | TomlValue.DateTime text -> LuaValue text
        | TomlValue.Array values ->
            let table = LuaTable(values.Length, 0)

            values
            |> List.iteri (fun index item -> table[LuaValue(double (index + 1))] <- tomlValue item)

            LuaValue table
        | TomlValue.Table values ->
            let table = LuaTable(0, values.Length)
            values |> List.iter (fun (key, item) -> setValue table key (tomlValue item))
            LuaValue table

    let rec jsonValue (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Null
        | JsonValueKind.Undefined -> LuaValue.Nil
        | JsonValueKind.True -> LuaValue true
        | JsonValueKind.False -> LuaValue false
        | JsonValueKind.String -> LuaValue(element.GetString() |> Option.ofObj |> Option.defaultValue String.Empty)
        | JsonValueKind.Number -> LuaValue(element.GetDouble())
        | JsonValueKind.Array ->
            let values = element.EnumerateArray() |> Seq.toArray
            let table = LuaTable(values.Length, 0)

            values
            |> Array.iteri (fun index value -> table[LuaValue(double (index + 1))] <- jsonValue value)

            LuaValue table
        | JsonValueKind.Object ->
            let properties = element.EnumerateObject() |> Seq.toArray
            let table = LuaTable(0, properties.Length)

            properties
            |> Array.iter (fun property -> setValue table property.Name (jsonValue property.Value))

            LuaValue table
        | kind -> invalidOp (String.Concat("Unsupported JSON value kind: ", kind.ToString()))

    let evaluationValue value =
        match value with
        | CdpEvaluationValue.Undefined
        | CdpEvaluationValue.Null -> LuaValue.Nil
        | CdpEvaluationValue.Boolean flag -> LuaValue flag
        | CdpEvaluationValue.Number number -> LuaValue number
        | CdpEvaluationValue.String text -> LuaValue text
        | CdpEvaluationValue.Json json -> jsonValue json

    let dimensionsTable dimensions =
        let table = LuaTable()
        setValue table "width" (LuaValue(double dimensions.Width))
        setValue table "height" (LuaValue(double dimensions.Height))
        table

    let deviceTable device =
        let table = LuaTable()
        setValue table "name" (LuaValue device.Name)
        setValue table "mobile" (LuaValue device.Mobile)
        setValue table "touch" (LuaValue device.Touch)
        setValue table "device_scale" (LuaValue device.DeviceScale)
        setValue table "viewport" (LuaValue(dimensionsTable device.Viewport))

        match device.Frame with
        | Some frame -> setValue table "frame" (LuaValue(dimensionsTable frame))
        | None -> setValue table "frame" LuaValue.Nil

        table

    let caseContext (plan: CapturePlan) (capture: PlannedCapture) =
        let table = LuaTable()
        setValue table "matrix_path" (LuaValue plan.MatrixPath)
        setValue table "adapter_path" (LuaValue plan.AdapterPath)
        setValue table "output_path" (LuaValue plan.OutputPath)
        setValue table "definition_id" (LuaValue capture.DefinitionId)
        setValue table "logical_name" (LuaValue capture.LogicalName)
        setValue table "device" (LuaValue(deviceTable capture.Device))

        match capture.Kind with
        | Still ->
            setValue table "kind" (LuaValue "still")
            setValue table "workflow" LuaValue.Nil
        | Animation workflow ->
            setValue table "kind" (LuaValue "animation")
            setValue table "workflow" (LuaValue workflow)

        let axes = LuaTable(0, capture.Axes.Length)

        capture.Axes
        |> List.iter (fun (key, value) -> setValue axes key (tomlValue value))

        setValue table "axes" (LuaValue axes)

        let data = LuaTable(0, capture.Data.Length)

        capture.Data
        |> List.iter (fun (key, value) -> setValue data key (tomlValue value))

        setValue table "data" (LuaValue data)
        table

    let readFunction (table: LuaTable) key =
        match getValue table key |> tryRead<LuaFunction> with
        | Some functionValue -> functionValue
        | None -> invalidOp (String.Concat("Adapter requires function '", key, "'."))

    let callAsync (state: LuaState) (functionValue: LuaFunction) (argument: LuaTable) cancellationToken =
        state.CallAsync(LuaValue functionValue, [| LuaValue argument |], cancellationToken).AsTask()

module LuaHost =
    let runAsync toolVersion (plan: CapturePlan) (browser: BrowserExecutable) (cancellationToken: CancellationToken) =
        task {
            ArgumentException.ThrowIfNullOrWhiteSpace toolVersion
            Output.validateRoot plan.OutputPath
            use state = LuaState.Create()
            state.OpenStandardLibraries()

            let adapterDirectory =
                Path.GetDirectoryName plan.AdapterPath
                |> Option.ofObj
                |> Option.defaultValue Environment.CurrentDirectory

            state.ModuleLoader <- AdapterModuleLoader(adapterDirectory)

            let processes = Dictionary<int, ManagedProcess>()
            let processLock = obj ()
            let mutable nextProcessHandle = 0
            let mutable activeCase: ActiveCase option = None
            use httpClient = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)

            let withActiveCase operation =
                match activeCase with
                | Some current -> operation current
                | None -> raise (InvalidOperationException "No capture case is active.")

            let removeProcess handle =
                lock processLock (fun () ->
                    match processes.TryGetValue handle with
                    | true, childProcess ->
                        processes.Remove handle |> ignore
                        Some childProcess
                    | false, _ -> None)

            let findProcess handle =
                lock processLock (fun () ->
                    match processes.TryGetValue handle with
                    | true, childProcess -> Some childProcess
                    | false, _ -> None)

            let stopProcessAsync handle cancellationToken =
                task {
                    match findProcess handle with
                    | None -> return LuaHostInternals.errorTable "unknown_process" "The process handle is not active."
                    | Some managed ->
                        let childProcess = managed.Process

                        try
                            if not childProcess.HasExited then
                                childProcess.Kill true

                            do! childProcess.WaitForExitAsync cancellationToken
                            let! standardOutput = managed.StandardOutput
                            let! standardError = managed.StandardError
                            let exitCode = childProcess.ExitCode
                            removeProcess handle |> Option.iter (fun value -> value.Process.Dispose())

                            return
                                LuaHostInternals.resultTable
                                    [ "exit_code", LuaValue(double exitCode)
                                      "stdout", LuaValue standardOutput
                                      "stderr", LuaValue standardError ]
                        with error ->
                            return LuaHostInternals.errorTable "process_stop_failed" error.Message
                }

            let cleanupProcessesAsync () =
                task {
                    let handles = lock processLock (fun () -> processes.Keys |> Seq.toArray)
                    let failures = ResizeArray<string>()

                    for handle in handles do
                        let! result = stopProcessAsync handle CancellationToken.None
                        let table = result.Read<LuaTable>()

                        if not (LuaHostInternals.getValue table "ok" |> fun value -> value.Read<bool>()) then
                            let error =
                                LuaHostInternals.getValue table "error" |> fun value -> value.Read<LuaTable>()

                            failures.Add(LuaHostInternals.requiredString error "message")

                    return List.ofSeq failures
                }

            let processStart =
                LuaHostInternals.hostFunction "viset.process.start" (fun context _ ->
                    task {
                        withActiveCase (fun _ -> ())
                        let options = context.GetArgument<LuaTable>(0)
                        let startInfo = ProcessStartInfo(LuaHostInternals.requiredString options "file")
                        startInfo.UseShellExecute <- false
                        startInfo.CreateNoWindow <- true
                        startInfo.RedirectStandardOutput <- true
                        startInfo.RedirectStandardError <- true

                        LuaHostInternals.optionalString options "working_directory"
                        |> Option.iter (fun directory -> startInfo.WorkingDirectory <- directory)

                        match
                            LuaHostInternals.getValue options "arguments"
                            |> LuaHostInternals.tryRead<LuaTable>
                        with
                        | Some arguments ->
                            for index in 1 .. arguments.ArrayLength do
                                startInfo.ArgumentList.Add(arguments[LuaValue(double index)].Read<string>())
                        | None -> ()

                        match
                            LuaHostInternals.getValue options "environment"
                            |> LuaHostInternals.tryRead<LuaTable>
                        with
                        | Some environment ->
                            for item in environment do
                                startInfo.Environment[item.Key.Read<string>()] <- item.Value.Read<string>()
                        | None -> ()

                        let childProcess =
                            Process.Start startInfo
                            |> Option.ofObj
                            |> Option.defaultWith (fun () ->
                                raise (InvalidOperationException "Process could not be started."))

                        let managed =
                            ManagedProcess(
                                childProcess,
                                childProcess.StandardOutput.ReadToEndAsync(),
                                childProcess.StandardError.ReadToEndAsync()
                            )

                        let handle =
                            lock processLock (fun () ->
                                nextProcessHandle <- nextProcessHandle + 1
                                processes.Add(nextProcessHandle, managed)
                                nextProcessHandle)

                        return
                            context.Return(
                                LuaHostInternals.resultTable
                                    [ "handle", LuaValue(double handle)
                                      "process_id", LuaValue(double childProcess.Id) ]
                            )
                    })

            let processWait =
                LuaHostInternals.hostFunction "viset.process.wait" (fun context cancellationToken ->
                    task {
                        let handle = context.GetArgument<double>(0) |> LuaHostInternals.numberToInt "handle"

                        let timeoutMilliseconds =
                            if context.HasArgument 1 then
                                context.GetArgument<double>(1)
                            else
                                30000.0

                        if not (Double.IsFinite timeoutMilliseconds) || timeoutMilliseconds <= 0.0 then
                            invalidArg "timeout_ms" "timeout_ms must be a positive finite number."

                        let managed = lock processLock (fun () -> processes.TryGetValue handle)

                        match managed with
                        | false, _ ->
                            return
                                context.Return(
                                    LuaHostInternals.errorTable "unknown_process" "The process handle is not active."
                                )
                        | true, childProcess ->
                            use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                            timeout.CancelAfter(TimeSpan.FromMilliseconds timeoutMilliseconds)

                            try
                                do! childProcess.Process.WaitForExitAsync timeout.Token
                                let! standardOutput = childProcess.StandardOutput
                                let! standardError = childProcess.StandardError
                                let exitCode = childProcess.Process.ExitCode
                                removeProcess handle |> Option.iter (fun value -> value.Process.Dispose())

                                return
                                    context.Return(
                                        LuaHostInternals.resultTable
                                            [ "exit_code", LuaValue(double exitCode)
                                              "stdout", LuaValue standardOutput
                                              "stderr", LuaValue standardError ]
                                    )
                            with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                                return
                                    context.Return(
                                        LuaHostInternals.errorTable
                                            "process_timeout"
                                            "The process did not exit before timeout_ms."
                                    )
                    })

            let processStop =
                LuaHostInternals.hostFunction "viset.process.stop" (fun context cancellationToken ->
                    task {
                        let handle = context.GetArgument<double>(0) |> LuaHostInternals.numberToInt "handle"
                        let! result = stopProcessAsync handle cancellationToken
                        return context.Return result
                    })

            let httpGet =
                LuaHostInternals.hostFunction "viset.http.get" (fun context cancellationToken ->
                    task {
                        let options = context.GetArgument<LuaTable>(0)
                        let uri = Uri(LuaHostInternals.requiredString options "url", UriKind.Absolute)

                        let timeoutMilliseconds =
                            LuaHostInternals.optionalNumber options "timeout_ms" 30000.0

                        if timeoutMilliseconds <= 0.0 then
                            invalidArg "timeout_ms" "timeout_ms must be positive."

                        use request = new HttpRequestMessage(HttpMethod.Get, uri)

                        match
                            LuaHostInternals.getValue options "headers"
                            |> LuaHostInternals.tryRead<LuaTable>
                        with
                        | Some headers ->
                            for item in headers do
                                request.Headers.TryAddWithoutValidation(
                                    item.Key.Read<string>(),
                                    item.Value.Read<string>()
                                )
                                |> ignore
                        | None -> ()

                        use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                        timeout.CancelAfter(TimeSpan.FromMilliseconds timeoutMilliseconds)

                        try
                            use! response = httpClient.SendAsync(request, timeout.Token)
                            let! body = response.Content.ReadAsStringAsync(timeout.Token)
                            let headers = LuaTable()

                            for header in response.Headers do
                                LuaHostInternals.setValue headers header.Key (LuaValue(String.Join(",", header.Value)))

                            for header in response.Content.Headers do
                                LuaHostInternals.setValue headers header.Key (LuaValue(String.Join(",", header.Value)))

                            return
                                context.Return(
                                    LuaHostInternals.resultTable
                                        [ "status", LuaValue(double (int response.StatusCode))
                                          "headers", LuaValue headers
                                          "body", LuaValue body ]
                                )
                        with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                            return
                                context.Return(
                                    LuaHostInternals.errorTable "http_timeout" "The HTTP request exceeded timeout_ms."
                                )
                    })

            let pageNavigate =
                LuaHostInternals.hostFunction "viset.page.navigate" (fun context cancellationToken ->
                    task {
                        let uri = Uri(context.GetArgument<string>(0), UriKind.Absolute)
                        do! withActiveCase (fun current -> current.Session.Page.NavigateAsync(uri, cancellationToken))
                        return context.Return(LuaHostInternals.resultTable [])
                    })

            let pageEvaluate =
                LuaHostInternals.hostFunction "viset.page.evaluate" (fun context cancellationToken ->
                    task {
                        let script = context.GetArgument<string>(0)

                        let! result =
                            withActiveCase (fun current ->
                                current.Session.Page.EvaluateAsync(script, cancellationToken))

                        match result with
                        | Ok value ->
                            return
                                context.Return(
                                    LuaHostInternals.resultTable [ "value", LuaHostInternals.evaluationValue value ]
                                )
                        | Error error ->
                            return context.Return(LuaHostInternals.errorTable "javascript_error" (error.ToString()))
                    })

            let pageWaitFor =
                LuaHostInternals.hostFunction "viset.page.wait_for" (fun context cancellationToken ->
                    task {
                        let script = context.GetArgument<string>(0)
                        let timeoutMilliseconds = context.GetArgument<double>(1)

                        if not (Double.IsFinite timeoutMilliseconds) || timeoutMilliseconds <= 0.0 then
                            invalidArg "timeout_ms" "timeout_ms must be a positive finite number."

                        use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                        timeout.CancelAfter(TimeSpan.FromMilliseconds timeoutMilliseconds)
                        let mutable ready = false
                        let mutable evaluationError = None

                        try
                            while not ready && evaluationError.IsNone do
                                let! result =
                                    withActiveCase (fun current ->
                                        current.Session.Page.EvaluateAsync(script, timeout.Token))

                                match result with
                                | Ok(CdpEvaluationValue.Boolean value) -> ready <- value
                                | Ok _ -> ready <- false
                                | Error error -> evaluationError <- Some(error.ToString())

                                if not ready && evaluationError.IsNone then
                                    do! Task.Delay(20, timeout.Token)

                            match evaluationError with
                            | Some message ->
                                return context.Return(LuaHostInternals.errorTable "javascript_error" message)
                            | None -> return context.Return(LuaHostInternals.resultTable [])
                        with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                            return
                                context.Return(
                                    LuaHostInternals.errorTable
                                        "wait_timeout"
                                        "The expression did not become true before timeout_ms."
                                )
                    })

            let emulationApply =
                LuaHostInternals.hostFunction "viset.emulation.apply" (fun context cancellationToken ->
                    task {
                        let device = context.GetArgument<LuaTable>(0)

                        let viewport =
                            LuaHostInternals.getValue device "viewport"
                            |> fun value -> value.Read<LuaTable>()

                        let width =
                            LuaHostInternals.getValue viewport "width"
                            |> fun value -> value.Read<double>() |> LuaHostInternals.numberToInt "width"

                        let height =
                            LuaHostInternals.getValue viewport "height"
                            |> fun value -> value.Read<double>() |> LuaHostInternals.numberToInt "height"

                        let scale = LuaHostInternals.optionalNumber device "device_scale" 1.0

                        let mobile =
                            LuaHostInternals.getValue device "mobile"
                            |> fun value ->
                                if value.Type = LuaValueType.Nil then
                                    false
                                else
                                    value.Read<bool>()

                        let touch =
                            LuaHostInternals.getValue device "touch"
                            |> fun value ->
                                if value.Type = LuaValueType.Nil then
                                    false
                                else
                                    value.Read<bool>()

                        do!
                            withActiveCase (fun current ->
                                current.Session.Page.ConfigureEmulationAsync(
                                    width,
                                    height,
                                    scale,
                                    mobile,
                                    touch,
                                    cancellationToken
                                ))

                        return context.Return(LuaHostInternals.resultTable [])
                    })

            let emulationTouch =
                LuaHostInternals.hostFunction "viset.emulation.touch" (fun context cancellationToken ->
                    task {
                        let x = context.GetArgument<double>(0)
                        let y = context.GetArgument<double>(1)
                        do! withActiveCase (fun current -> current.Session.Page.TouchAsync(x, y, cancellationToken))
                        return context.Return(LuaHostInternals.resultTable [])
                    })

            let capture kindName expectedKind =
                LuaHostInternals.hostFunction
                    (String.Concat("viset.capture.", kindName))
                    (fun context cancellationToken ->
                        task {
                            let! index =
                                withActiveCase (fun current ->
                                    task {
                                        let validKind =
                                            match expectedKind, current.Planned.Kind with
                                            | "still", Still -> true
                                            | "frame", Animation _ -> true
                                            | _ -> false

                                        if not validKind then
                                            invalidOp (
                                                String.Concat(
                                                    "viset.capture.",
                                                    kindName,
                                                    " is not valid for this definition."
                                                )
                                            )

                                        if expectedKind = "still" && current.Captures.Count > 0 then
                                            invalidOp "A still definition may capture exactly one still."

                                        let! bytes = current.Session.CapturePngAsync cancellationToken
                                        current.Captures.Add bytes
                                        return current.Captures.Count
                                    })

                            return context.Return(LuaHostInternals.resultTable [ "capture", LuaValue(double index) ])
                        })

            let processTable = LuaTable()
            LuaHostInternals.setValue processTable "start" (LuaValue processStart)
            LuaHostInternals.setValue processTable "wait" (LuaValue processWait)
            LuaHostInternals.setValue processTable "stop" (LuaValue processStop)

            let httpTable = LuaTable()
            LuaHostInternals.setValue httpTable "get" (LuaValue httpGet)

            let pageTable = LuaTable()
            LuaHostInternals.setValue pageTable "navigate" (LuaValue pageNavigate)
            LuaHostInternals.setValue pageTable "evaluate" (LuaValue pageEvaluate)
            LuaHostInternals.setValue pageTable "wait_for" (LuaValue pageWaitFor)

            let emulationTable = LuaTable()
            LuaHostInternals.setValue emulationTable "apply" (LuaValue emulationApply)
            LuaHostInternals.setValue emulationTable "touch" (LuaValue emulationTouch)

            let captureTable = LuaTable()
            LuaHostInternals.setValue captureTable "still" (LuaValue(capture "still" "still"))
            LuaHostInternals.setValue captureTable "frame" (LuaValue(capture "frame" "frame"))

            let visetTable = LuaTable()
            LuaHostInternals.setValue visetTable "api_version" (LuaValue 1.0)
            LuaHostInternals.setValue visetTable "process" (LuaValue processTable)
            LuaHostInternals.setValue visetTable "http" (LuaValue httpTable)
            LuaHostInternals.setValue visetTable "page" (LuaValue pageTable)
            LuaHostInternals.setValue visetTable "emulation" (LuaValue emulationTable)
            LuaHostInternals.setValue visetTable "capture" (LuaValue captureTable)
            state.Environment[LuaValue "viset"] <- LuaValue visetTable

            let! adapterResults = state.DoFileAsync(plan.AdapterPath, cancellationToken).AsTask()

            if adapterResults.Length <> 1 then
                invalidOp "Adapter must return exactly one table."

            let adapter = adapterResults[0].Read<LuaTable>()
            let prepare = LuaHostInternals.readFunction adapter "prepare"
            let start = LuaHostInternals.readFunction adapter "start"
            let ready = LuaHostInternals.readFunction adapter "ready"
            let openCase = LuaHostInternals.readFunction adapter "open"
            let stop = LuaHostInternals.readFunction adapter "stop"

            let workflows =
                match
                    LuaHostInternals.getValue adapter "workflows"
                    |> LuaHostInternals.tryRead<LuaTable>
                with
                | Some table -> table
                | None -> LuaTable()

            let browserOptions =
                BrowserSessionOptions(browser.ExecutablePath, plan.BrowserArguments)

            let outputs = ResizeArray<CapturedFile>()

            for planned in plan.Captures do
                let! session =
                    CaptureSession.LaunchAsync(browserOptions, planned.Device, plan.FramePath, cancellationToken)

                let current =
                    { Planned = planned
                      Session = session
                      Captures = ResizeArray<byte array>() }

                activeCase <- Some current
                let context = LuaHostInternals.caseContext plan planned
                let mutable started = false
                let mutable primaryError: exn option = None
                let cleanupFailures = ResizeArray<string>()

                try
                    try
                        let! _ = LuaHostInternals.callAsync state prepare context cancellationToken
                        let! _ = LuaHostInternals.callAsync state start context cancellationToken
                        started <- true
                        let! _ = LuaHostInternals.callAsync state ready context cancellationToken
                        let! _ = LuaHostInternals.callAsync state openCase context cancellationToken

                        match planned.Kind with
                        | Still -> ()
                        | Animation workflow ->
                            let workflowFunction = LuaHostInternals.readFunction workflows workflow
                            let! _ = LuaHostInternals.callAsync state workflowFunction context cancellationToken
                            ()

                        match planned.Kind with
                        | Still when current.Captures.Count = 1 ->
                            outputs.Add(
                                { Capture = planned
                                  Bytes = current.Captures[0]
                                  FrameTicksMs = [] }
                            )
                        | Still -> invalidOp "A still definition must call viset.capture.still() exactly once."
                        | Animation _ when current.Captures.Count >= 2 ->
                            let encoded =
                                Media.encodeAnimatedWebP plan.FramesPerSecond (List.ofSeq current.Captures)

                            outputs.Add(
                                { Capture = planned
                                  Bytes = encoded.Bytes
                                  FrameTicksMs = encoded.FrameTicksMs }
                            )
                        | Animation _ ->
                            invalidOp "An animation workflow must call viset.capture.frame() at least twice."
                    with error ->
                        primaryError <- Some error

                    if started then
                        try
                            let! _ = LuaHostInternals.callAsync state stop context CancellationToken.None
                            ()
                        with error ->
                            cleanupFailures.Add(String.Concat("Adapter stop failed: ", error.Message))

                    let! processFailures = cleanupProcessesAsync ()
                    processFailures |> List.iter cleanupFailures.Add

                    try
                        do! (session :> IAsyncDisposable).DisposeAsync().AsTask()
                    with error ->
                        cleanupFailures.Add(error.Message)
                finally
                    activeCase <- None

                match primaryError, List.ofSeq cleanupFailures with
                | None, [] -> ()
                | Some error, [] -> raise error
                | None, failures -> raise (InvalidOperationException(String.Join(" ", failures)))
                | Some error, failures ->
                    raise (
                        InvalidOperationException(
                            String.Concat(error.Message, " Cleanup also failed: ", String.Join(" ", failures)),
                            error
                        )
                    )

            return
                Output.write
                    toolVersion
                    { Version = browser.Version
                      Source = browser.Origin.ToString() }
                    plan
                    (List.ofSeq outputs)
        }
