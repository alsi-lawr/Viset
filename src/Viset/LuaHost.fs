namespace Viset

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Lua
open Lua.Standard

type private CaptureModuleLoader(scriptDirectory: string) =
    let root = Path.GetFullPath scriptDirectory

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
      AnimationUpdateDurations: ResizeArray<TimeSpan>
      mutable Snapshot: byte array option
      mutable Recorder: RecordingController option }

    override activeCase.ToString() = activeCase.Planned.OutputPath

module private LuaHostInternals =
    let setValue (table: LuaTable) (key: string) (value: LuaValue) = table[LuaValue key] <- value

    let getValue (table: LuaTable) (key: string) = table[LuaValue key]

    let tryRead<'T> (value: LuaValue) =
        let mutable result = Unchecked.defaultof<'T>
        if value.TryRead<'T>(&result) then Some result else None

    let requiredString (table: LuaTable) key =
        match getValue table key |> tryRead<string> with
        | Some value when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> invalidArg key (String.Concat(key, " is required and must be a non-empty string."))

    let optionalString (table: LuaTable) key =
        match getValue table key with
        | value when value.Type = LuaValueType.Nil -> None
        | value ->
            match tryRead<string> value with
            | Some text when not (String.IsNullOrWhiteSpace text) -> Some text
            | _ -> invalidArg key (String.Concat(key, " must be a non-empty string."))

    let optionalNumber (table: LuaTable) key defaultValue =
        match getValue table key with
        | value when value.Type = LuaValueType.Nil -> defaultValue
        | value ->
            match tryRead<double> value with
            | Some number when Double.IsFinite number -> number
            | _ -> invalidArg key (String.Concat(key, " must be a finite number."))

    let numberToInt label value =
        if
            not (Double.IsFinite value)
            || value < double Int32.MinValue
            || value > double Int32.MaxValue
            || Math.Truncate value <> value
        then
            invalidArg label (String.Concat(label, " must be an integer."))

        int value

    let tableValue values =
        let table = LuaTable()
        values |> List.iter (fun (key, value) -> setValue table key value)
        LuaValue table

    let hostFunction name operation =
        new LuaFunction(
            name,
            Func<LuaFunctionExecutionContext, CancellationToken, ValueTask<int>>(fun context cancellationToken ->
                ValueTask<int>(
                    task {
                        try
                            return! operation context cancellationToken
                        with
                        | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                            return raise (OperationCanceledException cancellationToken)
                        | :? OperationCanceledException as error ->
                            return raise (TimeoutException(String.Concat(name, " timed out."), error))
                        | error ->
                            return raise (InvalidOperationException(String.Concat(name, ": ", error.Message), error))
                    }
                ))
        )

    let durationMilliseconds (value: LuaValue) =
        let validate milliseconds =
            if not (Double.IsFinite milliseconds) || milliseconds <= 0.0 then
                invalidArg "duration" "duration must be a positive finite value."

            milliseconds

        match tryRead<double> value with
        | Some number -> validate number
        | None ->
            match tryRead<string> value with
            | None -> invalidArg "duration" "duration must be a number of milliseconds or a string ending in ms or s."
            | Some text ->
                let trimmed = text.Trim()

                let parse (suffix: string) (multiplier: double) =
                    let numberText = trimmed.Substring(0, trimmed.Length - suffix.Length)

                    match Double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture) with
                    | true, number -> validate (number * multiplier)
                    | _ -> invalidArg "duration" (String.Concat("Invalid duration: ", text))

                if trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase) then
                    parse "ms" 1.0
                elif trimmed.EndsWith("s", StringComparison.OrdinalIgnoreCase) then
                    parse "s" 1000.0
                else
                    invalidArg "duration" "duration strings must end in ms or s."

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

    let javascriptArguments (arguments: LuaTable) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)

        let rec writeValue depth (value: LuaValue) =
            if depth > 64 then
                invalidArg "arguments" "JavaScript arguments must not exceed 64 nested tables."

            match value.Type with
            | LuaValueType.Nil -> writer.WriteNullValue()
            | LuaValueType.Boolean -> writer.WriteBooleanValue(value.Read<bool>())
            | LuaValueType.String -> writer.WriteStringValue(value.Read<string>())
            | LuaValueType.Number ->
                let number = value.Read<double>()

                if not (Double.IsFinite number) then
                    invalidArg "arguments" "JavaScript arguments must not contain non-finite numbers."

                writer.WriteNumberValue number
            | LuaValueType.Table -> writeTable (depth + 1) (value.Read<LuaTable>())
            | unsupported ->
                invalidArg
                    "arguments"
                    (String.Concat(
                        "JavaScript arguments cannot contain Lua ",
                        unsupported.ToString().ToLowerInvariant(),
                        " values."
                    ))

        and writeTable depth (table: LuaTable) =
            if table.ArrayLength > 0 && table.HashMapCount > 0 then
                invalidArg "arguments" "JavaScript argument tables must not mix array and object entries."
            elif table.ArrayLength > 0 then
                writer.WriteStartArray()

                for index in 1 .. table.ArrayLength do
                    writeValue depth table[LuaValue(double index)]

                writer.WriteEndArray()
            else
                writer.WriteStartObject()

                for item in table do
                    if item.Key.Type <> LuaValueType.String then
                        invalidArg "arguments" "JavaScript argument object keys must be strings."

                    writer.WritePropertyName(item.Key.Read<string>())
                    writeValue depth item.Value

                writer.WriteEndObject()

        writeTable 0 arguments
        writer.Flush()
        let json = Encoding.UTF8.GetString(stream.ToArray())

        use literalStream = new MemoryStream()
        use literalWriter = new Utf8JsonWriter(literalStream)
        literalWriter.WriteStringValue json
        literalWriter.Flush()

        String.Concat("JSON.parse(", Encoding.UTF8.GetString(literalStream.ToArray()), ")")

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
        setValue table "script_path" (LuaValue plan.ScriptPath)
        setValue table "output" (LuaValue capture.OutputPath)
        setValue table "device" (LuaValue(deviceTable capture.Device))

        let axes = LuaTable(0, capture.Axes.Length)

        capture.Axes
        |> List.iter (fun (key, value) -> setValue axes key (tomlValue value))

        setValue table "axes" (LuaValue axes)

        let data = LuaTable(0, capture.Data.Length)

        capture.Data
        |> List.iter (fun (key, value) -> setValue data key (tomlValue value))

        setValue table "data" (LuaValue data)
        table

    let collectAnimationDurations (destination: ResizeArray<TimeSpan>) value =
        match value with
        | CdpEvaluationValue.Json json when json.ValueKind = JsonValueKind.Object ->
            let mutable durations = Unchecked.defaultof<JsonElement>

            if
                json.TryGetProperty("update_durations_ms", &durations)
                && durations.ValueKind = JsonValueKind.Array
            then
                for duration in durations.EnumerateArray() do
                    let milliseconds = duration.GetDouble()

                    if Double.IsFinite milliseconds && milliseconds >= 0.0 then
                        destination.Add(TimeSpan.FromMilliseconds milliseconds)
        | _ -> ()

