namespace Viset

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open Tomlyn
open Tomlyn.Model
open Viset.Serialization

module Matrix =
    [<Literal>]
    let private SupportedVersion = 1L

    [<Literal>]
    let private DefaultFramesPerSecond = 30

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

    let private definitionMessage definitionId suffix =
        concat [| "Definition '"; definitionId; "' "; suffix |]

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

    let private parseDevice (name: string) (model: DeviceTomlModel) =
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
                    let frameResult =
                        match Option.ofObj model.Frame with
                        | None -> Ok None
                        | Some frame ->
                            parseDimensions (concat [| "devices."; name; ".frame" |]) frame
                            |> Result.map Some

                    frameResult
                    |> Result.map (fun frame ->
                        name,
                        { Name = name
                          Mobile = model.Mobile |> Option.ofNullable |> Option.defaultValue false
                          Touch = model.Touch |> Option.ofNullable |> Option.defaultValue false
                          DeviceScale = deviceScale
                          Viewport = viewport
                          Frame = frame })

    let private parseDevices (devices: Dictionary<string, DeviceTomlModel>) =
        if devices.Count = 0 then
            error "devices is required and must contain at least one device."
        else
            devices
            |> Seq.map (fun entry -> entry.Key, entry.Value)
            |> List.ofSeq
            |> traverse (fun _ (name, model) -> parseDevice name model)
            |> Result.map Map.ofList

    let private parseAxes definitionId (matrix: TomlTable) =
        if matrix.Count = 0 then
            error (definitionMessage definitionId "requires a non-empty matrix.")
        else
            matrix
            |> Seq.map (fun entry -> entry.Key, entry.Value)
            |> List.ofSeq
            |> traverse (fun _ (axisName, axisValue) ->
                if String.IsNullOrWhiteSpace axisName then
                    error (definitionMessage definitionId "contains an empty axis name.")
                else
                    match axisValue with
                    | :? TomlArray as values when values.Count > 0 ->
                        values
                        |> Seq.cast<obj | null>
                        |> List.ofSeq
                        |> traverse (fun index value ->
                            parseTomlValue
                                (concat
                                    [| "definitions."
                                       definitionId
                                       ".matrix."
                                       axisName
                                       "["
                                       invariantInt32 index
                                       "]" |])
                                value)
                        |> Result.map (fun parsed -> axisName, parsed)
                    | :? TomlArray ->
                        error (concat [| "Definition '"; definitionId; "' axis '"; axisName; "' must not be empty." |])
                    | _ ->
                        error (
                            concat
                                [| "Definition '"
                                   definitionId
                                   "' axis '"
                                   axisName
                                   "' must be a TOML array." |]
                        ))

    let private parseData definitionId (data: TomlTable) =
        parseTomlTable (concat [| "definitions."; definitionId; ".data" |]) data

    let private parseDefinition id name kind matrix data =
        match requiredText "Definition id" id with
        | Error errors -> Error errors
        | Ok definitionId ->
            match requiredText (definitionMessage definitionId "name") name with
            | Error errors -> Error errors
            | Ok nameTemplate ->
                match parseAxes definitionId matrix with
                | Error errors -> Error errors
                | Ok axes ->
                    parseData definitionId data
                    |> Result.map (fun parsedData ->
                        { Id = definitionId
                          NameTemplate = nameTemplate
                          Kind = kind
                          Axes = axes
                          Data = parsedData })

    let private parseDefinitions (model: MatrixTomlModel) =
        let stills =
            model.Stills
            |> Seq.map (fun definition ->
                parseDefinition definition.Id definition.Name Still definition.Matrix definition.Data)
            |> List.ofSeq

        let animations =
            model.Animations
            |> Seq.map (fun definition ->
                match requiredText "Animation workflow" definition.Workflow with
                | Error errors -> Error errors
                | Ok workflow ->
                    parseDefinition
                        definition.Id
                        definition.Name
                        (Animation workflow)
                        definition.Matrix
                        definition.Data)
            |> List.ofSeq

        let definitions = stills @ animations

        if List.isEmpty definitions then
            error "Matrix v1 requires at least one still or animation definition."
        else
            match traverse (fun _ definition -> definition) definitions with
            | Error errors -> Error errors
            | Ok parsed ->
                let seen = HashSet<string>(StringComparer.Ordinal)

                parsed
                |> List.tryFind (fun definition -> not (seen.Add definition.Id))
                |> function
                    | Some duplicate -> error (concat [| "Definition id '"; duplicate.Id; "' is duplicated." |])
                    | None -> Ok parsed

    let private scalarText definitionId placeholder value =
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
                    [| "Definition '"
                       definitionId
                       "' placeholder '{"
                       placeholder
                       "}' refers to a non-scalar axis value." |]
            )

    let private renderName definitionId (template: string) (axes: (string * TomlValue) list) =
        let values = Dictionary<string, TomlValue>(StringComparer.Ordinal)
        axes |> List.iter (fun (name, value) -> values.Add(name, value))
        let rendered = StringBuilder()

        let rec append index =
            if index >= template.Length then
                Ok(rendered.ToString())
            else
                match template[index] with
                | '}' -> error (definitionMessage definitionId "name contains an unmatched closing brace.")
                | '{' ->
                    let closingIndex = template.IndexOf('}', index + 1)

                    if closingIndex < 0 then
                        error (definitionMessage definitionId "name contains an unmatched opening brace.")
                    else
                        let placeholder = template.Substring(index + 1, closingIndex - index - 1)

                        if String.IsNullOrWhiteSpace placeholder || placeholder.Contains('{') then
                            error (definitionMessage definitionId "name contains an invalid placeholder.")
                        else
                            match values.TryGetValue placeholder with
                            | false, _ ->
                                error (
                                    concat
                                        [| "Definition '"
                                           definitionId
                                           "' name requires missing axis '"
                                           placeholder
                                           "'." |]
                                )
                            | true, value ->
                                match scalarText definitionId placeholder value with
                                | Error errors -> Error errors
                                | Ok text ->
                                    rendered.Append(text) |> ignore
                                    append (closingIndex + 1)
                | character ->
                    rendered.Append(character) |> ignore
                    append (index + 1)

        append 0

    let private validateLogicalName definitionId name =
        let invalidCharacters = [| '<'; '>'; ':'; '"'; '|'; '?'; '*' |]

        if String.IsNullOrWhiteSpace name then
            error (definitionMessage definitionId "expands to an empty logical name.")
        elif Path.IsPathRooted name || name.Contains('\\') then
            error (
                concat
                    [| "Definition '"
                       definitionId
                       "' expands to unsafe logical name '"
                       name
                       "'." |]
            )
        elif name.IndexOfAny invalidCharacters >= 0 || name |> Seq.exists Char.IsControl then
            error (
                concat
                    [| "Definition '"
                       definitionId
                       "' expands to unsafe logical name '"
                       name
                       "'." |]
            )
        else
            let segments = name.Split('/', StringSplitOptions.None)

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
                error (
                    concat
                        [| "Definition '"
                           definitionId
                           "' expands to unsafe logical name '"
                           name
                           "'." |]
                )
            elif not (String.IsNullOrEmpty(Path.GetExtension segments[segments.Length - 1])) then
                error (
                    concat
                        [| "Definition '"
                           definitionId
                           "' logical name '"
                           name
                           "' must be extensionless." |]
                )
            else
                Ok name

    let private expandAxes (axes: (string * TomlValue list) list) =
        axes
        |> List.fold
            (fun combinations (axisName, values) ->
                [ for combination in combinations do
                      for value in values do
                          yield combination @ [ axisName, value ] ])
            [ [] ]

    let private bindDevice definitionId devices axes =
        match axes |> List.tryFind (fun (name, _) -> name = "device") with
        | None -> error (definitionMessage definitionId "matrix must contain a device axis.")
        | Some(_, TomlValue.String deviceName) ->
            match Map.tryFind deviceName devices with
            | Some device -> Ok device
            | None ->
                error (
                    concat
                        [| "Definition '"
                           definitionId
                           "' references unknown device '"
                           deviceName
                           "'." |]
                )
        | Some _ -> error (definitionMessage definitionId "device axis values must be strings.")

    let private expandDefinitions (devices: Map<string, Device>) (definitions: MatrixDefinition list) =
        let logicalNames = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        let expandDefinition (definition: MatrixDefinition) =
            definition.Axes
            |> expandAxes
            |> traverse (fun _ axes ->
                match bindDevice definition.Id devices axes with
                | Error errors -> Error errors
                | Ok device ->
                    match renderName definition.Id definition.NameTemplate axes with
                    | Error errors -> Error errors
                    | Ok renderedName ->
                        match validateLogicalName definition.Id renderedName with
                        | Error errors -> Error errors
                        | Ok logicalName when not (logicalNames.Add logicalName) ->
                            error (
                                concat
                                    [| "Logical name '"
                                       logicalName
                                       "' is duplicated across capture definitions." |]
                            )
                        | Ok logicalName ->
                            let extension =
                                match definition.Kind with
                                | Still -> ".png"
                                | Animation _ -> ".webp"

                            Ok
                                { DefinitionId = definition.Id
                                  Kind = definition.Kind
                                  LogicalName = logicalName
                                  OutputRelativePath = String.Concat(logicalName, extension)
                                  Device = device
                                  Axes = axes
                                  Data = definition.Data })

        let rec loop captures remaining =
            match remaining with
            | [] -> Ok(List.rev captures |> List.concat)
            | definition :: tail ->
                match expandDefinition definition with
                | Error errors -> Error errors
                | Ok expanded -> loop (expanded :: captures) tail

        loop [] definitions

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

    let private validateFramesPerSecond (value: Nullable<int64>) =
        if not value.HasValue then
            Ok DefaultFramesPerSecond
        elif value.Value <= 0L || value.Value > int64 Int32.MaxValue then
            error (
                concat
                    [| "frames_per_second must be between 1 and "
                       invariantInt32 Int32.MaxValue
                       "." |]
            )
        else
            Ok(int value.Value)

    let private validateModel (request: CaptureRequest) (model: MatrixTomlModel) =
        if not model.Version.HasValue then
            error "version is required."
        elif model.Version.Value <> SupportedVersion then
            error (
                concat
                    [| "Unsupported Matrix version "
                       invariantInt64 model.Version.Value
                       "; expected "
                       invariantInt64 SupportedVersion
                       "." |]
            )
        else
            let matrixDirectory =
                Path.GetDirectoryName request.MatrixPath
                |> Option.ofObj
                |> Option.defaultValue Environment.CurrentDirectory

            match resolveFrom matrixDirectory "adapter" model.Adapter with
            | Error errors -> Error errors
            | Ok adapterPath ->
                let frameResult =
                    if String.IsNullOrWhiteSpace model.Frame then
                        Ok None
                    else
                        resolveFrom matrixDirectory "frame" model.Frame |> Result.map Some

                match frameResult with
                | Error errors -> Error errors
                | Ok framePath ->
                    let outputResult =
                        match request.OutputPath, model.DefaultOutput with
                        | Some outputPath, _ -> Ok(outputPath, [])
                        | None, defaultOutput when not (String.IsNullOrWhiteSpace defaultOutput) ->
                            resolveFrom matrixDirectory "default_output" defaultOutput
                            |> Result.map (fun outputPath ->
                                outputPath,
                                [ String.Concat("--output was not provided; using default_output: ", outputPath) ])
                        | None, _ -> error "--output is required when the matrix does not define default_output."

                    match outputResult with
                    | Error errors -> Error errors
                    | Ok(outputPath, warnings) ->
                        match validateFramesPerSecond model.FramesPerSecond with
                        | Error errors -> Error errors
                        | Ok framesPerSecond ->
                            match validateBrowserArguments model.BrowserArguments with
                            | Error errors -> Error errors
                            | Ok browserArguments ->
                                match parseDevices model.Devices with
                                | Error errors -> Error errors
                                | Ok devices ->
                                    match parseDefinitions model with
                                    | Error errors -> Error errors
                                    | Ok definitions ->
                                        let selectedDefinitions =
                                            match request.OnlyDefinitionId with
                                            | None -> Ok definitions
                                            | Some selectedId ->
                                                match
                                                    definitions |> List.tryFind (fun item -> item.Id = selectedId)
                                                with
                                                | Some definition -> Ok [ definition ]
                                                | None ->
                                                    error (
                                                        concat
                                                            [| "--only definition '"; selectedId; "' does not exist." |]
                                                    )

                                        match selectedDefinitions with
                                        | Error errors -> Error errors
                                        | Ok selected ->
                                            expandDefinitions devices selected
                                            |> Result.map (fun captures ->
                                                { MatrixPath = request.MatrixPath
                                                  OutputPath = outputPath
                                                  AdapterPath = adapterPath
                                                  FramePath = framePath
                                                  BrowserPath = request.BrowserPath
                                                  BrowserArguments = browserArguments
                                                  FramesPerSecond = framesPerSecond
                                                  Captures = captures
                                                  Warnings = warnings })

    let plan (request: CaptureRequest) =
        if not (File.Exists request.MatrixPath) then
            error (String.Concat("Matrix file does not exist: ", request.MatrixPath))
        else
            try
                let source = File.ReadAllText request.MatrixPath
                let model = MatrixTomlModels.Deserialize source
                validateModel request model
            with ex ->
                error (String.Concat("Matrix TOML could not be parsed: ", ex.Message))
