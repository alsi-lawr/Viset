namespace Viset

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Viset.Serialization

[<DebuggerDisplay("BrowserOrigin")>]
type BrowserOrigin =
    | ExplicitPath
    | EnvironmentVariable
    | ManagedCache
    | SystemDiscovery of name: string

    override origin.ToString() =
        match origin with
        | ExplicitPath -> "explicit path"
        | EnvironmentVariable -> "VISET_BROWSER"
        | ManagedCache -> "managed cache"
        | SystemDiscovery name -> String.Concat("system ", name)

type BrowserExecutable =
    { ExecutablePath: string
      Origin: BrowserOrigin
      Version: string }

    override browser.ToString() = browser.ExecutablePath

type internal BrowserPlatformLock =
    { RuntimeIdentifier: string
      Url: Uri
      Sha256: string
      ExecutableLayout: string }

    override platform.ToString() = platform.RuntimeIdentifier

type internal BrowserLock =
    { LockPath: string
      BrowserVersion: string
      Revision: string
      Platforms: IReadOnlyDictionary<string, BrowserPlatformLock> }

    override browserLock.ToString() = browserLock.BrowserVersion

module internal BrowserManagementInternals =
    let browserLockFileName = "browser-lock.toml"
    let private expectedPublisher = "Google Chrome for Testing"
    let private versionTimeout = TimeSpan.FromSeconds 5.0
    let private diagnosticTimeout = TimeSpan.FromSeconds 1.0

    let private supportedPlatforms = [| "linux-x64"; "win-x64"; "osx-arm64" |]

    let private canonicalPlatform runtimeIdentifier url sha256 executableLayout =
        { RuntimeIdentifier = runtimeIdentifier
          Url = Uri url
          Sha256 = sha256
          ExecutableLayout = executableLayout }

    let canonicalBrowserLock =
        let platforms = Dictionary<string, BrowserPlatformLock>(StringComparer.Ordinal)

        platforms.Add(
            "linux-x64",
            canonicalPlatform
                "linux-x64"
                "https://storage.googleapis.com/chrome-for-testing-public/150.0.7871.124/linux64/chrome-linux64.zip"
                "ccb11556d5946fcf15f09d175c34d8e4b4293a8ef2eb7c4efc28cb60ac4d12fd"
                "chrome-linux64/chrome"
        )

        platforms.Add(
            "win-x64",
            canonicalPlatform
                "win-x64"
                "https://storage.googleapis.com/chrome-for-testing-public/150.0.7871.124/win64/chrome-win64.zip"
                "65fff74a602e0487fac43a148229b56d859070ac1b0e3d002d21d8edae3cfafe"
                "chrome-win64/chrome.exe"
        )

        platforms.Add(
            "osx-arm64",
            canonicalPlatform
                "osx-arm64"
                "https://storage.googleapis.com/chrome-for-testing-public/150.0.7871.124/mac-arm64/chrome-mac-arm64.zip"
                "36c8b5fe04c08a418a172206bb392600ec1550941bde6af2d4353df21db87a47"
                "chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing"
        )

        { LockPath = "compiled browser contract"
          BrowserVersion = "150.0.7871.124"
          Revision = "r1639810"
          Platforms = platforms }

    let private nonEmptyString (value: string | null) =
        value |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let currentRuntimeIdentifier () =
        let architecture = RuntimeInformation.ProcessArchitecture

        if OperatingSystem.IsLinux() then
            match architecture with
            | Architecture.X64 -> "linux-x64"
            | Architecture.Arm64 -> "linux-arm64"
            | _ -> String.Concat("linux-", architecture.ToString().ToLowerInvariant())
        elif OperatingSystem.IsWindows() then
            match architecture with
            | Architecture.X64 -> "win-x64"
            | Architecture.Arm64 -> "win-arm64"
            | _ -> String.Concat("win-", architecture.ToString().ToLowerInvariant())
        elif OperatingSystem.IsMacOS() then
            match architecture with
            | Architecture.Arm64 -> "osx-arm64"
            | Architecture.X64 -> "osx-x64"
            | _ -> String.Concat("osx-", architecture.ToString().ToLowerInvariant())
        else
            RuntimeInformation.RuntimeIdentifier

    let private requireText label (value: string | null) =
        match nonEmptyString value with
        | Some text -> text
        | None -> raise (InvalidDataException(String.Concat("browser-lock.toml requires ", label, ".")))

    let private validateSha256 runtimeIdentifier value =
        let digest =
            requireText (String.Concat("platforms.", runtimeIdentifier, ".sha256")) value

        if digest.Length <> 64 || not (digest |> Seq.forall Char.IsAsciiHexDigit) then
            raise (
                InvalidDataException(
                    String.Concat("browser-lock.toml has an invalid SHA-256 for ", runtimeIdentifier, ".")
                )
            )

        digest.ToLowerInvariant()

    let private validateExecutableLayout runtimeIdentifier value =
        let layout =
            requireText (String.Concat("platforms.", runtimeIdentifier, ".executable")) value

        let hasUnsafeSegment =
            layout.Split('/', StringSplitOptions.None)
            |> Array.exists (fun segment ->
                String.IsNullOrWhiteSpace segment
                || String.Equals(segment, ".", StringComparison.Ordinal)
                || String.Equals(segment, "..", StringComparison.Ordinal)
                || segment.Contains(':'))

        if
            layout.Contains('\\')
            || layout.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted layout
            || hasUnsafeSegment
        then
            raise (
                InvalidDataException(
                    String.Concat("browser-lock.toml has an unsafe executable layout for ", runtimeIdentifier, ".")
                )
            )

        layout

    let private validatePlatform runtimeIdentifier (model: BrowserLockPlatformTomlModel) =
        let urlText =
            requireText (String.Concat("platforms.", runtimeIdentifier, ".url")) model.Url

        let url =
            match Uri.TryCreate(urlText, UriKind.Absolute) with
            | true, parsed ->
                match Option.ofObj parsed with
                | Some value when
                    value.Scheme = Uri.UriSchemeHttps
                    && String.Equals(value.Host, "storage.googleapis.com", StringComparison.Ordinal)
                    && value.IsDefaultPort
                    ->
                    value
                | _ ->
                    raise (
                        InvalidDataException(
                            String.Concat("browser-lock.toml has an invalid download URL for ", runtimeIdentifier, ".")
                        )
                    )
            | false, _ ->
                raise (
                    InvalidDataException(
                        String.Concat("browser-lock.toml has an invalid download URL for ", runtimeIdentifier, ".")
                    )
                )

        { RuntimeIdentifier = runtimeIdentifier
          Url = url
          Sha256 = validateSha256 runtimeIdentifier model.Sha256
          ExecutableLayout = validateExecutableLayout runtimeIdentifier model.Executable }

    let private parseBrowserLock (lockPath: string) =
        try
            if String.IsNullOrWhiteSpace lockPath then
                Error "browser-lock.toml path must not be empty."
            elif not (File.Exists lockPath) then
                Error(String.Concat("browser-lock.toml was not found: ", lockPath))
            else
                let fullPath = Path.GetFullPath lockPath
                let model = File.ReadAllText fullPath |> BrowserLockTomlModels.Deserialize

                if model.Version <> Nullable 1L then
                    raise (InvalidDataException "browser-lock.toml version must be 1.")

                let publisher = requireText "publisher" model.Publisher

                if not (String.Equals(publisher, expectedPublisher, StringComparison.Ordinal)) then
                    raise (
                        InvalidDataException(
                            String.Concat("browser-lock.toml publisher must be '", expectedPublisher, "'.")
                        )
                    )

                let browserVersion = requireText "browser_version" model.BrowserVersion
                let revision = requireText "revision" model.Revision

                if model.Platforms.Count <> supportedPlatforms.Length then
                    raise (
                        InvalidDataException("browser-lock.toml must define exactly linux-x64, win-x64, and osx-arm64.")
                    )

                let platforms = Dictionary<string, BrowserPlatformLock>(StringComparer.Ordinal)

                for runtimeIdentifier in supportedPlatforms do
                    match model.Platforms.TryGetValue runtimeIdentifier with
                    | true, platform -> platforms.Add(runtimeIdentifier, validatePlatform runtimeIdentifier platform)
                    | false, _ ->
                        raise (
                            InvalidDataException(
                                String.Concat("browser-lock.toml is missing platforms.", runtimeIdentifier, ".")
                            )
                        )

                for runtimeIdentifier in model.Platforms.Keys do
                    if not (platforms.ContainsKey runtimeIdentifier) then
                        raise (
                            InvalidDataException(
                                String.Concat(
                                    "browser-lock.toml contains unsupported platform '",
                                    runtimeIdentifier,
                                    "'."
                                )
                            )
                        )

                Ok
                    { LockPath = fullPath
                      BrowserVersion = browserVersion
                      Revision = revision
                      Platforms = platforms }
        with error ->
            Error(String.Concat("Failed to read browser-lock.toml: ", error.Message))

    let private browserLocksEqual (candidate: BrowserLock) =
        let canonical = canonicalBrowserLock

        String.Equals(candidate.BrowserVersion, canonical.BrowserVersion, StringComparison.Ordinal)
        && String.Equals(candidate.Revision, canonical.Revision, StringComparison.Ordinal)
        && candidate.Platforms.Count = canonical.Platforms.Count
        && supportedPlatforms
           |> Array.forall (fun runtimeIdentifier ->
               match
                   candidate.Platforms.TryGetValue runtimeIdentifier, canonical.Platforms.TryGetValue runtimeIdentifier
               with
               | (true, candidatePlatform), (true, canonicalPlatform) ->
                   candidatePlatform.Url = canonicalPlatform.Url
                   && String.Equals(candidatePlatform.Sha256, canonicalPlatform.Sha256, StringComparison.Ordinal)
                   && String.Equals(
                       candidatePlatform.ExecutableLayout,
                       canonicalPlatform.ExecutableLayout,
                       StringComparison.Ordinal
                   )
               | _ -> false)

    let loadTrustedBrowserLock (lockPath: string option) =
        match lockPath with
        | None -> Ok canonicalBrowserLock
        | Some path ->
            match parseBrowserLock path with
            | Ok candidate when browserLocksEqual candidate -> Ok candidate
            | Ok _ -> Error "browser-lock.toml does not match the compiled trusted browser contract."
            | Error message -> Error message

    let validateBrowserLockSidecar lockPath =
        loadTrustedBrowserLock (Some lockPath) |> Result.map ignore

    let cacheRootForRuntime (runtimeIdentifier: string) =
        let environmentRoot variableName segments =
            match Environment.GetEnvironmentVariable variableName |> nonEmptyString with
            | Some root -> Ok(Path.Combine(Array.append [| root |] segments))
            | None -> Error(String.Concat(variableName, " is not set; the managed browser cache cannot be located."))

        if runtimeIdentifier.StartsWith("linux-", StringComparison.Ordinal) then
            match Environment.GetEnvironmentVariable "XDG_CACHE_HOME" |> nonEmptyString with
            | Some root -> Ok(Path.Combine(root, "viset", "browser"))
            | None -> environmentRoot "HOME" [| ".cache"; "viset"; "browser" |]
        elif runtimeIdentifier.StartsWith("osx-", StringComparison.Ordinal) then
            environmentRoot "HOME" [| "Library"; "Caches"; "Viset"; "browser" |]
        elif runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal) then
            environmentRoot "LOCALAPPDATA" [| "Viset"; "browser" |]
        else
            Error(String.Concat("No managed browser cache is defined for ", runtimeIdentifier, "."))

    let targetDirectory (cacheRoot: string) (browserLock: BrowserLock) (runtimeIdentifier: string) =
        Path.Combine(cacheRoot, String.Concat(browserLock.BrowserVersion, "-", browserLock.Revision), runtimeIdentifier)

    let executablePath (targetDirectoryPath: string) (platform: BrowserPlatformLock) =
        platform.ExecutableLayout.Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> Array.fold (fun current segment -> Path.Combine(current, segment)) targetDirectoryPath

    let private tryParseVersionToken (text: string) =
        text.Split([| ' '; '\t'; '\r'; '\n'; '('; ')'; ','; ';' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun token ->
            let candidate = token.Trim()
            let segments = candidate.Split('.', StringSplitOptions.None)

            if
                segments.Length = 4
                && segments
                   |> Array.forall (fun segment -> segment.Length > 0 && segment |> Seq.forall Char.IsAsciiDigit)
            then
                Some candidate
            else
                None)

    let private readDiagnosticsAsync (standardOutput: Task<string>) (standardError: Task<string>) =
        task {
            try
                let! output = Task.WhenAll([| standardOutput; standardError |]).WaitAsync diagnosticTimeout
                return Ok(String.Concat(output[0], Environment.NewLine, output[1]).Trim())
            with
            | :? TimeoutException -> return Error "Browser version diagnostic streams did not close within 1000 ms."
            | error -> return Error(String.Concat("Failed to read browser version diagnostics: ", error.Message))
        }

    let readBrowserVersionAsync (executablePath: string) (cancellationToken: CancellationToken) =
        task {
            if String.IsNullOrWhiteSpace executablePath then
                return Error "Browser executable path must not be empty."
            elif not (File.Exists executablePath) then
                return Error(String.Concat("Browser executable does not exist: ", executablePath))
            else
                try
                    let startInfo = ProcessStartInfo(Path.GetFullPath executablePath)
                    startInfo.UseShellExecute <- false
                    startInfo.CreateNoWindow <- true
                    startInfo.RedirectStandardOutput <- true
                    startInfo.RedirectStandardError <- true
                    startInfo.ArgumentList.Add "--version"

                    use browserProcess =
                        Process.Start startInfo
                        |> Option.ofObj
                        |> Option.defaultWith (fun () ->
                            raise (InvalidOperationException "The browser version process could not be started."))

                    let standardOutput = browserProcess.StandardOutput.ReadToEndAsync()
                    let standardError = browserProcess.StandardError.ReadToEndAsync()

                    use timeoutCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                    timeoutCancellation.CancelAfter versionTimeout

                    let! exitResult =
                        task {
                            try
                                do! browserProcess.WaitForExitAsync timeoutCancellation.Token
                                return Ok()
                            with
                            | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                                return Error "Browser version validation was cancelled."
                            | :? OperationCanceledException ->
                                try
                                    browserProcess.Kill true
                                with _ ->
                                    ()

                                return
                                    Error(
                                        String.Concat(
                                            "Browser executable did not answer --version within ",
                                            versionTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                                            " ms: ",
                                            executablePath
                                        )
                                    )
                        }

                    match exitResult with
                    | Error message -> return Error message
                    | Ok() ->
                        let! diagnosticsResult = readDiagnosticsAsync standardOutput standardError

                        match diagnosticsResult with
                        | Error message -> return Error message
                        | Ok diagnostics when browserProcess.ExitCode <> 0 ->
                            return
                                Error(
                                    String.Concat(
                                        "Browser executable --version failed with exit code ",
                                        browserProcess.ExitCode.ToString(CultureInfo.InvariantCulture),
                                        ": ",
                                        diagnostics
                                    )
                                )
                        | Ok diagnostics ->
                            match tryParseVersionToken diagnostics with
                            | Some version -> return Ok version
                            | None ->
                                return
                                    Error(
                                        String.Concat(
                                            "Browser executable returned no four-part version from --version: ",
                                            executablePath
                                        )
                                    )
                with error ->
                    return
                        Error(
                            String.Concat(
                                "Browser executable could not be validated: ",
                                executablePath,
                                ": ",
                                error.Message
                            )
                        )
        }

    let validateBrowserAsync
        (origin: BrowserOrigin)
        (expectedVersion: string option)
        (executablePath: string)
        (cancellationToken: CancellationToken)
        =
        task {
            let fullPath = Path.GetFullPath executablePath
            let! versionResult = readBrowserVersionAsync fullPath cancellationToken

            match versionResult with
            | Error message -> return Error message
            | Ok version ->
                match expectedVersion with
                | Some expected when not (String.Equals(version, expected, StringComparison.Ordinal)) ->
                    return
                        Error(
                            String.Concat("Browser executable reported version ", version, "; expected ", expected, ".")
                        )
                | _ ->
                    return
                        Ok
                            { ExecutablePath = fullPath
                              Origin = origin
                              Version = version }
        }

    let validateManagedBrowserAsync expectedVersion executablePath cancellationToken =
        task {
            try
                let file = FileInfo(Path.GetFullPath executablePath)

                if file.LinkTarget |> Option.ofObj |> Option.isSome then
                    return Error(String.Concat("Managed browser executable is a symbolic link: ", executablePath))
                elif file.Exists && file.Attributes.HasFlag FileAttributes.ReparsePoint then
                    return Error(String.Concat("Managed browser executable is a reparse point: ", executablePath))
                else
                    let mutable current = file.Directory |> Option.ofObj
                    let mutable unsafeAncestor = None

                    while current.IsSome && unsafeAncestor.IsNone do
                        let directory = current.Value

                        if
                            directory.LinkTarget |> Option.ofObj |> Option.isSome
                            || directory.Exists && directory.Attributes.HasFlag FileAttributes.ReparsePoint
                        then
                            unsafeAncestor <- Some directory.FullName

                        current <- directory.Parent |> Option.ofObj

                    match unsafeAncestor with
                    | Some path ->
                        return Error(String.Concat("Managed browser executable has a link or reparse ancestor: ", path))
                    | None ->
                        return!
                            validateBrowserAsync
                                BrowserOrigin.ManagedCache
                                (Some expectedVersion)
                                executablePath
                                cancellationToken
            with error ->
                return Error(String.Concat("Managed browser cache validation failed: ", error.Message))
        }

    let private pathCandidates (executableName: string) =
        match Environment.GetEnvironmentVariable "PATH" |> nonEmptyString with
        | None -> Seq.empty
        | Some pathValue ->
            pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            |> Seq.map (fun directory -> Path.Combine(directory, executableName))

    let systemCandidates (runtimeIdentifier: string) =
        let candidates = ResizeArray<string * string>()
        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        let add name path =
            if not (String.IsNullOrWhiteSpace path) then
                let fullPath = Path.GetFullPath path

                if seen.Add fullPath then
                    candidates.Add(name, fullPath)

        let addFromPath name executableName =
            pathCandidates executableName |> Seq.iter (add name)

        let addWindowsRoot variableName name segments =
            match Environment.GetEnvironmentVariable variableName |> nonEmptyString with
            | Some root -> add name (Path.Combine(Array.append [| root |] segments))
            | None -> ()

        if runtimeIdentifier.StartsWith("linux-", StringComparison.Ordinal) then
            addFromPath "Google Chrome" "google-chrome"
            addFromPath "Google Chrome Stable" "google-chrome-stable"
            addFromPath "Chromium" "chromium"
            addFromPath "Chromium Browser" "chromium-browser"
            addFromPath "Microsoft Edge" "microsoft-edge"
            addFromPath "Microsoft Edge Stable" "microsoft-edge-stable"
        elif runtimeIdentifier.StartsWith("osx-", StringComparison.Ordinal) then
            add "Google Chrome" "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"

            add
                "Google Chrome for Testing"
                "/Applications/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing"

            add "Chromium" "/Applications/Chromium.app/Contents/MacOS/Chromium"
            add "Microsoft Edge" "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"
            addFromPath "Google Chrome" "google-chrome"
            addFromPath "Chromium" "chromium"
        elif runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal) then
            addWindowsRoot "PROGRAMFILES" "Google Chrome" [| "Google"; "Chrome"; "Application"; "chrome.exe" |]

            addWindowsRoot "PROGRAMFILES(X86)" "Google Chrome" [| "Google"; "Chrome"; "Application"; "chrome.exe" |]

            addWindowsRoot "LOCALAPPDATA" "Google Chrome" [| "Google"; "Chrome"; "Application"; "chrome.exe" |]

            addWindowsRoot "PROGRAMFILES" "Chromium" [| "Chromium"; "Application"; "chrome.exe" |]

            addWindowsRoot "PROGRAMFILES(X86)" "Microsoft Edge" [| "Microsoft"; "Edge"; "Application"; "msedge.exe" |]

            addWindowsRoot "PROGRAMFILES" "Microsoft Edge" [| "Microsoft"; "Edge"; "Application"; "msedge.exe" |]

            addFromPath "Google Chrome" "chrome.exe"
            addFromPath "Chromium" "chromium.exe"
            addFromPath "Microsoft Edge" "msedge.exe"

        candidates |> Seq.filter (fun (_, path) -> File.Exists path) |> Seq.toList

    let locateBrowserLockSidecar (baseDirectory: string) =
        let basePath = Path.Combine(baseDirectory, browserLockFileName)

        if File.Exists basePath then
            Some(Path.GetFullPath basePath)
        else
            None

