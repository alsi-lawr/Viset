namespace Viset

open System
open System.IO

module Cli =
    [<Literal>]
    let version = "0.1.0"

    let usage =
        String.Join(
            Environment.NewLine,
            [| "Usage:"
               "  viset capture MATRIX [--output DIR] [--only ID] [--browser PATH]"
               "  viset browser install"
               "  viset --version"
               "  viset --help" |]
        )

    let versionText = String.Concat("viset ", version)

    let private resolvePath label currentDirectory value =
        if String.IsNullOrWhiteSpace value then
            Error(String.Concat(label, " requires a non-empty path."))
        else
            try
                Ok(Path.GetFullPath(value, currentDirectory))
            with
            | :? ArgumentException
            | :? NotSupportedException
            | :? PathTooLongException -> Error(String.Concat(label, " is not a valid path: ", value))

    let private parseCapture currentDirectory matrixArgument optionArguments =
        match resolvePath "MATRIX" currentDirectory matrixArgument with
        | Error message -> Error message
        | Ok matrixPath when
            not (String.Equals(Path.GetExtension matrixPath, ".toml", StringComparison.OrdinalIgnoreCase))
            ->
            Error "MATRIX must be a TOML file with a .toml extension."
        | Ok matrixPath ->
            let rec parseOptions remaining outputPath onlyDefinitionId browserPath =
                let requireValue optionName (tail: string list) continuation =
                    match tail with
                    | value :: rest when not (value.StartsWith("--", StringComparison.Ordinal)) ->
                        continuation value rest
                    | _ -> Error(String.Concat(optionName, " requires a value."))

                match remaining with
                | [] ->
                    Ok
                        { MatrixPath = matrixPath
                          OutputPath = outputPath
                          OnlyDefinitionId = onlyDefinitionId
                          BrowserPath = browserPath }
                | "--output" :: tail when outputPath.IsSome -> Error "--output may be specified only once."
                | "--output" :: tail ->
                    requireValue "--output" tail (fun value rest ->
                        match resolvePath "--output" currentDirectory value with
                        | Ok path -> parseOptions rest (Some path) onlyDefinitionId browserPath
                        | Error message -> Error message)
                | "--only" :: tail when onlyDefinitionId.IsSome -> Error "--only may be specified only once."
                | "--only" :: tail ->
                    requireValue "--only" tail (fun value rest ->
                        if String.IsNullOrWhiteSpace value then
                            Error "--only requires a non-empty definition ID."
                        else
                            parseOptions rest outputPath (Some value) browserPath)
                | "--browser" :: tail when browserPath.IsSome -> Error "--browser may be specified only once."
                | "--browser" :: tail ->
                    requireValue "--browser" tail (fun value rest ->
                        match resolvePath "--browser" currentDirectory value with
                        | Ok path -> parseOptions rest outputPath onlyDefinitionId (Some path)
                        | Error message -> Error message)
                | argument :: _ when argument.StartsWith("--", StringComparison.Ordinal) ->
                    Error(String.Concat("Unknown capture option: ", argument))
                | argument :: _ -> Error(String.Concat("Unexpected capture argument: ", argument))

            parseOptions optionArguments None None None

    let parse currentDirectory arguments =
        match List.ofArray arguments with
        | [ "--help" ] -> Ok Command.Help
        | [ "--version" ] -> Ok Command.Version
        | [ "browser"; "install" ] -> Ok Command.BrowserInstall
        | "capture" :: matrixArgument :: optionArguments ->
            parseCapture currentDirectory matrixArgument optionArguments
            |> Result.map Command.Capture
        | [] -> Error "A command is required."
        | "capture" :: [] -> Error "capture requires MATRIX."
        | "browser" :: _ -> Error "The only supported browser command is: browser install"
        | command :: _ -> Error(String.Concat("Unknown command: ", command))
