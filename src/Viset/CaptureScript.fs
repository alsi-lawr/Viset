namespace Viset

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open Tomlyn
open Tomlyn.Model
open Viset.Serialization

module CaptureScript =
    [<Literal>]
    let private SupportedVersion = 1L

    [<Literal>]
    let private DefaultFramesPerSecond = 30

    [<Literal>]
    let private MaximumFramesPerSecond = 60

    [<Literal>]
    let private MaximumLuaSafeInteger = 9007199254740991L

    let private error message = Error [ message ]

    let private concat (parts: string array) = String.Concat parts

    let private invariantInt32 (value: int) =
        value.ToString(CultureInfo.InvariantCulture)

    let private invariantInt64 (value: int64) =
        value.ToString(CultureInfo.InvariantCulture)

    let private appendIndex path index =
        concat [| path; "["; invariantInt32 index; "]" |]

    let private appendKey path key = concat [| path; "."; key |]

    let private traverse parser values =
        let rec loop index parsed remaining =
            match remaining with
            | [] -> Ok(List.rev parsed)
            | value :: tail ->
                match parser index value with
                | Ok parsedValue -> loop (index + 1) (parsedValue :: parsed) tail
                | Error errors -> Error errors

        loop 0 [] values

    let rec private parseTomlValue path (value: obj | null) =
        let parseArray (values: (obj | null) list) =
            values
            |> traverse (fun index item -> parseTomlValue (appendIndex path index) item)
            |> Result.map TomlValue.Array

        let parseTable (table: TomlTable) =
            table
            |> Seq.map (fun entry -> entry.Key, entry.Value)
            |> List.ofSeq
            |> traverse (fun _ (key, item) ->
                parseTomlValue (appendKey path key) item
                |> Result.map (fun parsed -> key, parsed))
            |> Result.map TomlValue.Table

        match value with
        | null -> error (String.Concat(path, " contains an unsupported null value."))
        | :? string as text -> Ok(TomlValue.String text)
        | :? bool as flag -> Ok(TomlValue.Boolean flag)
        | :? int64 as number ->
            if number < -MaximumLuaSafeInteger || number > MaximumLuaSafeInteger then
                error (
                    concat
                        [| path
                           " contains integer "
                           invariantInt64 number
                           ", outside Lua's safe integer range." |]
                )
            else
                Ok(TomlValue.Integer number)
        | :? double as number ->
            if Double.IsFinite number then
                Ok(TomlValue.Float number)
            else
                error (String.Concat(path, " contains a non-finite number."))
        | :? TomlDateTime as dateTime -> Ok(TomlValue.DateTime(dateTime.ToString()))
        | :? TomlTable as table -> parseTable table
        | :? TomlTableArray as tables ->
            tables
            |> Seq.cast<TomlTable>
            |> Seq.map (fun table -> table :> obj)
            |> List.ofSeq
            |> parseArray
        | :? TomlArray as values -> values |> Seq.cast<obj | null> |> List.ofSeq |> parseArray
        | _ -> error (String.Concat(path, " contains an unsupported TOML value."))

    let private parseTomlTable path (table: TomlTable) =
        table
        |> Seq.map (fun entry -> entry.Key, entry.Value)
        |> List.ofSeq
        |> traverse (fun _ (key, value) ->
            parseTomlValue (appendKey path key) value
            |> Result.map (fun parsed -> key, parsed))

    let private requiredText fieldName value =
        if String.IsNullOrWhiteSpace value then
            error (String.Concat(fieldName, " is required and must not be empty."))
        else
            Ok value

    let private parseDimensions path (model: DimensionsTomlModel) =
        let parseDimension name (value: Nullable<int64>) =
            if not value.HasValue then
                error (String.Concat(appendKey path name, " is required."))
            elif value.Value <= 0L || value.Value > int64 Int32.MaxValue then
                error (
                    concat
                        [| appendKey path name
                           " must be between 1 and "
                           invariantInt32 Int32.MaxValue
                           "." |]
                )
            else
                Ok(int value.Value)

        match parseDimension "width" model.Width, parseDimension "height" model.Height with
        | Ok width, Ok height -> Ok { Width = width; Height = height }
        | Error errors, _ -> Error errors
        | _, Error errors -> Error errors

    let private parseDevice frameSource (name: string) (model: DeviceTomlModel) =
        if String.IsNullOrWhiteSpace name then
            error "Device names must not be empty."
        else
            let deviceScale = model.DeviceScale |> Option.ofNullable |> Option.defaultValue 1.0

            if not (Double.IsFinite deviceScale) || deviceScale <= 0.0 then
                error (concat [| "devices."; name; ".device_scale must be a positive finite number." |])
            else
                match parseDimensions (concat [| "devices."; name; ".viewport" |]) model.Viewport with
                | Error errors -> Error errors
                | Ok viewport ->
                    let explicitFrameResult =
                        match Option.ofObj model.Frame with
                        | None -> Ok None
                        | Some frame ->
                            parseDimensions (concat [| "devices."; name; ".frame" |]) frame
                            |> Result.map Some

                    match explicitFrameResult with
                    | Error errors -> Error errors
                    | Ok explicitFrame ->
                        let device =
                            { Name = name
                              Mobile = model.Mobile |> Option.ofNullable |> Option.defaultValue false
                              Touch = model.Touch |> Option.ofNullable |> Option.defaultValue false
                              DeviceScale = deviceScale
                              Viewport = viewport
                              Frame = explicitFrame }

                        match explicitFrame, frameSource with
                        | None, Some(BuiltInFrame style) ->
                            BuiltInFrames.deriveDimensions style device
                            |> Result.mapError List.singleton
                            |> Result.map (fun frame -> name, { device with Frame = Some frame })
                        | _ -> Ok(name, device)

    let private parseDevices frameSource (devices: Dictionary<string, DeviceTomlModel>) =
        if devices.Count = 0 then
            error "devices is required and must contain at least one device."
        else
            devices
            |> Seq.map (fun entry -> entry.Key, entry.Value)
            |> List.ofSeq
            |> traverse (fun _ (name, model) -> parseDevice frameSource name model)
            |> Result.map Map.ofList

    let private parseAxes (matrix: TomlTable) =
        matrix
        |> Seq.map (fun entry -> entry.Key, entry.Value)
        |> List.ofSeq
        |> traverse (fun _ (axisName, axisValue) ->
            if String.IsNullOrWhiteSpace axisName then
                error "matrix contains an empty axis name."
            else
                match axisValue with
                | :? TomlArray as values when values.Count > 0 ->
                    values
                    |> Seq.cast<obj | null>
                    |> List.ofSeq
                    |> traverse (fun index value ->
                        parseTomlValue (concat [| "matrix."; axisName; "["; invariantInt32 index; "]" |]) value)
                    |> Result.map (fun parsed -> axisName, parsed)
                | :? TomlArray -> error (concat [| "matrix."; axisName; " must not be empty." |])
                | _ -> error (concat [| "matrix."; axisName; " must be a TOML array." |]))

    let private scalarText placeholder value =
        match value with
        | TomlValue.String text -> Ok text
        | TomlValue.Integer number -> Ok(number.ToString(CultureInfo.InvariantCulture))
        | TomlValue.Float number -> Ok(number.ToString("R", CultureInfo.InvariantCulture))
        | TomlValue.Boolean flag -> Ok(if flag then "true" else "false")
        | TomlValue.DateTime dateTime -> Ok dateTime
        | TomlValue.Array _
        | TomlValue.Table _ ->
            error (
                concat
                    [| "Output placeholder '{"
                       placeholder
                       "}' refers to a non-scalar matrix value." |]
            )

    let private renderOutput (template: string) (values: (string * TomlValue) list) =
        let bindings = Dictionary<string, TomlValue>(StringComparer.Ordinal)
        values |> List.iter (fun (name, value) -> bindings.Add(name, value))
        let rendered = StringBuilder()

        let rec append index =
            if index >= template.Length then
                Ok(rendered.ToString())
            else
                match template[index] with
                | '}' -> error "output contains an unmatched closing brace."
                | '{' ->
                    let closingIndex = template.IndexOf('}', index + 1)

                    if closingIndex < 0 then
                        error "output contains an unmatched opening brace."
                    else
                        let placeholder = template.Substring(index + 1, closingIndex - index - 1)

                        if String.IsNullOrWhiteSpace placeholder || placeholder.Contains('{') then
                            error "output contains an invalid placeholder."
                        else
                            match bindings.TryGetValue placeholder with
                            | false, _ ->
                                error (concat [| "output requires missing matrix value '"; placeholder; "'." |])
                            | true, value ->
                                match scalarText placeholder value with
                                | Error errors -> Error errors
                                | Ok text ->
                                    rendered.Append(text) |> ignore
                                    append (closingIndex + 1)
                | character ->
                    rendered.Append(character) |> ignore
                    append (index + 1)

        append 0

    let private validateOutputPath (value: string) =
        let invalidCharacters =
            [| '<'; '>'; ':'; '"'; '|'; '?'; '*'; '#'; '!'; '['; ']'; '{'; '}' |]

        if String.IsNullOrWhiteSpace value then
            error "output expands to an empty path."
        elif Path.IsPathRooted value || value.Contains('\\') then
            error (String.Concat("output expands to unsafe path '", value, "'."))
        elif value.IndexOfAny invalidCharacters >= 0 || value |> Seq.exists Char.IsControl then
            error (String.Concat("output expands to unsafe path '", value, "'."))
        else
            let segments = value.Split('/', StringSplitOptions.None)

            if
                segments
                |> Array.exists (fun segment ->
                    String.IsNullOrEmpty segment
                    || segment = "."
                    || segment = ".."
                    || segment.StartsWith(".", StringComparison.Ordinal)
                    || segment.EndsWith(' ')
                    || segment.EndsWith('.'))
            then
                error (String.Concat("output expands to unsafe path '", value, "'."))
            else
                Ok value

    let private expandAxes (axes: (string * TomlValue list) list) =
        axes
        |> List.fold
            (fun combinations (axisName, values) ->
                [ for combination in combinations do
                      for value in values do
                          yield combination @ [ axisName, value ] ])
            [ [] ]

    let private validateBrowserArguments (arguments: List<string>) =
        let conflicts =
            [ "--remote-debugging-port"; "--remote-debugging-pipe"; "--user-data-dir" ]

        arguments
        |> Seq.tryFind (fun argument ->
            String.IsNullOrWhiteSpace argument
            || argument |> Seq.exists Char.IsControl
            || conflicts
               |> List.exists (fun required ->
                   argument.Equals(required, StringComparison.OrdinalIgnoreCase)
                   || argument.StartsWith(String.Concat(required, "="), StringComparison.OrdinalIgnoreCase)))
        |> function
            | Some argument when String.IsNullOrWhiteSpace argument ->
                error "browser_arguments must not contain empty values."
            | Some argument when argument |> Seq.exists Char.IsControl ->
                error "browser_arguments must not contain control characters."
            | Some argument ->
                error (
                    concat
                        [| "browser_arguments contains '"
                           argument
                           "', which conflicts with mandatory browser launch isolation." |]
                )
            | None -> Ok(List.ofSeq arguments)

    let private resolveFrom directory fieldName value =
        match requiredText fieldName value with
        | Error errors -> Error errors
        | Ok path ->
            try
                Ok(Path.GetFullPath(path, directory))
            with
            | :? ArgumentException
            | :? NotSupportedException
            | :? PathTooLongException -> error (concat [| fieldName; " is not a valid path: "; path |])

    let private parseFrameSource scriptDirectory value =
        if String.IsNullOrWhiteSpace value then
            Ok None
        elif String.Equals(value, "builtin:auto", StringComparison.OrdinalIgnoreCase) then
            Ok(Some(BuiltInFrame Automatic))
        elif String.Equals(value, "builtin:phone", StringComparison.OrdinalIgnoreCase) then
            Ok(Some(BuiltInFrame Phone))
        elif String.Equals(value, "builtin:laptop", StringComparison.OrdinalIgnoreCase) then
            Ok(Some(BuiltInFrame Laptop))
        elif value.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase) then
            error (
                String.Concat(
                    "Unknown built-in frame '",
                    value,
                    "'; expected builtin:auto, builtin:phone, or builtin:laptop."
                )
            )
        else
            resolveFrom scriptDirectory "frame" value |> Result.map (CustomFrame >> Some)

    let private captureFormat (output: string) =
        if output.EndsWith(".png", StringComparison.OrdinalIgnoreCase) then
            Ok Png
        elif output.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) then
            Ok WebP
        else
            error "output must end in .png or .webp."

    let private framesPerSecond format (value: Nullable<int64>) =
        match format with
        | Png when value.HasValue -> error "frames_per_second is valid only for .webp output."
        | Png -> Ok DefaultFramesPerSecond
        | WebP when not value.HasValue -> Ok DefaultFramesPerSecond
        | WebP when value.Value < 1L || value.Value > int64 MaximumFramesPerSecond ->
            error (
                concat
                    [| "frames_per_second must be between 1 and "
                       invariantInt32 MaximumFramesPerSecond
                       "." |]
            )
        | WebP -> Ok(int value.Value)

    let private extractHeader (source: string) =
        let mutable index = 0

        while index < source.Length
              && (Char.IsWhiteSpace source[index] || source[index] = '\uFEFF') do
            index <- index + 1

        if
            index + 4 > source.Length
            || not (source.AsSpan(index, 4).SequenceEqual("--[[".AsSpan()))
        then
            error "Capture Lua must begin with a --[[ TOML header block."
        else
            let contentStart = index + 4
            let closingIndex = source.IndexOf("]]", contentStart, StringComparison.Ordinal)

            if closingIndex < 0 then
                error "Capture Lua TOML header block is not closed with ]]."
            else
                let content = source.Substring(contentStart, closingIndex - contentStart)
                let trimmed = content.TrimStart([| '\r'; '\n'; ' '; '\t' |])

                use reader = new StringReader(trimmed)

                match reader.ReadLine() |> Option.ofObj with
                | Some marker when String.Equals(marker.Trim(), "# viset", StringComparison.Ordinal) -> Ok content
                | _ -> error "Capture Lua TOML header must begin with '# viset'."

    let private validateModel (request: CaptureRequest) (scriptDirectory: string) (model: CaptureTomlModel) =
        if not model.Version.HasValue then
            error "version is required."
        elif model.Version.Value <> SupportedVersion then
            error (
                concat
                    [| "Unsupported capture version "
                       invariantInt64 model.Version.Value
                       "; expected "
                       invariantInt64 SupportedVersion
                       "." |]
            )
        else
            match requiredText "output" model.Output with
            | Error errors -> Error errors
            | Ok outputTemplate ->
                match captureFormat outputTemplate with
                | Error errors -> Error errors
                | Ok format ->
                    match parseFrameSource scriptDirectory model.Frame with
                    | Error errors -> Error errors
                    | Ok frameSource ->
                        match framesPerSecond format model.FramesPerSecond with
                        | Error errors -> Error errors
                        | Ok fps ->
                            match validateBrowserArguments model.BrowserArguments with
                            | Error errors -> Error errors
                            | Ok browserArguments ->
                                match parseDevices frameSource model.Devices with
                                | Error errors -> Error errors
                                | Ok devices ->
                                    match parseAxes model.Matrix with
                                    | Error errors -> Error errors
                                    | Ok axes ->
                                        let hasDeviceAxis = axes |> List.exists (fun (name, _) -> name = "device")

                                        let fixedDevice =
                                            if String.IsNullOrWhiteSpace model.Device then
                                                None
                                            else
                                                Some model.Device

                                        if fixedDevice.IsSome && hasDeviceAxis then
                                            error "device and matrix.device are mutually exclusive."
                                        elif fixedDevice.IsNone && not hasDeviceAxis then
                                            error "device is required unless matrix.device supplies the device axis."
                                        else
                                            match parseTomlTable "data" model.Data with
                                            | Error errors -> Error errors
                                            | Ok data ->
                                                let outputRootResult =
                                                    match request.OutputPath with
                                                    | Some outputPath -> Ok outputPath
                                                    | None when String.IsNullOrWhiteSpace model.OutputRoot ->
                                                        Ok scriptDirectory
                                                    | None -> resolveFrom scriptDirectory "output_root" model.OutputRoot

                                                match outputRootResult with
                                                | Error errors -> Error errors
                                                | Ok outputRoot ->
                                                    let pathComparer =
                                                        if OperatingSystem.IsWindows() then
                                                            StringComparer.OrdinalIgnoreCase
                                                        else
                                                            StringComparer.Ordinal

                                                    let outputPaths = HashSet<string>(pathComparer)

                                                    let planCase matrixValues =
                                                        let deviceNameResult =
                                                            match fixedDevice with
                                                            | Some name -> Ok name
                                                            | None ->
                                                                match
                                                                    matrixValues
                                                                    |> List.tryFind (fun (name, _) -> name = "device")
                                                                with
                                                                | Some(_, TomlValue.String name) -> Ok name
                                                                | Some _ ->
                                                                    error "matrix.device values must be strings."
                                                                | None -> error "matrix.device is required."

                                                        match deviceNameResult with
                                                        | Error errors -> Error errors
                                                        | Ok deviceName ->
                                                            match Map.tryFind deviceName devices with
                                                            | None ->
                                                                error (
                                                                    String.Concat(
                                                                        "Capture references unknown device '",
                                                                        deviceName,
                                                                        "'."
                                                                    )
                                                                )
                                                            | Some device ->
                                                                let placeholders =
                                                                    if hasDeviceAxis then
                                                                        matrixValues
                                                                    else
                                                                        ("device", TomlValue.String deviceName)
                                                                        :: matrixValues

                                                                match renderOutput outputTemplate placeholders with
                                                                | Error errors -> Error errors
                                                                | Ok rendered ->
                                                                    match validateOutputPath rendered with
                                                                    | Error errors -> Error errors
                                                                    | Ok relativePath ->
                                                                        let absolutePath =
                                                                            Path.GetFullPath(
                                                                                relativePath.Replace(
                                                                                    '/',
                                                                                    Path.DirectorySeparatorChar
                                                                                ),
                                                                                outputRoot
                                                                            )

                                                                        if not (outputPaths.Add absolutePath) then
                                                                            error (
                                                                                String.Concat(
                                                                                    "Expanded output path is duplicated: ",
                                                                                    relativePath
                                                                                )
                                                                            )
                                                                        else
                                                                            Ok
                                                                                { Format = format
                                                                                  OutputRelativePath = relativePath
                                                                                  OutputPath = absolutePath
                                                                                  Device = device
                                                                                  Axes = matrixValues
                                                                                  Data = data }

                                                    axes
                                                    |> expandAxes
                                                    |> traverse (fun _ values -> planCase values)
                                                    |> Result.map (fun captures ->
                                                        { ScriptPath = request.ScriptPath
                                                          ScriptDirectory = scriptDirectory
                                                          OutputPath = outputRoot
                                                          FrameSource = frameSource
                                                          BrowserPath = request.BrowserPath
                                                          BrowserArguments = browserArguments
                                                          FramesPerSecond = fps
                                                          Captures = captures
                                                          Force = request.Force })

    let plan (request: CaptureRequest) =
        if not (File.Exists request.ScriptPath) then
            error (String.Concat("Capture Lua does not exist: ", request.ScriptPath))
        else
            try
                let scriptDirectory =
                    Path.GetDirectoryName request.ScriptPath
                    |> Option.ofObj
                    |> Option.defaultValue Environment.CurrentDirectory

                let source = File.ReadAllText request.ScriptPath

                match extractHeader source with
                | Error errors -> Error errors
                | Ok header ->
                    let model = CaptureTomlModels.Deserialize header
                    validateModel request scriptDirectory model
            with errorValue ->
                error (String.Concat("Capture TOML could not be parsed: ", errorValue.Message))
