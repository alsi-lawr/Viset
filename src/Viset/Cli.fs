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
               "  viset capture CAPTURE.lua [--output DIR] [--browser PATH] [--force]"
               "  viset init [DIR] [-i|--interactive] [--force]"
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

    let private parseCapture currentDirectory scriptArgument optionArguments =
        match resolvePath "CAPTURE" currentDirectory scriptArgument with
        | Error message -> Error message
        | Ok scriptPath when
            not (String.Equals(Path.GetExtension scriptPath, ".lua", StringComparison.OrdinalIgnoreCase))
            ->
            Error "CAPTURE must be a Lua file with a .lua extension."
        | Ok scriptPath ->
            let rec parseOptions remaining outputPath browserPath force =
                let requireValue optionName (tail: string list) continuation =
                    match tail with
                    | value :: rest when not (value.StartsWith("--", StringComparison.Ordinal)) ->
                        continuation value rest
                    | _ -> Error(String.Concat(optionName, " requires a value."))

                match remaining with
                | [] ->
                    Ok
                        { ScriptPath = scriptPath
                          OutputPath = outputPath
                          BrowserPath = browserPath
                          Force = force }
                | "--output" :: tail when outputPath.IsSome -> Error "--output may be specified only once."
                | "--output" :: tail ->
                    requireValue "--output" tail (fun value rest ->
                        match resolvePath "--output" currentDirectory value with
                        | Ok path -> parseOptions rest (Some path) browserPath force
                        | Error message -> Error message)
                | "--browser" :: tail when browserPath.IsSome -> Error "--browser may be specified only once."
                | "--browser" :: tail ->
                    requireValue "--browser" tail (fun value rest ->
                        match resolvePath "--browser" currentDirectory value with
                        | Ok path -> parseOptions rest outputPath (Some path) force
                        | Error message -> Error message)
                | "--force" :: _ when force -> Error "--force may be specified only once."
                | "--force" :: tail -> parseOptions tail outputPath browserPath true
                | argument :: _ when argument.StartsWith("--", StringComparison.Ordinal) ->
                    Error(String.Concat("Unknown capture option: ", argument))
                | argument :: _ -> Error(String.Concat("Unexpected capture argument: ", argument))

            parseOptions optionArguments None None false

    let private parseInit currentDirectory arguments =
        let rec parseOptions remaining targetDirectory interactive force =
            match remaining with
            | [] ->
                Ok
                    { TargetDirectory = targetDirectory |> Option.defaultValue currentDirectory
                      Interactive = interactive
                      Force = force }
            | ("-i" | "--interactive") :: _ when interactive -> Error "--interactive may be specified only once."
            | ("-i" | "--interactive") :: tail -> parseOptions tail targetDirectory true force
            | "--force" :: _ when force -> Error "--force may be specified only once."
            | "--force" :: tail -> parseOptions tail targetDirectory interactive true
            | argument :: _ when argument.StartsWith("-", StringComparison.Ordinal) ->
                Error(String.Concat("Unknown init option: ", argument))
            | _ :: _ when targetDirectory.IsSome -> Error "init accepts at most one target directory."
            | directory :: tail ->
                match resolvePath "DIR" currentDirectory directory with
                | Ok path -> parseOptions tail (Some path) interactive force
                | Error message -> Error message

        parseOptions arguments None false false

    let parse currentDirectory arguments =
        match List.ofArray arguments with
        | [ "--help" ] -> Ok Command.Help
        | [ "--version" ] -> Ok Command.Version
        | [ "browser"; "install" ] -> Ok Command.BrowserInstall
        | "init" :: arguments -> parseInit currentDirectory arguments |> Result.map Command.Init
        | "capture" :: scriptArgument :: optionArguments ->
            parseCapture currentDirectory scriptArgument optionArguments
            |> Result.map Command.Capture
        | [] -> Error "A command is required."
        | "capture" :: [] -> Error "capture requires CAPTURE.lua."
        | "browser" :: _ -> Error "The only supported browser command is: browser install"
        | command :: _ -> Error(String.Concat("Unknown command: ", command))
