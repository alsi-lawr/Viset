namespace Viset

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography
open Viset.Serialization

type OutputBrowser =
    { Version: string
      Source: string }

    override browser.ToString() = browser.Version

type CapturedFile =
    { Capture: PlannedCapture
      Bytes: byte array
      FrameTicksMs: int list }

    override file.ToString() = file.Capture.OutputRelativePath

type OutputWriteResult =
    { MarkerPath: string
      ManifestPath: string
      WrittenFiles: string list }

    override result.ToString() = result.ManifestPath

module Output =
    [<Literal>]
    let private SchemaVersion = 1L

    [<Literal>]
    let private Owner = "viset"

    [<Literal>]
    let private MarkerFileName = ".viset"

    [<Literal>]
    let private ManifestFileName = "manifest.toml"

    let private pathComparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let private isLinkOrReparse (info: FileSystemInfo) =
        info.LinkTarget |> Option.ofObj |> Option.isSome
        || info.Exists && info.Attributes.HasFlag FileAttributes.ReparsePoint

    let private ensureDirectoryIsNotLinked path =
        let directory = DirectoryInfo path

        if isLinkOrReparse directory then
            raise (InvalidDataException(String.Concat("Output path is a link or reparse point: ", path)))

    let private normalizeRelativePath (relativePath: string) =
        if String.IsNullOrWhiteSpace relativePath then
            raise (InvalidDataException "Output path must not be empty.")

        relativePath.Replace('/', Path.DirectorySeparatorChar)

    let private containedPath root relativePath =
        let normalizedRoot = Path.GetFullPath root
        let candidate = Path.GetFullPath(normalizeRelativePath relativePath, normalizedRoot)

        let rootPrefix =
            String.Concat(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), Path.DirectorySeparatorChar)

        if not (candidate.StartsWith(rootPrefix, pathComparison)) then
            raise (InvalidDataException(String.Concat("Output path escapes the owned root: ", relativePath)))

        candidate

    let private ensureContainedAncestors (root: string) (path: string) =
        ensureDirectoryIsNotLinked root
        let normalizedRoot = Path.GetFullPath root
        let mutable current = Path.GetDirectoryName path |> Option.ofObj

        while current.IsSome
              && not (String.Equals(current.Value, normalizedRoot, pathComparison)) do
            if Directory.Exists current.Value then
                ensureDirectoryIsNotLinked current.Value

            current <- Path.GetDirectoryName current.Value |> Option.ofObj

    let private validateMarker (marker: OutputMarkerTomlModel) =
        if marker.Version <> Nullable SchemaVersion then
            raise (InvalidDataException ".viset version must be 1.")

        if not (String.Equals(marker.Owner, Owner, StringComparison.Ordinal)) then
            raise (InvalidDataException ".viset owner must be 'viset'.")

        if not (String.Equals(marker.Manifest, ManifestFileName, StringComparison.Ordinal)) then
            raise (InvalidDataException ".viset manifest must be 'manifest.toml'.")

    let private outputRootIsEmpty root =
        Directory.EnumerateFileSystemEntries root |> Seq.isEmpty

    let private prepareOwnedRoot root =
        let fullRoot = Path.GetFullPath root

        if Directory.Exists fullRoot then
            ensureDirectoryIsNotLinked fullRoot
            let markerPath = Path.Combine(fullRoot, MarkerFileName)

            if File.Exists markerPath then
                File.ReadAllText markerPath
                |> OutputTomlModels.DeserializeMarker
                |> validateMarker
            elif not (outputRootIsEmpty fullRoot) then
                raise (InvalidDataException(String.Concat("Output directory is not owned by Viset: ", fullRoot)))
        else
            Directory.CreateDirectory fullRoot |> ignore

        fullRoot

    let validateRoot root = prepareOwnedRoot root |> ignore

    let private markerText () =
        let marker = OutputMarkerTomlModel()
        marker.Version <- Nullable SchemaVersion
        marker.Owner <- Owner
        marker.Manifest <- ManifestFileName
        OutputTomlModels.SerializeMarker marker

    let private requireText label value =
        if String.IsNullOrWhiteSpace value then
            raise (InvalidDataException(String.Concat("manifest.toml requires ", label, ".")))

        value

    let private validateSha256 value =
        let digest = requireText "files.sha256" value

        if digest.Length <> 64 || not (digest |> Seq.forall Char.IsAsciiHexDigit) then
            raise (InvalidDataException "manifest.toml contains an invalid SHA-256 digest.")

        digest.ToLowerInvariant()

    let private validateManifest root (manifest: OutputManifestTomlModel) =
        if manifest.Version <> Nullable SchemaVersion then
            raise (InvalidDataException "manifest.toml version must be 1.")

        if not (String.Equals(manifest.Owner, Owner, StringComparison.Ordinal)) then
            raise (InvalidDataException "manifest.toml owner must be 'viset'.")

        requireText "tool.name" manifest.Tool.Name |> ignore
        requireText "tool.version" manifest.Tool.Version |> ignore
        requireText "browser.version" manifest.Browser.Version |> ignore
        requireText "browser.source" manifest.Browser.Source |> ignore

        for file in manifest.Files do
            requireText "files.definition_id" file.DefinitionId |> ignore
            requireText "files.logical_name" file.LogicalName |> ignore
            requireText "files.kind" file.Kind |> ignore
            validateSha256 file.Sha256 |> ignore
            containedPath root (requireText "files.path" file.Path) |> ignore

            if file.FrameTicksMs |> Seq.exists (fun value -> value <= 0L) then
                raise (InvalidDataException "manifest.toml frame_ticks_ms values must be positive.")

        manifest

    let private readExistingManifest root =
        let path = Path.Combine(root, ManifestFileName)

        if File.Exists path then
            File.ReadAllText path
            |> OutputTomlModels.DeserializeManifest
            |> validateManifest root
            |> Some
        else
            None

    let private kindText (capture: PlannedCapture) =
        match capture.Kind with
        | Still -> "still"
        | Animation _ -> "animation"

    let private sha256 (bytes: byte array) =
        let digest = SHA256.HashData bytes
        Convert.ToHexString(digest).ToLowerInvariant()

    let private manifestFile (captured: CapturedFile) =
        let file = OutputFileTomlModel()
        file.DefinitionId <- captured.Capture.DefinitionId
        file.LogicalName <- captured.Capture.LogicalName
        file.Path <- captured.Capture.OutputRelativePath.Replace(Path.DirectorySeparatorChar, '/')
        file.Kind <- kindText captured.Capture
        file.Sha256 <- sha256 captured.Bytes

        file.FrameTicksMs <-
            match captured.FrameTicksMs with
            | [] -> ResizeArray<int64>()
            | ticks -> ticks |> List.map int64 |> ResizeArray

        file

    let private mergeFiles
        (plan: CapturePlan)
        (existing: OutputManifestTomlModel option)
        (newFiles: OutputFileTomlModel list)
        =
        if plan.SelectedDefinitionIds.Length = plan.DefinitionIds.Length then
            List.ofSeq newFiles
        else
            let selected = HashSet<string>(plan.SelectedDefinitionIds, StringComparer.Ordinal)

            let byDefinition =
                Dictionary<string, ResizeArray<OutputFileTomlModel>>(StringComparer.Ordinal)

            let add (file: OutputFileTomlModel) =
                match byDefinition.TryGetValue file.DefinitionId with
                | true, files -> files.Add file
                | false, _ ->
                    let files = ResizeArray<OutputFileTomlModel>()
                    files.Add file
                    byDefinition.Add(file.DefinitionId, files)

            existing
            |> Option.iter (fun manifest ->
                manifest.Files
                |> Seq.filter (fun file -> not (selected.Contains file.DefinitionId))
                |> Seq.iter add)

            newFiles |> Seq.iter add

            let ordered = ResizeArray<OutputFileTomlModel>()

            for definitionId in plan.DefinitionIds do
                match byDefinition.TryGetValue definitionId with
                | true, files -> ordered.AddRange files
                | false, _ -> ()

            existing
            |> Option.iter (fun manifest ->
                manifest.Files
                |> Seq.filter (fun file ->
                    not (plan.DefinitionIds |> List.contains file.DefinitionId)
                    && not (selected.Contains file.DefinitionId))
                |> Seq.iter ordered.Add)

            List.ofSeq ordered

    let write toolVersion (browser: OutputBrowser) (plan: CapturePlan) (capturedFiles: CapturedFile list) =
        ArgumentException.ThrowIfNullOrWhiteSpace toolVersion
        ArgumentNullException.ThrowIfNull browser

        if capturedFiles.Length <> plan.Captures.Length then
            invalidArg (nameof capturedFiles) "Every planned capture must have exactly one output."

        let expectedPaths =
            plan.Captures |> List.map (fun capture -> capture.OutputRelativePath)

        let actualPaths =
            capturedFiles |> List.map (fun captured -> captured.Capture.OutputRelativePath)

        if expectedPaths <> actualPaths then
            invalidArg (nameof capturedFiles) "Captured outputs must preserve planned logical order."

        let root = prepareOwnedRoot plan.OutputPath

        let existing =
            if plan.SelectedDefinitionIds.Length = plan.DefinitionIds.Length then
                None
            else
                readExistingManifest root

        let written = ResizeArray<string>()

        for captured in capturedFiles do
            ArgumentNullException.ThrowIfNull captured.Bytes

            if captured.Bytes.Length = 0 then
                invalidArg (nameof capturedFiles) "Captured output bytes must not be empty."

            let path = containedPath root captured.Capture.OutputRelativePath
            let parent = Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue root
            Directory.CreateDirectory parent |> ignore
            ensureContainedAncestors root path

            let fileInfo = FileInfo path

            if isLinkOrReparse fileInfo then
                raise (InvalidDataException(String.Concat("Output file is a link or reparse point: ", path)))

            File.WriteAllBytes(path, captured.Bytes)
            written.Add path

        let markerPath = Path.Combine(root, MarkerFileName)
        File.WriteAllText(markerPath, markerText ())

        let manifest = OutputManifestTomlModel()
        manifest.Version <- Nullable SchemaVersion
        manifest.Owner <- Owner
        manifest.Tool <- OutputToolTomlModel(Name = "viset", Version = toolVersion)
        manifest.Browser <- OutputBrowserTomlModel(Version = browser.Version, Source = browser.Source)

        manifest.Files <-
            capturedFiles
            |> List.map manifestFile
            |> mergeFiles plan existing
            |> ResizeArray

        let manifestPath = Path.Combine(root, ManifestFileName)
        File.WriteAllText(manifestPath, OutputTomlModels.SerializeManifest manifest)

        { MarkerPath = markerPath
          ManifestPath = manifestPath
          WrittenFiles = List.ofSeq written }