module BrowserResolution =
    let private tryManagedAsync
        (lockPath: string option)
        (cacheRoot: string option)
        (runtimeIdentifier: string)
        (cancellationToken: CancellationToken)
        =
        task {
            match BrowserManagementInternals.loadTrustedBrowserLock lockPath with
            | Error message -> return Error message
            | Ok browserLock ->
                match browserLock.Platforms.TryGetValue runtimeIdentifier with
                | false, _ -> return Ok None
                | true, platform ->
                    let rootResult =
                        match cacheRoot with
                        | Some root when not (String.IsNullOrWhiteSpace root) -> Ok(Path.GetFullPath root)
                        | Some _ -> Error "Managed browser cache path must not be empty."
                        | None -> BrowserManagementInternals.cacheRootForRuntime runtimeIdentifier

                    match rootResult with
                    | Error _ -> return Ok None
                    | Ok root ->
                        let target =
                            BrowserManagementInternals.targetDirectory root browserLock runtimeIdentifier

                        let executable = BrowserManagementInternals.executablePath target platform

                        if not (File.Exists executable) then
                            return Ok None
                        else
                            let! validation =
                                BrowserManagementInternals.validateManagedBrowserAsync
                                    browserLock.BrowserVersion
                                    executable
                                    cancellationToken

                            match validation with
                            | Ok browser -> return Ok(Some browser)
                            | Error _ -> return Ok None
        }

    let rec private trySystemAsync candidates cancellationToken =
        task {
            match candidates with
            | [] -> return None
            | (name, path) :: remaining ->
                let! result =
                    BrowserManagementInternals.validateBrowserAsync
                        (BrowserOrigin.SystemDiscovery name)
                        None
                        path
                        cancellationToken

                match result with
                | Ok browser -> return Some browser
                | Error _ -> return! trySystemAsync remaining cancellationToken
        }

    let resolveForRuntimeAsync
        (explicitPath: string option)
        (lockPath: string option)
        (cacheRoot: string option)
        (runtimeIdentifier: string)
        (cancellationToken: CancellationToken)
        =
        task {
            match explicitPath with
            | Some path ->
                return!
                    BrowserManagementInternals.validateBrowserAsync
                        BrowserOrigin.ExplicitPath
                        None
                        path
                        cancellationToken
            | None ->
                match Environment.GetEnvironmentVariable "VISET_BROWSER" |> Option.ofObj with
                | Some path when not (String.IsNullOrWhiteSpace path) ->
                    return!
                        BrowserManagementInternals.validateBrowserAsync
                            BrowserOrigin.EnvironmentVariable
                            None
                            path
                            cancellationToken
                | _ ->
                    let! managedResult = tryManagedAsync lockPath cacheRoot runtimeIdentifier cancellationToken

                    match managedResult with
                    | Error message -> return Error message
                    | Ok(Some browser) -> return Ok browser
                    | Ok None ->
                        let! systemBrowser =
                            trySystemAsync
                                (BrowserManagementInternals.systemCandidates runtimeIdentifier)
                                cancellationToken

                        match systemBrowser with
                        | Some browser -> return Ok browser
                        | None ->
                            return
                                Error
                                    "No usable browser was found. Set --browser, VISET_BROWSER, run 'viset browser install', or install Chrome, Chromium, or Edge."
        }

    let resolveAsync explicitPath lockPath cancellationToken =
        resolveForRuntimeAsync
            explicitPath
            lockPath
            None
            (BrowserManagementInternals.currentRuntimeIdentifier ())
            cancellationToken
