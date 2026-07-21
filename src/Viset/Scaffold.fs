namespace Viset

open System
open System.Globalization
open System.IO
open System.Text

type ScaffoldResult =
    { DirectoryPath: string
      CapturePath: string }

    override result.ToString() = result.DirectoryPath

module Scaffold =
    type private Settings =
        { PageUrl: string
          OutputPath: string
          ViewportWidth: int
          ViewportHeight: int }

        override settings.ToString() = settings.OutputPath

    type private LegacyNeovimScaffold =
        { QueryFile: string
          AncestorDirectories: string list }

    [<Literal>]
    let private DefaultPageUrl =
        "data:text/html;charset=utf-8,%3C!doctype%20html%3E%3Chtml%20lang%3D%22en%22%3E%3Cmeta%20charset%3D%22utf-8%22%3E%3Cmeta%20name%3D%22viewport%22%20content%3D%22width%3Ddevice-width%2Cinitial-scale%3D1%22%3E%3Ctitle%3EViset%3C%2Ftitle%3E%3Cstyle%3Ehtml%2Cbody%7Bwidth%3A100%25%3Bheight%3A100%25%3Bmargin%3A0%7Dbody%7Bdisplay%3Agrid%3Bplace-items%3Acenter%3Bbackground%3A%23131a2a%3Bcolor%3A%23f5f7ff%3Bfont%3A600%2032px%20system-ui%7D%3C%2Fstyle%3E%3Cbody%3EViset%20is%20ready%3C%2Fbody%3E%3C%2Fhtml%3E"

    let private defaults =
        { PageUrl = DefaultPageUrl
          OutputPath = "output/example.png"
          ViewportWidth = 1280
          ViewportHeight = 720 }

    let private generatedFileNames =
        [ "capture.lua"
          "README.md"
          ".gitignore"
          ".luarc.json"
          Path.Combine(".viset", "viset.d.lua") ]

    let private generatedDirectoryNames = [ ".viset" ]

    let private legacyNeovimScaffold =
        { QueryFile = Path.Combine(".viset", "nvim", "queries", "lua", "injections.scm")
          AncestorDirectories =
            [ Path.Combine(".viset", "nvim")
              Path.Combine(".viset", "nvim", "queries")
              Path.Combine(".viset", "nvim", "queries", "lua") ] }

    let private entryExists path =
        if File.Exists path || Directory.Exists path then
            true
        else
            try
                File.GetAttributes path |> ignore
                true
            with
            | :? FileNotFoundException
            | :? DirectoryNotFoundException -> false

    let private isLink path =
        entryExists path
        && (File.GetAttributes path).HasFlag FileAttributes.ReparsePoint

    let private generatedPaths directory =
        generatedFileNames
        |> List.map (fun fileName -> Path.Combine(directory, fileName))

    let private legacyAncestorPaths directory =
        legacyNeovimScaffold.AncestorDirectories
        |> List.map (fun path -> Path.Combine(directory, path))

    let private validateForcedTarget (request: InitRequest) =
        match request.TargetDirectory |> legacyAncestorPaths |> List.tryFind isLink with
        | Some path -> Error(String.Concat("Legacy Neovim scaffold directory must not be a link: ", path))
        | None ->
            let legacyQuery =
                Path.Combine(request.TargetDirectory, legacyNeovimScaffold.QueryFile)

            if Directory.Exists legacyQuery && not (isLink legacyQuery) then
                Error(String.Concat("A directory occupies the legacy Neovim scaffold file path: ", legacyQuery))
            else
                request.TargetDirectory
                |> generatedPaths
                |> List.tryFind Directory.Exists
                |> function
                    | Some path -> Error(String.Concat("A directory occupies a scaffold file path: ", path))
                    | None -> Ok()

    let private validateTarget (request: InitRequest) =
        if isLink request.TargetDirectory then
            Error(String.Concat("Initialization target must not be a link: ", request.TargetDirectory))
        elif File.Exists request.TargetDirectory then
            Error(String.Concat("Initialization target is a file: ", request.TargetDirectory))
        elif
            generatedDirectoryNames
            |> List.map (fun path -> Path.Combine(request.TargetDirectory, path))
            |> List.tryFind (fun path -> File.Exists path || isLink path)
            |> Option.isSome
        then
            Error "Scaffold editor-support directories must not be files or links."
        elif request.Force then
            validateForcedTarget request
        else
            let conflicts = request.TargetDirectory |> generatedPaths |> List.filter entryExists

            match conflicts with
            | [] -> Ok()
            | _ ->
                Error(
                    String.Concat(
                        "Scaffold files already exist; use --force to replace them: ",
                        String.Join(", ", conflicts)
                    )
                )

    let private validateAbsoluteUrl value =
        try
            let uri = Uri(value, UriKind.Absolute)

            if String.IsNullOrWhiteSpace uri.Scheme then
                Error "Page URL must be an absolute URL."
            else
                Ok value
        with :? UriFormatException ->
            Error "Page URL must be an absolute URL."

    let private validateOutputPath value =
        let invalidCharacters =
            [| '<'; '>'; ':'; '"'; '|'; '?'; '*'; '#'; '!'; '['; ']'; '{'; '}' |]

        if String.IsNullOrWhiteSpace value then
            Error "Output file must not be empty."
        elif Path.IsPathRooted value || value.Contains('\\') then
            Error "Output file must be a project-relative path using forward slashes."
        elif value.IndexOfAny invalidCharacters >= 0 || value |> Seq.exists Char.IsControl then
            Error "Output file contains unsafe characters."
        elif not (value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) then
            Error "The generated scaffold output file must end in .png."
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
                Error "Output file must not contain empty, dot, or traversal segments."
            else
                Ok value

    let private validateDimension label (value: string) =
        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, dimension when dimension > 0 -> Ok dimension
        | _ -> Error(String.Concat(label, " must be a positive integer."))

    let private prompt label displayedDefault defaultValue validator =
        let rec read () =
            Console.Out.Write(String.Concat(label, " [", displayedDefault, "]: "))
            Console.Out.Flush()

            match Console.ReadLine() with
            | null -> Error "Interactive input ended before initialization completed."
            | value ->
                let candidate =
                    if String.IsNullOrWhiteSpace value then
                        defaultValue
                    else
                        value.Trim()

                match validator candidate with
                | Ok result -> Ok result
                | Error message ->
                    Console.Error.WriteLine(String.Concat("error: ", message))
                    read ()

        read ()

    let private interactiveSettings () =
        match prompt "Page URL" "built-in page" defaults.PageUrl validateAbsoluteUrl with
        | Error message -> Error message
        | Ok pageUrl ->
            match prompt "Output file" defaults.OutputPath defaults.OutputPath validateOutputPath with
            | Error message -> Error message
            | Ok outputPath ->
                match
                    prompt
                        "Viewport width"
                        (defaults.ViewportWidth.ToString(CultureInfo.InvariantCulture))
                        (defaults.ViewportWidth.ToString(CultureInfo.InvariantCulture))
                        (validateDimension "Viewport width")
                with
                | Error message -> Error message
                | Ok viewportWidth ->
                    match
                        prompt
                            "Viewport height"
                            (defaults.ViewportHeight.ToString(CultureInfo.InvariantCulture))
                            (defaults.ViewportHeight.ToString(CultureInfo.InvariantCulture))
                            (validateDimension "Viewport height")
                    with
                    | Error message -> Error message
                    | Ok viewportHeight ->
                        Ok
                            { PageUrl = pageUrl
                              OutputPath = outputPath
                              ViewportWidth = viewportWidth
                              ViewportHeight = viewportHeight }

    let private escapeTomlString (value: string) =
        let escaped = StringBuilder(value.Length)

        value
        |> Seq.iter (fun character ->
            match character with
            | '"' -> escaped.Append("\\\"") |> ignore
            | '\\' -> escaped.Append("\\\\") |> ignore
            | '\b' -> escaped.Append("\\b") |> ignore
            | '\t' -> escaped.Append("\\t") |> ignore
            | '\n' -> escaped.Append("\\n") |> ignore
            | '\f' -> escaped.Append("\\f") |> ignore
            | '\r' -> escaped.Append("\\r") |> ignore
            | value when Char.IsControl value ->
                escaped.Append("\\u") |> ignore

                escaped.Append((int value).ToString("X4", CultureInfo.InvariantCulture))
                |> ignore
            | value -> escaped.Append value |> ignore)

        escaped.ToString()

    let private capture (settings: Settings) =
        String.Join(
            "\n",
            [| "--[["
               "# viset"
               "version = 1"
               String.Concat("output = \"", escapeTomlString settings.OutputPath, "\"")
               "browser_arguments = []"
               ""
               "[devices.desktop]"
               "mobile = false"
               "touch = false"
               "device_scale = 1.0"
               ""
               "[devices.desktop.viewport]"
               String.Concat("width = ", settings.ViewportWidth.ToString(CultureInfo.InvariantCulture))
               String.Concat("height = ", settings.ViewportHeight.ToString(CultureInfo.InvariantCulture))
               ""
               "[data]"
               String.Concat("url = \"", escapeTomlString settings.PageUrl, "\"")
               "]]"
               ""
               "local url = viset.context.data.url"
               "---@cast url string"
               "viset.page.navigate(url)"
               "viset.page.wait_for(viset.javascript [=["
               "  document.readyState === \"complete\""
               "]=], \"10s\")"
               "viset.snapshot()"
               "" |]
        )

    let private readme (settings: Settings) =
        String.Join(
            "\n",
            [| "# Viset capture project"
               ""
               "Edit `capture.lua`, then run:"
               ""
               "```sh"
               "viset capture capture.lua"
               "```"
               ""
               String.Concat("Generated output: [`", settings.OutputPath, "`](", settings.OutputPath, ")")
               ""
               String.Concat("![Generated Viset capture](", settings.OutputPath, ")")
               ""
               "Capture files are trusted local Lua code and run with Lua's standard libraries."
               ""
               "## Editor support"
               ""
               "`.luarc.json` loads `.viset/viset.d.lua` for Viset API completion and diagnostics in Lua Language Server."
               ""
               "Install [`getviset/viset.nvim`](https://github.com/getviset/viset.nvim) with your Neovim plugin manager for TOML header and `viset.javascript` highlighting; no setup call is required."
               ""
               "The plugin requires Neovim 0.12 or newer and the Lua, TOML, and JavaScript Tree-sitter parsers. Run `:checkhealth viset` for diagnostics."
               "" |]
        )

    let private gitignore (settings: Settings) =
        let segments = settings.OutputPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
        let suffix = if segments.Length = 1 then String.Empty else "/"
        String.Concat("/", segments[0], suffix, "\n")

    let private deleteLink path =
        if isLink path then
            File.Delete path

    let private writeFile (path: string) (content: string) =
        deleteLink path

        Path.GetDirectoryName path
        |> Option.ofObj
        |> Option.iter (fun directory -> Directory.CreateDirectory directory |> ignore)

        File.WriteAllText(path, content, UTF8Encoding(false))

    let private removeLegacyNeovimScaffold directory =
        let ancestorPaths = legacyAncestorPaths directory

        match ancestorPaths |> List.tryFind isLink with
        | Some path -> invalidOp (String.Concat("Legacy Neovim scaffold directory must not be a link: ", path))
        | None ->
            let queryPath = Path.Combine(directory, legacyNeovimScaffold.QueryFile)

            if isLink queryPath || File.Exists queryPath then
                File.Delete queryPath
            elif Directory.Exists queryPath then
                invalidOp (String.Concat("A directory occupies the legacy Neovim scaffold file path: ", queryPath))

            ancestorPaths
            |> List.rev
            |> List.iter (fun path ->
                if isLink path then
                    invalidOp (String.Concat("Legacy Neovim scaffold directory must not be a link: ", path))
                elif
                    Directory.Exists path
                    && (Directory.EnumerateFileSystemEntries path |> Seq.isEmpty)
                then
                    Directory.Delete path)

    let run request =
        try
            match validateTarget request with
            | Error message -> Error message
            | Ok() ->
                let settings =
                    if request.Interactive then
                        interactiveSettings ()
                    else
                        Ok defaults

                match settings with
                | Error message -> Error message
                | Ok values ->
                    Directory.CreateDirectory request.TargetDirectory |> ignore

                    writeFile (Path.Combine(request.TargetDirectory, "capture.lua")) (capture values)
                    writeFile (Path.Combine(request.TargetDirectory, "README.md")) (readme values)
                    writeFile (Path.Combine(request.TargetDirectory, ".gitignore")) (gitignore values)

                    writeFile
                        (Path.Combine(request.TargetDirectory, ".luarc.json"))
                        EditorSupport.LuaLanguageServerConfiguration

                    writeFile
                        (Path.Combine(request.TargetDirectory, ".viset", "viset.d.lua"))
                        EditorSupport.LuaDefinitions

                    if request.Force then
                        removeLegacyNeovimScaffold request.TargetDirectory

                    Ok
                        { DirectoryPath = request.TargetDirectory
                          CapturePath = Path.Combine(request.TargetDirectory, "capture.lua") }
        with error ->
            Error(String.Concat("Project initialization failed: ", error.Message))
