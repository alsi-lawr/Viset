namespace Viset

open System
open System.Buffers
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks

module internal BrowserInstallInternals =
    let private installLockTimeout = TimeSpan.FromMinutes 5.0
    let private installLockRetryDelay = TimeSpan.FromMilliseconds 100.0

    let unsupportedDiagnostic (runtimeIdentifier: string) =
        String.Concat(
            "Managed browser installation is not supported for ",
            runtimeIdentifier,
            "; set VISET_BROWSER or install Chrome, Chromium, or Edge on PATH."
        )

    let acquireInstallLockAsync (lockPath: string) (cancellationToken: CancellationToken) =
        task {
            let stopwatch = Stopwatch.StartNew()
            let mutable acquired: FileStream option = None

            while acquired.IsNone do
                cancellationToken.ThrowIfCancellationRequested()

                try
                    acquired <-
                        Some(
                            new FileStream(
                                lockPath,
                                FileMode.OpenOrCreate,
                                FileAccess.ReadWrite,
                                FileShare.None,
                                1,
                                FileOptions.Asynchronous
                            )
                        )
                with :? IOException when stopwatch.Elapsed < installLockTimeout ->
                    do! Task.Delay(installLockRetryDelay, cancellationToken)

                if acquired.IsNone && stopwatch.Elapsed >= installLockTimeout then
                    raise (
                        TimeoutException(
                            String.Concat(
                                "Timed out waiting for the managed browser install lock after ",
                                installLockTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                                " ms."
                            )
                        )
                    )

            return acquired.Value
        }

    let downloadAndVerifyAsync
        (platform: BrowserPlatformLock)
        (archivePath: string)
        (downloadTimeout: TimeSpan)
        (cancellationToken: CancellationToken)
        =
        task {
            use timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

            timeoutCancellation.CancelAfter downloadTimeout

            try
                use httpClient = new HttpClient()
                httpClient.Timeout <- Timeout.InfiniteTimeSpan

                use! response =
                    httpClient.GetAsync(
                        platform.Url,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutCancellation.Token
                    )

                response.EnsureSuccessStatusCode() |> ignore
                use! source = response.Content.ReadAsStreamAsync timeoutCancellation.Token

                use destination =
                    new FileStream(
                        archivePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        FileOptions.Asynchronous ||| FileOptions.SequentialScan
                    )

                use digest = IncrementalHash.CreateHash HashAlgorithmName.SHA256
                let buffer = ArrayPool<byte>.Shared.Rent 81920

                try
                    let mutable complete = false

                    while not complete do
                        let! read = source.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCancellation.Token)

                        if read = 0 then
                            complete <- true
                        else
                            digest.AppendData(buffer, 0, read)
                            do! destination.WriteAsync(buffer.AsMemory(0, read), timeoutCancellation.Token)
                finally
                    ArrayPool<byte>.Shared.Return buffer

                do! destination.FlushAsync timeoutCancellation.Token

                let actualDigest =
                    digest.GetHashAndReset()
                    |> Convert.ToHexString
                    |> fun value -> value.ToLowerInvariant()

                if not (String.Equals(actualDigest, platform.Sha256, StringComparison.Ordinal)) then
                    raise (
                        InvalidDataException(
                            String.Concat(
                                "Browser archive SHA-256 mismatch for ",
                                platform.RuntimeIdentifier,
                                ": expected ",
                                platform.Sha256,
                                ", received ",
                                actualDigest,
                                "."
                            )
                        )
                    )
            with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                raise (
                    TimeoutException(
                        String.Concat(
                            "Browser download exceeded ",
                            downloadTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                            " ms."
                        )
                    )
                )
        }

    let private canonicalArchivePath (entryName: string) =
        if String.IsNullOrWhiteSpace entryName then
            raise (InvalidDataException "Browser archive contains an empty entry name.")

        if entryName.Contains('\\') then
            raise (
                InvalidDataException(
                    String.Concat("Browser archive entry contains a backslash and was rejected: ", entryName)
                )
            )

        if
            Path.IsPathRooted entryName
            || entryName.StartsWith("/", StringComparison.Ordinal)
        then
            raise (InvalidDataException(String.Concat("Browser archive entry must be relative: ", entryName)))

        let segments = entryName.Split('/', StringSplitOptions.None)

        let unsafeSegment =
            segments
            |> Array.mapi (fun index segment -> index, segment)
            |> Array.exists (fun (index, segment) ->
                let finalEmptyDirectorySegment = index = segments.Length - 1 && segment.Length = 0

                not finalEmptyDirectorySegment
                && (String.IsNullOrWhiteSpace segment
                    || String.Equals(segment, ".", StringComparison.Ordinal)
                    || String.Equals(segment, "..", StringComparison.Ordinal)
                    || segment.Contains(':')))

        if unsafeSegment then
            raise (InvalidDataException(String.Concat("Browser archive entry has an unsafe path: ", entryName)))

        entryName.TrimEnd('/')

    let private unixFileType (entry: ZipArchiveEntry) =
        (entry.ExternalAttributes >>> 16) &&& 0xF000

    let private isWindowsReparsePoint (entry: ZipArchiveEntry) =
        entry.ExternalAttributes &&& int FileAttributes.ReparsePoint <> 0

    let private normalizeLinkTarget linkPath (target: string) =
        if
            String.IsNullOrWhiteSpace target
            || target.Length > 4096
            || target.Contains(char 0)
        then
            raise (InvalidDataException(String.Concat("Browser archive link has an invalid target: ", linkPath)))

        if
            target.Contains('\\')
            || target.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted target
        then
            raise (InvalidDataException(String.Concat("Browser archive link target must be relative: ", linkPath)))

        let stack = ResizeArray<string>()
        let linkParentIndex = linkPath.LastIndexOf('/')

        if linkParentIndex >= 0 then
            stack.AddRange(linkPath.Substring(0, linkParentIndex).Split('/', StringSplitOptions.RemoveEmptyEntries))

        for segment in target.Split('/', StringSplitOptions.None) do
            if
                String.IsNullOrEmpty segment
                || String.Equals(segment, ".", StringComparison.Ordinal)
            then
                ()
            elif String.Equals(segment, "..", StringComparison.Ordinal) then
                if stack.Count = 0 then
                    raise (
                        InvalidDataException(
                            String.Concat("Browser archive link target escapes the extraction root: ", linkPath)
                        )
                    )

                stack.RemoveAt(stack.Count - 1)
            elif segment.Contains(':') then
                raise (InvalidDataException(String.Concat("Browser archive link has an unsafe target: ", linkPath)))
            else
                stack.Add segment

        if stack.Count = 0 then
            raise (InvalidDataException(String.Concat("Browser archive link targets the extraction root: ", linkPath)))

        String.Join("/", stack)

    let private ensureNoReparseParents (extractionRoot: string) (targetDirectory: string) =
        let comparison =
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        let root = Path.GetFullPath extractionRoot
        let mutable current = DirectoryInfo(Path.GetFullPath targetDirectory)
        let mutable reachedRoot = String.Equals(current.FullName, root, comparison)

        while not reachedRoot do
            if current.LinkTarget |> Option.ofObj |> Option.isSome then
                raise (
                    InvalidDataException(
                        String.Concat(
                            "Browser archive extraction encountered a symbolic-link directory: ",
                            current.FullName
                        )
                    )
                )

            if current.Attributes.HasFlag FileAttributes.ReparsePoint then
                raise (
                    InvalidDataException(
                        String.Concat(
                            "Browser archive extraction encountered a reparse-point directory: ",
                            current.FullName
                        )
                    )
                )

            match current.Parent |> Option.ofObj with
            | Some parent ->
                current <- parent
                reachedRoot <- String.Equals(current.FullName, root, comparison)
            | None -> raise (InvalidDataException "Browser archive extraction left the temporary root.")

    let private ensureNotLinkOrReparse (path: string) =
        let info = FileInfo path :> FileSystemInfo

        if info.LinkTarget |> Option.ofObj |> Option.isSome then
            raise (InvalidDataException(String.Concat("Browser extraction encountered a symbolic link: ", path)))

        if info.Attributes.HasFlag FileAttributes.ReparsePoint then
            raise (InvalidDataException(String.Concat("Browser extraction encountered a reparse point: ", path)))

    let private setArchivedUnixMode (entry: ZipArchiveEntry) (destinationPath: string) =
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            let permissions = (entry.ExternalAttributes >>> 16) &&& 0x0FFF

            if permissions <> 0 then
                File.SetUnixFileMode(destinationPath, enum<UnixFileMode> permissions)

    let rec private resolveLinkTarget
        (declarations: Dictionary<string, bool>)
        (symlinkTargets: Dictionary<string, string>)
        (path: string)
        (visited: HashSet<string>)
        (depth: int)
        =
        let segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
        let mutable matchedPrefix = None
        let mutable index = 0

        while matchedPrefix.IsNone && index < segments.Length do
            index <- index + 1
            let prefix = String.Join("/", segments, 0, index)

            match symlinkTargets.TryGetValue prefix with
            | true, target -> matchedPrefix <- Some(prefix, target, index)
            | false, _ -> ()

        match matchedPrefix with
        | Some(prefix, target, consumed) ->
            if not (visited.Add prefix) then
                raise (
                    InvalidDataException(String.Concat("Browser archive contains a symbolic-link cycle at: ", prefix))
                )

            let remaining =
                if consumed = segments.Length then
                    String.Empty
                else
                    String.Join("/", segments, consumed, segments.Length - consumed)

            let nextPath =
                if String.IsNullOrEmpty remaining then
                    target
                else
                    String.Concat(target, "/", remaining)

            resolveLinkTarget declarations symlinkTargets nextPath visited (depth + 1)
        | None ->
            match declarations.TryGetValue path with
            | true, isDirectory -> path, isDirectory, depth
            | false, _ ->
                raise (
                    InvalidDataException(String.Concat("Browser archive link target is not archive-declared: ", path))
                )


    let extractArchiveAsync
        (platform: BrowserPlatformLock)
        (archivePath: string)
        (extractionRoot: string)
        (cancellationToken: CancellationToken)
        =
        task {
            Directory.CreateDirectory extractionRoot |> ignore
            let root = Path.GetFullPath extractionRoot

            let rootPrefix =
                String.Concat(root.TrimEnd(Path.DirectorySeparatorChar), Path.DirectorySeparatorChar)

            let comparison =
                if OperatingSystem.IsWindows() then
                    StringComparison.OrdinalIgnoreCase
                else
                    StringComparison.Ordinal

            let comparer =
                if OperatingSystem.IsWindows() then
                    StringComparer.OrdinalIgnoreCase
                else
                    StringComparer.Ordinal

            let targets = HashSet<string>(comparer)
            let declarations = Dictionary<string, bool>(StringComparer.Ordinal)
            let symlinkEntries = ResizeArray<ZipArchiveEntry * string * string>()

            use archiveStream =
                new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read)

            use archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, false)

            for entry in archive.Entries do
                cancellationToken.ThrowIfCancellationRequested()
                let entryName = entry.FullName
                let canonicalPath = canonicalArchivePath entryName
                let fileType = unixFileType entry

                let isDirectory =
                    entryName.EndsWith("/", StringComparison.Ordinal) || fileType = 0x4000

                if isWindowsReparsePoint entry then
                    raise (InvalidDataException(String.Concat("Browser archive entry is a reparse point: ", entryName)))

                if fileType <> 0 && fileType <> 0x4000 && fileType <> 0x8000 && fileType <> 0xA000 then
                    raise (
                        InvalidDataException(
                            String.Concat("Browser archive entry has an unsupported special file type: ", entryName)
                        )
                    )

                let relativePath = canonicalPath.Replace('/', Path.DirectorySeparatorChar)
                let destinationPath = Path.GetFullPath(Path.Combine(root, relativePath))

                if not (destinationPath.StartsWith(rootPrefix, comparison)) then
                    raise (
                        InvalidDataException(
                            String.Concat("Browser archive entry escapes the extraction root: ", entryName)
                        )
                    )

                if not (targets.Add destinationPath) || declarations.ContainsKey canonicalPath then
                    raise (
                        InvalidDataException(String.Concat("Browser archive contains a duplicate entry: ", entryName))
                    )

                declarations.Add(canonicalPath, isDirectory)

                if fileType = 0xA000 then
                    if not (String.Equals(platform.RuntimeIdentifier, "osx-arm64", StringComparison.Ordinal)) then
                        raise (
                            InvalidDataException(String.Concat("Browser archive entry is a symbolic link: ", entryName))
                        )

                    if entry.Length <= 0L || entry.Length > 4096L then
                        raise (
                            InvalidDataException(String.Concat("Browser archive link has an invalid size: ", entryName))
                        )

                    use reader = new StreamReader(entry.Open())
                    let target = reader.ReadToEnd()
                    let normalizedTarget = normalizeLinkTarget canonicalPath target
                    symlinkEntries.Add(entry, target, normalizedTarget)

            let symlinkTargets = Dictionary<string, string>(StringComparer.Ordinal)

            for entry, _, normalizedTarget in symlinkEntries do
                symlinkTargets.Add(canonicalArchivePath entry.FullName, normalizedTarget)

            let resolvedSymlinks = ResizeArray<string * string * string * string * bool * int>()

            for entry, target, normalizedTarget in symlinkEntries do
                let linkPath = canonicalArchivePath entry.FullName

                let resolvedTarget, isDirectory, depth =
                    resolveLinkTarget
                        declarations
                        symlinkTargets
                        normalizedTarget
                        (HashSet<string>(StringComparer.Ordinal))
                        0

                resolvedSymlinks.Add(linkPath, target, normalizedTarget, resolvedTarget, isDirectory, depth)

            for entry in archive.Entries do
                cancellationToken.ThrowIfCancellationRequested()

                if unixFileType entry <> 0xA000 then
                    let entryName = entry.FullName
                    let canonicalPath = canonicalArchivePath entryName

                    let destinationPath =
                        Path.GetFullPath(Path.Combine(root, canonicalPath.Replace('/', Path.DirectorySeparatorChar)))

                    let isDirectory =
                        entryName.EndsWith("/", StringComparison.Ordinal) || unixFileType entry = 0x4000

                    if isDirectory then
                        Directory.CreateDirectory destinationPath |> ignore
                        ensureNoReparseParents root destinationPath
                    else
                        match Path.GetDirectoryName destinationPath |> Option.ofObj with
                        | None ->
                            raise (
                                InvalidDataException(String.Concat("Browser archive entry has no parent: ", entryName))
                            )
                        | Some parent ->
                            Directory.CreateDirectory parent |> ignore
                            ensureNoReparseParents root parent
                            use source = entry.Open()

                            use destination =
                                new FileStream(
                                    destinationPath,
                                    FileMode.CreateNew,
                                    FileAccess.Write,
                                    FileShare.None,
                                    81920,
                                    FileOptions.Asynchronous
                                )

                            do! source.CopyToAsync(destination, cancellationToken)
                            destination.Flush(true)
                            ensureNotLinkOrReparse destinationPath
                            setArchivedUnixMode entry destinationPath

            for linkPath, target, normalizedTarget, _, isDirectory, _ in
                resolvedSymlinks |> Seq.sortBy (fun (_, _, _, _, _, depth) -> depth) do
                let destinationPath =
                    Path.GetFullPath(Path.Combine(root, linkPath.Replace('/', Path.DirectorySeparatorChar)))

                match Path.GetDirectoryName destinationPath |> Option.ofObj with
                | None -> raise (InvalidDataException(String.Concat("Browser archive link has no parent: ", linkPath)))
                | Some parent ->
                    ensureNoReparseParents root parent

                    if File.Exists destinationPath || Directory.Exists destinationPath then
                        raise (
                            InvalidDataException(String.Concat("Browser archive link path already exists: ", linkPath))
                        )

                    if isDirectory then
                        Directory.CreateSymbolicLink(destinationPath, target) |> ignore
                    else
                        File.CreateSymbolicLink(destinationPath, target) |> ignore

                    let linkInfo =
                        if isDirectory then
                            DirectoryInfo(destinationPath) :> FileSystemInfo
                        else
                            FileInfo(destinationPath) :> FileSystemInfo

                    if linkInfo.LinkTarget |> Option.ofObj |> Option.isNone then
                        raise (InvalidDataException(String.Concat("Browser archive link was not created: ", linkPath)))

                    match linkInfo.ResolveLinkTarget false |> Option.ofObj with
                    | None ->
                        raise (InvalidDataException(String.Concat("Browser archive link does not resolve: ", linkPath)))
                    | Some resolvedInfo ->
                        let expectedPath =
                            Path.GetFullPath(
                                Path.Combine(root, normalizedTarget.Replace('/', Path.DirectorySeparatorChar))
                            )

                        if not (String.Equals(resolvedInfo.FullName, expectedPath, comparison)) then
                            raise (
                                InvalidDataException(
                                    String.Concat(
                                        "Browser archive link resolved outside its declared target: ",
                                        linkPath
                                    )
                                )
                            )
        }

    let ensureUnixExecutable (executablePath: string) =
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            let mode = File.GetUnixFileMode executablePath
            File.SetUnixFileMode(executablePath, mode ||| UnixFileMode.UserExecute)

    let private fileSystemInfoIsLinkOrReparse (info: FileSystemInfo) =
        info.LinkTarget |> Option.ofObj |> Option.isSome
        || info.Exists && info.Attributes.HasFlag FileAttributes.ReparsePoint

    let private ensureNoLinkOrReparseAncestors (path: string) =
        let file = FileInfo(Path.GetFullPath path)

        if fileSystemInfoIsLinkOrReparse file then
            raise (InvalidDataException(String.Concat("Managed browser executable is a link or reparse point: ", path)))

        let mutable current = file.Directory |> Option.ofObj

        while current.IsSome do
            let directory = current.Value

            if fileSystemInfoIsLinkOrReparse directory then
                raise (
                    InvalidDataException(
                        String.Concat("Managed browser executable has a link or reparse ancestor: ", directory.FullName)
                    )
                )

            current <- directory.Parent |> Option.ofObj

    let private ensureNoLinkOrReparseDirectoryAncestors (path: string) =
        let mutable current = Some(DirectoryInfo(Path.GetFullPath path))

        while current.IsSome do
            let directory = current.Value

            if fileSystemInfoIsLinkOrReparse directory then
                raise (
                    InvalidDataException(
                        String.Concat("Managed browser target has a link or reparse ancestor: ", directory.FullName)
                    )
                )

            current <- directory.Parent |> Option.ofObj

    let private createPrivateDirectory (path: string) =
        Directory.CreateDirectory path |> ignore

        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    let verifyExpectedExecutableAsync
        (browserLock: BrowserLock)
        (platform: BrowserPlatformLock)
        (targetDirectory: string)
        (cancellationToken: CancellationToken)
        =
        task {
            let executable = BrowserManagementInternals.executablePath targetDirectory platform

            if not (File.Exists executable) then
                return
                    Error(
                        String.Concat(
                            "Browser archive did not contain the expected executable layout: ",
                            platform.ExecutableLayout
                        )
                    )
            elif File.GetAttributes(executable).HasFlag FileAttributes.ReparsePoint then
                return Error(String.Concat("Browser executable is a reparse point: ", executable))
            else
                try
                    ensureNoLinkOrReparseAncestors executable
                    ensureUnixExecutable executable

                    return!
                        BrowserManagementInternals.validateBrowserAsync
                            BrowserOrigin.ManagedCache
                            (Some browserLock.BrowserVersion)
                            executable
                            cancellationToken
                with error ->
                    return Error error.Message
        }

    let deleteDirectoryIfPresent (path: string) =
        if Directory.Exists path then
            Directory.Delete(path, true)

    let installUnderLockAsync
        (browserLock: BrowserLock)
        (platform: BrowserPlatformLock)
        (resolvedCacheRoot: string)
        (runtimeIdentifier: string)
        (workRoot: string)
        (downloadTimeout: TimeSpan)
        (cancellationToken: CancellationToken)
        =
        task {
            let targetDirectory =
                BrowserManagementInternals.targetDirectory resolvedCacheRoot browserLock runtimeIdentifier

            let targetInfo = DirectoryInfo targetDirectory

            if fileSystemInfoIsLinkOrReparse targetInfo then
                raise (
                    InvalidDataException(
                        String.Concat("Managed browser target is a link or reparse point: ", targetDirectory)
                    )
                )

            let! existingBrowserResult =
                task {
                    if Directory.Exists targetDirectory then
                        let! validation =
                            verifyExpectedExecutableAsync browserLock platform targetDirectory cancellationToken

                        match validation with
                        | Ok browser -> return Ok(Some browser)
                        | Error message -> return Error message
                    else
                        return Ok None
                }

            match existingBrowserResult with
            | Error message -> return Error message
            | Ok(Some browser) -> return Ok browser
            | Ok None ->
                createPrivateDirectory workRoot
                let archivePath = Path.Combine(workRoot, "browser.zip")
                let extractionRoot = Path.Combine(workRoot, "extracted")

                do! downloadAndVerifyAsync platform archivePath downloadTimeout cancellationToken
                do! extractArchiveAsync platform archivePath extractionRoot cancellationToken

                let! staged = verifyExpectedExecutableAsync browserLock platform extractionRoot cancellationToken

                match staged with
                | Error message -> return Error message
                | Ok _ ->
                    match Path.GetDirectoryName targetDirectory |> Option.ofObj with
                    | None -> return Error "Managed browser target directory has no parent."
                    | Some targetParent ->
                        Directory.CreateDirectory targetParent |> ignore
                        ensureNoLinkOrReparseDirectoryAncestors targetParent

                        let targetInfo = DirectoryInfo targetDirectory

                        if fileSystemInfoIsLinkOrReparse targetInfo then
                            return
                                Error(
                                    String.Concat(
                                        "Managed browser target is a link or reparse point: ",
                                        targetDirectory
                                    )
                                )
                        else
                            Directory.Move(extractionRoot, targetDirectory)

                            let! promoted =
                                verifyExpectedExecutableAsync browserLock platform targetDirectory cancellationToken

                            match promoted with
                            | Ok browser -> return Ok browser
                            | Error message ->
                                deleteDirectoryIfPresent targetDirectory
                                return Error message
        }