module LuaHost =
    let private bootstrap =
        """
local duration_ms = viset.__duration_ms
local now_ms = viset.__now_ms
local sleep_ms = viset.__sleep_ms
local create_recording = viset.__recording_create
local start_recording = viset.__recording_start
local stop_recording = viset.__recording_stop
local recording_active = viset.__recording_active

function viset.sleep(duration)
  sleep_ms(duration_ms(duration))
end

function viset.javascript(source)
  if type(source) ~= "string" then
    error("viset.javascript requires a string", 2)
  end

  return source
end

function viset.record()
  create_recording()
  local recording = {}

  function recording:start()
    start_recording()
  end

  function recording:stop()
    stop_recording()
  end

  function recording:during(duration, callback)
    if not recording_active() then
      error("recording:during requires a started recording", 2)
    end

    if callback ~= nil and type(callback) ~= "function" then
      error("recording:during callback must be a function", 2)
    end

    local minimum = duration_ms(duration)
    local started = now_ms()

    if callback ~= nil then
      callback()
    end

    if not recording_active() then
      error("recording:during callback must not stop the recording", 2)
    end

    local remaining = minimum - (now_ms() - started)
    if remaining > 0 then
      sleep_ms(remaining)
    end
  end

  return recording
end

viset.__duration_ms = nil
viset.__now_ms = nil
viset.__sleep_ms = nil
viset.__recording_create = nil
viset.__recording_start = nil
viset.__recording_stop = nil
viset.__recording_active = nil
"""

    let runAsync
        (_toolVersion: string)
        (plan: CapturePlan)
        (browser: BrowserExecutable)
        (cancellationToken: CancellationToken)
        =
        task {
            Output.preflight plan

            let browserOptions =
                BrowserSessionOptions(browser.ExecutablePath, plan.BrowserArguments)

            let outputs = ResizeArray<CaptureOutputResult>()

            for planned in plan.Captures do
                use! session =
                    CaptureSession.LaunchAsync(browserOptions, planned.Device, plan.FrameSource, cancellationToken)

                use state = LuaState.Create()
                state.OpenStandardLibraries()
                state.ModuleLoader <- CaptureModuleLoader(plan.ScriptDirectory)
                use httpClient = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)
                let processes = Dictionary<int, ManagedProcess>()
                let processLock = obj ()
                let mutable nextProcessHandle = 0

                let activeCase =
                    { Planned = planned
                      Session = session
                      AnimationUpdateDurations = ResizeArray<TimeSpan>()
                      Snapshot = None
                      Recorder = None }

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

                let processResultAsync handle (managed: ManagedProcess) =
                    task {
                        let! standardOutput = managed.StandardOutput
                        let! standardError = managed.StandardError
                        let exitCode = managed.Process.ExitCode
                        removeProcess handle |> Option.iter (fun value -> value.Process.Dispose())

                        return
                            LuaHostInternals.tableValue
                                [ "exit_code", LuaValue(double exitCode)
                                  "stdout", LuaValue standardOutput
                                  "stderr", LuaValue standardError ]
                    }

                let stopProcessAsync handle cancellationToken =
                    task {
                        let managed =
                            findProcess handle
                            |> Option.defaultWith (fun () -> invalidOp "The process handle is not active.")

                        if not managed.Process.HasExited then
                            managed.Process.Kill true

                        do! managed.Process.WaitForExitAsync cancellationToken
                        return! processResultAsync handle managed
                    }

                let cleanupProcessesAsync () =
                    task {
                        let handles = lock processLock (fun () -> processes.Keys |> Seq.toArray)
                        let failures = ResizeArray<string>()

                        for handle in handles do
                            try
                                let! _ = stopProcessAsync handle CancellationToken.None
                                ()
                            with error ->
                                failures.Add(error.Message)

                        return List.ofSeq failures
                    }

                let responseTable (response: HttpResponseMessage) (body: string) =
                    let headers = LuaTable()

                    for header in response.Headers do
                        LuaHostInternals.setValue headers header.Key (LuaValue(String.Join(",", header.Value)))

                    for header in response.Content.Headers do
                        LuaHostInternals.setValue headers header.Key (LuaValue(String.Join(",", header.Value)))

                    LuaHostInternals.tableValue
                        [ "status", LuaValue(double (int response.StatusCode))
                          "headers", LuaValue headers
                          "body", LuaValue body ]

                let sendGetAsync
                    (options: LuaTable)
                    (timeoutMilliseconds: double)
                    (cancellationToken: CancellationToken)
                    =
                    task {
                        let uri = Uri(LuaHostInternals.requiredString options "url", UriKind.Absolute)
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
                        use! response = httpClient.SendAsync(request, timeout.Token)
                        let! body = response.Content.ReadAsStringAsync(timeout.Token)
                        return response, body
                    }

                let processStart =
                    LuaHostInternals.hostFunction "viset.process.start" (fun context _ ->
                        task {
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
                                |> Option.defaultWith (fun () -> invalidOp "Process could not be started.")

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

                            return context.Return(LuaValue(double handle))
                        })

                let processWait =
                    LuaHostInternals.hostFunction "viset.process.wait" (fun context cancellationToken ->
                        task {
                            let handle = context.GetArgument<double>(0) |> LuaHostInternals.numberToInt "handle"

                            let timeoutMilliseconds =
                                if context.HasArgument 1 then
                                    LuaHostInternals.durationMilliseconds (context.GetArgument(1))
                                else
                                    30000.0

                            let managed =
                                findProcess handle
                                |> Option.defaultWith (fun () -> invalidOp "The process handle is not active.")

                            use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                            timeout.CancelAfter(TimeSpan.FromMilliseconds timeoutMilliseconds)
                            do! managed.Process.WaitForExitAsync timeout.Token
                            let! result = processResultAsync handle managed
                            return context.Return result
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

                            let timeoutMilliseconds =
                                match LuaHostInternals.getValue options "timeout" with
                                | value when value.Type = LuaValueType.Nil -> 30000.0
                                | value -> LuaHostInternals.durationMilliseconds value

                            let! response, body = sendGetAsync options timeoutMilliseconds cancellationToken
                            use response = response
                            return context.Return(responseTable response body)
                        })

                let httpWait =
                    LuaHostInternals.hostFunction "viset.http.wait" (fun context cancellationToken ->
                        task {
                            let options = context.GetArgument<LuaTable>(0)

                            let timeoutMilliseconds =
                                match LuaHostInternals.getValue options "timeout" with
                                | value when value.Type = LuaValueType.Nil -> 30000.0
                                | value -> LuaHostInternals.durationMilliseconds value

                            let stopwatch = Stopwatch.StartNew()
                            let mutable completed = None

                            while completed.IsNone && stopwatch.Elapsed.TotalMilliseconds < timeoutMilliseconds do
                                let remaining = timeoutMilliseconds - stopwatch.Elapsed.TotalMilliseconds
                                let requestTimeout = max 1.0 (min 500.0 remaining)

                                try
                                    let! response, body = sendGetAsync options requestTimeout cancellationToken
                                    use response = response

                                    if int response.StatusCode >= 200 && int response.StatusCode <= 299 then
                                        completed <- Some(responseTable response body)
                                with
                                | :? HttpRequestException -> ()
                                | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                                    ()

                                if completed.IsNone then
                                    do! Task.Delay(50, cancellationToken)

                            match completed with
                            | Some value -> return context.Return value
                            | None ->
                                return
                                    raise (
                                        TimeoutException
                                            "The HTTP endpoint did not return a 2xx response before timeout."
                                    )
                        })

                let pageNavigate =
                    LuaHostInternals.hostFunction "viset.page.navigate" (fun context cancellationToken ->
                        task {
                            let uri = Uri(context.GetArgument<string>(0), UriKind.Absolute)
                            do! activeCase.Session.Page.NavigateAsync(uri, cancellationToken)
                            return context.Return()
                        })

                let pageEvaluate =
                    LuaHostInternals.hostFunction "viset.page.evaluate" (fun context cancellationToken ->
                        task {
                            let script = context.GetArgument<string>(0)

                            let expression =
                                if context.HasArgument 1 then
                                    let arguments = context.GetArgument<LuaTable>(1)
                                    let serialized = LuaHostInternals.javascriptArguments arguments

                                    String.Concat(
                                        "(async()=>{const __visetFunction=(",
                                        script,
                                        ");if(typeof __visetFunction!=='function')throw new TypeError('JavaScript with arguments must evaluate to a function');return await __visetFunction(",
                                        serialized,
                                        ");})()"
                                    )
                                else
                                    script

                            let! result = activeCase.Session.Page.EvaluateAsync(expression, cancellationToken)

                            match result with
                            | Ok value -> return context.Return(LuaHostInternals.evaluationValue value)
                            | Error error -> return raise (InvalidOperationException(error.ToString()))
                        })

                let pageWaitFor =
                    LuaHostInternals.hostFunction "viset.page.wait_for" (fun context cancellationToken ->
                        task {
                            let script = context.GetArgument<string>(0)

                            let timeoutMilliseconds =
                                LuaHostInternals.durationMilliseconds (context.GetArgument(1))

                            use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                            timeout.CancelAfter(TimeSpan.FromMilliseconds timeoutMilliseconds)
                            let mutable ready = false

                            while not ready do
                                let! result = activeCase.Session.Page.EvaluateAsync(script, timeout.Token)

                                match result with
                                | Ok(CdpEvaluationValue.Boolean value) -> ready <- value
                                | Ok _ -> ready <- false
                                | Error error -> raise (InvalidOperationException(error.ToString()))

                                if not ready then
                                    do! Task.Delay(20, timeout.Token)

                            return context.Return()
                        })

                let pageAnimate =
                    LuaHostInternals.hostFunction "viset.page.animate" (fun context cancellationToken ->
                        task {
                            let options = context.GetArgument<LuaTable>(0)

                            let duration =
                                LuaHostInternals.getValue options "duration"
                                |> LuaHostInternals.durationMilliseconds

                            let update = LuaHostInternals.requiredString options "update"

                            let easing =
                                LuaHostInternals.optionalString options "easing" |> Option.defaultValue "linear"

                            let easingExpression =
                                match easing with
                                | "linear" -> "progress => progress"
                                | "in_sine" -> "progress => 1 - Math.cos((progress * Math.PI) / 2)"
                                | "out_sine" -> "progress => Math.sin((progress * Math.PI) / 2)"
                                | "in_out_sine" -> "progress => -(Math.cos(Math.PI * progress) - 1) / 2"
                                | custom -> custom

                            let script =
                                String.Concat(
                                    "(async () => {",
                                    "const durationMs=",
                                    duration.ToString("R", CultureInfo.InvariantCulture),
                                    ";const update=(",
                                    update,
                                    ");const easing=(",
                                    easingExpression,
                                    ");",
                                    "if(typeof update!=='function')throw new TypeError('update must evaluate to a function');",
                                    "if(typeof easing!=='function')throw new TypeError('easing must evaluate to a function');",
                                    "const updateDurations=[];const started=performance.now();",
                                    "return await new Promise((resolve,reject)=>{",
                                    "const tick=now=>{try{const linear=Math.min(1,Math.max(0,(now-started)/durationMs));",
                                    "const progress=easing(linear);if(!Number.isFinite(progress))throw new TypeError('easing returned a non-finite value');",
                                    "const before=performance.now();const result=update(Object.freeze({progress,linear_progress:linear,elapsed_ms:Math.min(now-started,durationMs),duration_ms:durationMs}));",
                                    "if(result&&typeof result.then==='function')throw new TypeError('update must be synchronous');",
                                    "updateDurations.push(performance.now()-before);",
                                    "if(linear>=1){resolve({update_durations_ms:updateDurations});}else{requestAnimationFrame(tick);}",
                                    "}catch(error){reject(error);}};requestAnimationFrame(tick);});})()"
                                )

                            let! result = activeCase.Session.Page.EvaluateAsync(script, cancellationToken)

                            match result with
                            | Error error -> return raise (InvalidOperationException(error.ToString()))
                            | Ok value ->
                                LuaHostInternals.collectAnimationDurations activeCase.AnimationUpdateDurations value
                                return context.Return()
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
                                |> fun value -> value.Read<double>()
                                |> LuaHostInternals.numberToInt "width"

                            let height =
                                LuaHostInternals.getValue viewport "height"
                                |> fun value -> value.Read<double>()
                                |> LuaHostInternals.numberToInt "height"

                            let scale = LuaHostInternals.optionalNumber device "device_scale" 1.0

                            let mobile =
                                match LuaHostInternals.getValue device "mobile" with
                                | value when value.Type = LuaValueType.Nil -> false
                                | value -> value.Read<bool>()

                            let touch =
                                match LuaHostInternals.getValue device "touch" with
                                | value when value.Type = LuaValueType.Nil -> false
                                | value -> value.Read<bool>()

                            do!
                                activeCase.Session.Page.ConfigureEmulationAsync(
                                    width,
                                    height,
                                    scale,
                                    mobile,
                                    touch,
                                    cancellationToken
                                )

                            return context.Return()
                        })

                let emulationTouch =
                    LuaHostInternals.hostFunction "viset.emulation.touch" (fun context cancellationToken ->
                        task {
                            let x = context.GetArgument<double>(0)
                            let y = context.GetArgument<double>(1)
                            do! activeCase.Session.Page.TouchAsync(x, y, cancellationToken)
                            return context.Return()
                        })

                let snapshot =
                    LuaHostInternals.hostFunction "viset.snapshot" (fun context cancellationToken ->
                        task {
                            match planned.Format with
                            | WebP -> invalidOp "viset.snapshot is valid only for .png output."
                            | Png -> ()

                            if activeCase.Snapshot.IsSome then
                                invalidOp "A .png capture must call viset.snapshot exactly once."

                            let! bytes = activeCase.Session.CapturePngAsync cancellationToken
                            activeCase.Snapshot <- Some bytes
                            return context.Return()
                        })

                let duration =
                    LuaHostInternals.hostFunction "viset.__duration_ms" (fun context _ ->
                        task {
                            let milliseconds = LuaHostInternals.durationMilliseconds (context.GetArgument(0))
                            return context.Return(LuaValue milliseconds)
                        })

                let now =
                    LuaHostInternals.hostFunction "viset.__now_ms" (fun context _ ->
                        task {
                            let milliseconds =
                                double (Stopwatch.GetTimestamp()) * 1000.0 / double Stopwatch.Frequency

                            return context.Return(LuaValue milliseconds)
                        })

                let sleep =
                    LuaHostInternals.hostFunction "viset.__sleep_ms" (fun context cancellationToken ->
                        task {
                            let milliseconds = context.GetArgument<double>(0)

                            if not (Double.IsFinite milliseconds) || milliseconds <= 0.0 then
                                invalidArg "duration" "sleep duration must be a positive finite number."

                            do! Task.Delay(TimeSpan.FromMilliseconds milliseconds, cancellationToken)
                            return context.Return()
                        })

                let recordingCreate =
                    LuaHostInternals.hostFunction "viset.__recording_create" (fun context _ ->
                        task {
                            match planned.Format with
                            | Png -> invalidOp "viset.record is valid only for .webp output."
                            | WebP -> ()

                            if activeCase.Recorder.IsSome then
                                invalidOp "A .webp capture may create exactly one recording."

                            activeCase.Recorder <-
                                Some(
                                    RecordingController.CreateScreencast(
                                        activeCase.Session,
                                        plan.FramesPerSecond,
                                        plan.WebP,
                                        cancellationToken
                                    )
                                )

                            return context.Return()
                        })

                let recordingStart =
                    LuaHostInternals.hostFunction "recording:start" (fun context _ ->
                        task {
                            let recorder =
                                activeCase.Recorder
                                |> Option.defaultWith (fun () -> invalidOp "viset.record must be called first.")

                            do! recorder.StartAsync()
                            return context.Return()
                        })

                let recordingStop =
                    LuaHostInternals.hostFunction "recording:stop" (fun context _ ->
                        task {
                            let recorder =
                                activeCase.Recorder
                                |> Option.defaultWith (fun () -> invalidOp "viset.record must be called first.")

                            do! recorder.StopAsync()
                            return context.Return()
                        })

                let recordingActive =
                    LuaHostInternals.hostFunction "recording:active" (fun context _ ->
                        task {
                            let isActive =
                                activeCase.Recorder |> Option.exists (fun recorder -> recorder.IsActive)

                            return context.Return(LuaValue isActive)
                        })

                let processTable = LuaTable()
                LuaHostInternals.setValue processTable "start" (LuaValue processStart)
                LuaHostInternals.setValue processTable "wait" (LuaValue processWait)
                LuaHostInternals.setValue processTable "stop" (LuaValue processStop)

                let httpTable = LuaTable()
                LuaHostInternals.setValue httpTable "get" (LuaValue httpGet)
                LuaHostInternals.setValue httpTable "wait" (LuaValue httpWait)

                let pageTable = LuaTable()
                LuaHostInternals.setValue pageTable "navigate" (LuaValue pageNavigate)
                LuaHostInternals.setValue pageTable "evaluate" (LuaValue pageEvaluate)
                LuaHostInternals.setValue pageTable "wait_for" (LuaValue pageWaitFor)
                LuaHostInternals.setValue pageTable "animate" (LuaValue pageAnimate)

                let emulationTable = LuaTable()
                LuaHostInternals.setValue emulationTable "apply" (LuaValue emulationApply)
                LuaHostInternals.setValue emulationTable "touch" (LuaValue emulationTouch)

                let scriptTable = LuaTable()
                LuaHostInternals.setValue scriptTable "directory" (LuaValue plan.ScriptDirectory)

                let visetTable = LuaTable()
                LuaHostInternals.setValue visetTable "api_version" (LuaValue 1.0)
                LuaHostInternals.setValue visetTable "context" (LuaValue(LuaHostInternals.caseContext plan planned))
                LuaHostInternals.setValue visetTable "script" (LuaValue scriptTable)
                LuaHostInternals.setValue visetTable "process" (LuaValue processTable)
                LuaHostInternals.setValue visetTable "http" (LuaValue httpTable)
                LuaHostInternals.setValue visetTable "page" (LuaValue pageTable)
                LuaHostInternals.setValue visetTable "emulation" (LuaValue emulationTable)
                LuaHostInternals.setValue visetTable "snapshot" (LuaValue snapshot)
                LuaHostInternals.setValue visetTable "__duration_ms" (LuaValue duration)
                LuaHostInternals.setValue visetTable "__now_ms" (LuaValue now)
                LuaHostInternals.setValue visetTable "__sleep_ms" (LuaValue sleep)
                LuaHostInternals.setValue visetTable "__recording_create" (LuaValue recordingCreate)
                LuaHostInternals.setValue visetTable "__recording_start" (LuaValue recordingStart)
                LuaHostInternals.setValue visetTable "__recording_stop" (LuaValue recordingStop)
                LuaHostInternals.setValue visetTable "__recording_active" (LuaValue recordingActive)
                state.Environment[LuaValue "viset"] <- LuaValue visetTable

                let mutable primaryError: exn option = None
                let cleanupFailures = ResizeArray<string>()
                let mutable captured: CapturedFile option = None
                let mutable performance: CapturePerformanceMetrics option = None
                let mutable webPPerformance: WebPProductionMetrics option = None

                try
                    try
                        let! _ = state.DoStringAsync(bootstrap, cancellationToken = cancellationToken).AsTask()
                        let! _ = state.DoFileAsync(plan.ScriptPath, cancellationToken).AsTask()

                        match planned.Format with
                        | Png ->
                            let bytes =
                                activeCase.Snapshot
                                |> Option.defaultWith (fun () ->
                                    invalidOp "A .png capture must call viset.snapshot exactly once.")

                            captured <-
                                Some
                                    { Capture = planned
                                      Bytes = bytes
                                      FrameTicksMs = [] }
                        | WebP ->
                            let recorder =
                                activeCase.Recorder
                                |> Option.defaultWith (fun () ->
                                    invalidOp "A .webp capture must call viset.record exactly once.")

                            let! animation = recorder.FinalizeAsync cancellationToken
                            performance <- Some animation.Metrics
                            webPPerformance <- Some animation.Encoded.Metrics

                            captured <-
                                Some
                                    { Capture = planned
                                      Bytes = animation.Encoded.Bytes
                                      FrameTicksMs = animation.Encoded.FrameTicksMs }
                    with error ->
                        primaryError <- Some error

                    match activeCase.Recorder with
                    | Some recorder when recorder.IsActive ->
                        try
                            do! recorder.StopAsync()
                        with error ->
                            cleanupFailures.Add(String.Concat("Recording cleanup failed: ", error.Message))
                    | _ -> ()

                    let! processFailures = cleanupProcessesAsync ()
                    processFailures |> List.iter cleanupFailures.Add

                    activeCase.Recorder
                    |> Option.iter (fun recorder ->
                        try
                            (recorder :> IDisposable).Dispose()
                        with error ->
                            cleanupFailures.Add(String.Concat("Recording spool cleanup failed: ", error.Message)))

                    try
                        do! (session :> IAsyncDisposable).DisposeAsync().AsTask()
                    with error ->
                        cleanupFailures.Add(error.Message)
                with error ->
                    primaryError <- primaryError |> Option.orElse (Some error)

                match primaryError, List.ofSeq cleanupFailures with
                | Some error, [] -> raise error
                | None, [] -> ()
                | None, failures -> raise (InvalidOperationException(String.Join(" ", failures)))
                | Some error, failures ->
                    raise (
                        InvalidOperationException(
                            String.Concat(error.Message, " Cleanup also failed: ", String.Join(" ", failures)),
                            error
                        )
                    )

                let completed =
                    captured
                    |> Option.defaultWith (fun () -> invalidOp "Capture completed without output bytes.")

                let writtenPath = Output.write plan.Force completed

                outputs.Add(
                    { Path = writtenPath
                      Format = planned.Format
                      FrameTicksMs = completed.FrameTicksMs
                      Performance = performance
                      WebPPerformance = webPPerformance
                      AnimationUpdateDurations = List.ofSeq activeCase.AnimationUpdateDurations }
                )

            return { Outputs = List.ofSeq outputs }
        }