module BrowserInstall =
    let findBrowserLockSidecar baseDirectory =
        BrowserManagementInternals.locateBrowserLockSidecar baseDirectory

    let validateBrowserLockSidecar lockPath =
        BrowserManagementInternals.validateBrowserLockSidecar lockPath

    let installForRuntimeAsync
        (lockPath: string option)
        (cacheRoot: string option)
        (runtimeIdentifier: string)
        (downloadTimeout: TimeSpan)
        (cancellationToken: CancellationToken)
        =
        task {
            if
                String.Equals(runtimeIdentifier, "linux-arm64", StringComparison.Ordinal)
                || String.Equals(runtimeIdentifier, "win-arm64", StringComparison.Ordinal)
            then
                return Error(BrowserInstallInternals.unsupportedDiagnostic runtimeIdentifier)
            elif downloadTimeout <= TimeSpan.Zero then
                return Error "Browser download timeout must be positive."
            else
                match BrowserManagementInternals.loadTrustedBrowserLock lockPath with
                | Error message -> return Error message
                | Ok browserLock ->
                    match browserLock.Platforms.TryGetValue runtimeIdentifier with
                    | false, _ -> return Error(BrowserInstallInternals.unsupportedDiagnostic runtimeIdentifier)
                    | true, platform ->
                        let cacheRootResult =
                            match cacheRoot with
                            | Some value when not (String.IsNullOrWhiteSpace value) -> Ok(Path.GetFullPath value)
                            | Some _ -> Error "Managed browser cache path must not be empty."
                            | None -> BrowserManagementInternals.cacheRootForRuntime runtimeIdentifier

                        match cacheRootResult with
                        | Error message -> return Error message
                        | Ok resolvedCacheRoot ->
                            let workRoot =
                                Path.Combine(
                                    resolvedCacheRoot,
                                    String.Concat(".install-", runtimeIdentifier, "-", Guid.NewGuid().ToString("N"))
                                )

                            let work =
                                task {
                                    try
                                        Directory.CreateDirectory resolvedCacheRoot |> ignore

                                        let installLockPath =
                                            Path.Combine(
                                                resolvedCacheRoot,
                                                String.Concat(".install-", runtimeIdentifier, ".lock")
                                            )

                                        use! installLock =
                                            BrowserInstallInternals.acquireInstallLockAsync
                                                installLockPath
                                                cancellationToken

                                        return!
                                            BrowserInstallInternals.installUnderLockAsync
                                                browserLock
                                                platform
                                                resolvedCacheRoot
                                                runtimeIdentifier
                                                workRoot
                                                downloadTimeout
                                                cancellationToken
                                    with
                                    | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                                        return Error "Browser installation was cancelled."
                                    | error ->
                                        return Error(String.Concat("Browser installation failed: ", error.Message))
                                }

                            try
                                return! work
                            finally
                                try
                                    BrowserInstallInternals.deleteDirectoryIfPresent workRoot
                                with _ ->
                                    ()
        }

    let installAsync lockPath cancellationToken =
        installForRuntimeAsync
            lockPath
            None
            (BrowserManagementInternals.currentRuntimeIdentifier ())
            (TimeSpan.FromMinutes 5.0)
            cancellationToken
