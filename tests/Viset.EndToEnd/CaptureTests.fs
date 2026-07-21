namespace Viset.EndToEnd

open System
open System.IO
open System.Security.Cryptography
open System.Text.RegularExpressions
open FsUnit
open NUnit.Framework
open Viset.EndToEnd.Fixtures
open Viset.EndToEnd.MediaAssertions

[<assembly: LevelOfParallelism(1)>]
do ()

module CaptureTests =
    let private requireSuccess result = result.ExitCode |> should equal 0

    let private requireFailure expected result =
        result.ExitCode |> should not' (equal 0)
        output result |> should contain expected

    let private requirePortClosed port = isPortOpen port |> should equal false

    let private capture directory fixtureName outputDirectory arguments environment =
        let port, result =
            runCapture directory (fixturePath fixtureName) outputDirectory arguments environment

        requirePortClosed port
        result

    let private webPTemplate configuration =
        $"""--[[
# viset
version = 1
output = "capture.webp"

[webp]
{configuration}

[devices.desktop]

[devices.desktop.viewport]
width = 320
height = 240
]]

local recording = viset.record()
recording:start()
recording:during("100ms")
recording:stop()
"""

    [<Test; NonParallelizable>]
    let ``the command line should reject invalid capture configurations`` () =
        use directory = createTemporaryDirectory "invalid-configurations"
        assertBinaryExists ()

        let unknownProperty =
            writeScript
                directory
                "unknown-property.lua"
                """--[[
# viset
version = 1
output = "capture.png"
mystery_option = true

[devices.desktop]

[devices.desktop.viewport]
width = 320
height = 240
]]

viset.snapshot()
"""

        run binaryPath [ "capture"; unknownProperty ] directory.Path [] (TimeSpan.FromSeconds 30.0)
        |> requireFailure "Unknown TOML property 'capture.mystery_option'."

        let invalidMethod =
            writeScript directory "invalid-webp-method.lua" (webPTemplate "method = 7")

        run binaryPath [ "capture"; invalidMethod ] directory.Path [] (TimeSpan.FromSeconds 30.0)
        |> requireFailure "webp.method must be between 0 and 6."

        [ "invalid-webp-source",
          "source = \"browser_webp\"",
          "Unknown webp.source 'browser_webp'; expected png_screencast or jpeg_screencast."
          "invalid-webp-source-quality",
          "source_quality = 95",
          "webp.source_quality is valid only when webp.source = 'jpeg_screencast'."
          "invalid-webp-encoder",
          "encoder = \"libwebp\"",
          "Unknown webp.encoder 'libwebp'; expected libwebp_full, libwebp_anim, or ffmpeg."
          "invalid-webp-pipeline",
          "pipeline = \"streaming\"",
          "Unknown webp.pipeline 'streaming'; expected spooled or live."
          "invalid-webp-mode",
          "mode = \"near_lossless\"",
          "Unknown webp.mode 'near_lossless'; expected lossy or lossless."
          "invalid-webp-quality", "quality = 101", "webp.quality must be a finite number between 0 and 100."
          "removed-webp-lossless", "lossless = true", "Unknown TOML property 'webp.lossless'." ]
        |> List.iter (fun (name, configuration, expected) ->
            let script = writeScript directory $"{name}.lua" (webPTemplate configuration)

            run binaryPath [ "capture"; script ] directory.Path [] (TimeSpan.FromSeconds 30.0)
            |> requireFailure expected)

        let redundantDeviceAxis =
            writeScript
                directory
                "redundant-device-axis.lua"
                """--[[
# viset
version = 1
output = "{device}.png"

[devices.desktop]

[devices.desktop.viewport]
width = 320
height = 240

[matrix]
device = ["desktop"]
]]

viset.snapshot()
"""

        run binaryPath [ "capture"; redundantDeviceAxis ] directory.Path [] (TimeSpan.FromSeconds 30.0)
        |> requireFailure "matrix.device is redundant"

    [<Test; NonParallelizable>]
    let ``the command line should validate ffmpeg before browser work`` () =
        use directory = createTemporaryDirectory "missing-ffmpeg"
        let pathWithoutFfmpeg = Path.Combine(directory.Path, "empty-path")
        Directory.CreateDirectory pathWithoutFfmpeg |> ignore

        runCapture
            directory
            (fixturePath "animation-ffmpeg.lua")
            (Path.Combine(directory.Path, "output"))
            []
            [ "PATH", pathWithoutFfmpeg ]
        |> snd
        |> requireFailure "webp.encoder = 'ffmpeg' requires ffmpeg"

    [<Test; NonParallelizable>]
    let ``the capture fixtures should produce deterministic media and protect existing output`` () =
        use directory = createTemporaryDirectory "media"
        let outputDirectory = Path.Combine(directory.Path, "output")

        capture directory "stills.lua" outputDirectory [] [] |> requireSuccess
        capture directory "animation.lua" outputDirectory [] [] |> requireSuccess

        assertInventory outputDirectory [ "animations/motion.webp"; "screenshots/blue.png"; "screenshots/red.png" ]

        assertPng (Path.Combine(outputDirectory, "screenshots", "red.png")) 400 300
        assertPng (Path.Combine(outputDirectory, "screenshots", "blue.png")) 400 300
        assertWebP (Path.Combine(outputDirectory, "animations", "motion.webp")) 400 300 (Some 400) 2 None

        let red =
            SHA256.HashData(File.ReadAllBytes(Path.Combine(outputDirectory, "screenshots", "red.png")))

        let blue =
            SHA256.HashData(File.ReadAllBytes(Path.Combine(outputDirectory, "screenshots", "blue.png")))

        red |> should not' (equal blue)
        Directory.Exists(Path.Combine(outputDirectory, ".viset")) |> should equal false

        File.Exists(Path.Combine(outputDirectory, "manifest.toml"))
        |> should equal false

        capture directory "stills.lua" outputDirectory [] []
        |> requireFailure "Refusing to overwrite existing output without --force"

        capture directory "stills.lua" outputDirectory [ "--force" ] []
        |> requireSuccess

    [<Test; NonParallelizable>]
    let ``the recording encoders should preserve duration and dimensions`` () =
        use directory = createTemporaryDirectory "encoders"

        [ "animation-libwebp-anim.lua", "libwebp-anim-output", "animations/motion-libwebp-anim.webp"
          "animation-ffmpeg.lua", "ffmpeg-output", "animations/motion.webp" ]
        |> List.iter (fun (fixture, outputName, mediaPath) ->
            let outputDirectory = Path.Combine(directory.Path, outputName)
            capture directory fixture outputDirectory [] [] |> requireSuccess
            assertInventory outputDirectory [ mediaPath ]
            assertWebP (Path.Combine(outputDirectory, mediaPath)) 400 300 (Some 400) 2 None)

    [<Test; NonParallelizable>]
    let ``the recording pipeline should report coalescing and live spill`` () =
        use directory = createTemporaryDirectory "recording-pipeline"
        let coalescingOutput = Path.Combine(directory.Path, "coalescing-output")
        let coalescing = capture directory "coalescing.lua" coalescingOutput [] []
        requireSuccess coalescing
        output coalescing |> should contain "frames=12 encoded=2"
        assertInventory coalescingOutput [ "animations/coalesced.webp" ]
        assertWebP (Path.Combine(coalescingOutput, "animations", "coalesced.webp")) 320 240 (Some 400) 1 (Some 2)

        let liveOutput = Path.Combine(directory.Path, "live-output")
        let live = capture directory "animation-live-spill.lua" liveOutput [] []
        requireSuccess live
        output live |> should contain "pipeline=live"
        Regex.IsMatch(output live, "spilled=[1-9][0-9]*") |> should equal true
        assertInventory liveOutput [ "animations/live-spill.webp" ]
        assertWebP (Path.Combine(liveOutput, "animations", "live-spill.webp")) 1600 900 (Some 400) 1 None

    [<Test; NonParallelizable>]
    let ``the capture process should clean up child services after failure`` () =
        use directory = createTemporaryDirectory "cleanup"

        [ "fixture failure", [ "VISET_FIXTURE_FAIL", "1" ]
          "active recording failure", [ "VISET_FIXTURE_FAIL_ACTIVE", "1" ] ]
        |> List.iter (fun (name, environment) ->
            let outputDirectory = Path.Combine(directory.Path, name.Replace(' ', '-'))

            let port, result =
                runCapture directory (fixturePath "animation.lua") outputDirectory [] environment

            requireFailure "forced" result
            requirePortClosed port

            File.Exists(Path.Combine(outputDirectory, "animations", "motion.webp"))
            |> should equal false)

    [<Test; NonParallelizable>]
    let ``the initialized scaffold should contain exact editor files and standalone Neovim guidance`` () =
        use directory = createTemporaryDirectory "scaffold"
        let project = Path.Combine(directory.Path, "project")

        run binaryPath [ "init"; project ] directory.Path [] (TimeSpan.FromSeconds 30.0)
        |> requireSuccess

        assertInventory
            project
            [ ".gitignore"
              ".luarc.json"
              ".viset/viset.d.lua"
              "README.md"
              "capture.lua" ]

        Directory.Exists(Path.Combine(project, ".viset", "nvim")) |> should equal false

        File.ReadAllText(Path.Combine(project, ".gitignore"))
        |> should contain "/output/"

        File.ReadAllText(Path.Combine(project, ".luarc.json"))
        |> should contain "\"runtime.version\": \"Lua 5.2\""

        File.ReadAllText(Path.Combine(project, ".viset", "viset.d.lua"))
        |> should contain "---@class VisetApi"

        let readme = File.ReadAllText(Path.Combine(project, "README.md"))
        readme |> should contain "https://github.com/getviset/viset.nvim"
        readme |> should contain "Lua, TOML, and JavaScript Tree-sitter parsers"
        readme |> should contain ":checkhealth viset"
        readme |> should not' (contain ".viset/nvim")
        readme |> should not' (contain "runtimepath")
        readme |> should not' (contain "Tree-sitter query")
        readme |> should not' (contain "VS Code")

    [<Test; NonParallelizable>]
    let ``the forced scaffold should safely remove only legacy Neovim support`` () =
        use directory = createTemporaryDirectory "legacy-neovim-scaffold"

        let initialize project force =
            let arguments =
                if force then
                    [ "init"; project; "--force" ]
                else
                    [ "init"; project ]

            run binaryPath arguments directory.Path [] (TimeSpan.FromSeconds 30.0)

        let writeLegacyQuery nvimDirectory =
            let luaDirectory = Path.Combine(nvimDirectory, "queries", "lua")
            Directory.CreateDirectory luaDirectory |> ignore
            let queryPath = Path.Combine(luaDirectory, "injections.scm")
            File.WriteAllText(queryPath, "; legacy generated query\n")
            queryPath

        let migratedProject = Path.Combine(directory.Path, "migrated-project")
        initialize migratedProject false |> requireSuccess
        let migratedNvim = Path.Combine(migratedProject, ".viset", "nvim")
        let migratedQuery = writeLegacyQuery migratedNvim
        let projectNote = Path.Combine(migratedProject, ".viset", "project-note.txt")
        File.WriteAllText(projectNote, "keep project note")

        initialize migratedProject false |> requireFailure "use --force"
        File.Exists migratedQuery |> should equal true
        initialize migratedProject true |> requireSuccess
        File.Exists migratedQuery |> should equal false

        Directory.Exists(Path.Combine(migratedNvim, "queries", "lua"))
        |> should equal false

        Directory.Exists(Path.Combine(migratedNvim, "queries")) |> should equal false
        Directory.Exists migratedNvim |> should equal false
        File.ReadAllText projectNote |> should equal "keep project note"

        assertInventory
            migratedProject
            [ ".gitignore"
              ".luarc.json"
              ".viset/project-note.txt"
              ".viset/viset.d.lua"
              "README.md"
              "capture.lua" ]

        let preservedProject = Path.Combine(directory.Path, "preserved-project")
        initialize preservedProject false |> requireSuccess
        let preservedNvim = Path.Combine(preservedProject, ".viset", "nvim")
        let preservedQuery = writeLegacyQuery preservedNvim
        let userFile = Path.Combine(preservedNvim, "user-notes.txt")
        File.WriteAllText(userFile, "keep Neovim note")

        initialize preservedProject true |> requireSuccess
        File.Exists preservedQuery |> should equal false

        Directory.Exists(Path.Combine(preservedNvim, "queries", "lua"))
        |> should equal false

        Directory.Exists(Path.Combine(preservedNvim, "queries")) |> should equal false
        Directory.Exists preservedNvim |> should equal true
        File.ReadAllText userFile |> should equal "keep Neovim note"

        assertInventory
            preservedProject
            [ ".gitignore"
              ".luarc.json"
              ".viset/nvim/user-notes.txt"
              ".viset/viset.d.lua"
              "README.md"
              "capture.lua" ]

        let combine root segments =
            segments |> List.fold (fun path segment -> Path.Combine(path, segment)) root

        [ "nvim", [ ".viset" ], "nvim", [ "queries"; "lua" ]
          "queries", [ ".viset"; "nvim" ], "queries", [ "lua" ]
          "lua", [ ".viset"; "nvim"; "queries" ], "lua", [] ]
        |> List.iter (fun (label, parentSegments, linkName, outsideSegments) ->
            let linkedProject =
                Path.Combine(directory.Path, String.Concat("linked-", label, "-project"))

            initialize linkedProject false |> requireSuccess

            let outsideDirectory =
                Path.Combine(directory.Path, String.Concat("outside-", label))

            let outsideQueryDirectory = combine outsideDirectory outsideSegments
            Directory.CreateDirectory outsideQueryDirectory |> ignore
            let outsideQuery = Path.Combine(outsideQueryDirectory, "injections.scm")
            File.WriteAllText(outsideQuery, "; legacy generated query\n")
            let linkParent = combine linkedProject parentSegments
            Directory.CreateDirectory linkParent |> ignore

            Directory.CreateSymbolicLink(Path.Combine(linkParent, linkName), outsideDirectory)
            |> ignore

            initialize linkedProject true |> requireFailure "must not be a link"
            File.ReadAllText outsideQuery |> should equal "; legacy generated query\n"

            File.Exists(Path.Combine(linkedProject, ".viset", "viset.d.lua"))
            |> should equal true)

    [<Test; NonParallelizable>]
    let ``the scaffold should capture its example and apply interactive settings`` () =
        use directory = createTemporaryDirectory "scaffold-capture"
        let project = Path.Combine(directory.Path, "project")

        run binaryPath [ "init"; project ] directory.Path [] (TimeSpan.FromSeconds 30.0)
        |> requireSuccess

        run
            binaryPath
            [ "capture"; Path.Combine(project, "capture.lua") ]
            project
            [ "VISET_BROWSER", browserExecutable directory ]
            (TimeSpan.FromMinutes 2.0)
        |> requireSuccess

        File.Exists(Path.Combine(project, "output", "example.png")) |> should equal true

        let readme = File.ReadAllText(Path.Combine(project, "README.md"))
        readme |> should contain "viset capture capture.lua"
        readme |> should contain "output/example.png"

        let interactive = Path.Combine(directory.Path, "interactive-project")

        runWithInput
            binaryPath
            [ "init"; interactive; "--interactive" ]
            directory.Path
            []
            (TimeSpan.FromSeconds 30.0)
            "data:text/html,%3Ch1%3EInteractive%3C%2Fh1%3E\ncapture.png\n1024\n640\n"
        |> requireSuccess

        let interactiveCapture = File.ReadAllText(Path.Combine(interactive, "capture.lua"))
        interactiveCapture |> should contain "output = \"capture.png\""
        interactiveCapture |> should contain "width = 1024"
        interactiveCapture |> should contain "height = 640"

        File.ReadAllText(Path.Combine(interactive, ".gitignore"))
        |> should contain "/capture.png"

    [<Test; NonParallelizable>]
    let ``the shipped examples should produce their expected media`` () =
        use directory = createTemporaryDirectory "examples"
        let minimalOutput = Path.Combine(directory.Path, "minimal-output")
        let minimalPort = freePort ()

        run
            binaryPath
            [ "capture"
              Path.Combine(repositoryRoot, "examples", "minimal", "capture.lua")
              "--output"
              minimalOutput ]
            directory.Path
            [ "VISET_BROWSER", browserExecutable directory
              "VISET_EXAMPLE_PORT", string minimalPort
              "VISET_EXAMPLE_PYTHON", pythonPath ]
            (TimeSpan.FromMinutes 2.0)
        |> requireSuccess

        requirePortClosed minimalPort
        assertInventory minimalOutput [ "screenshots/home.png" ]
        assertPng (Path.Combine(minimalOutput, "screenshots", "home.png")) 960 600

        let mediumOutput = Path.Combine(directory.Path, "medium-output")

        [ "screenshots.lua"; "home-scroll.lua" ]
        |> List.iter (fun script ->
            let port = freePort ()

            run
                binaryPath
                [ "capture"
                  Path.Combine(repositoryRoot, "examples", "medium", script)
                  "--output"
                  mediumOutput ]
                directory.Path
                [ "VISET_BROWSER", browserExecutable directory
                  "VISET_EXAMPLE_PORT", string port
                  "VISET_EXAMPLE_PYTHON", pythonPath ]
                (TimeSpan.FromMinutes 2.0)
            |> requireSuccess

            requirePortClosed port)

        assertInventory
            mediumOutput
            [ "animations/laptop-dark-home-scroll.webp"
              "animations/laptop-light-home-scroll.webp"
              "animations/phone-dark-home-scroll.webp"
              "animations/phone-light-home-scroll.webp"
              "screenshots/laptop-dark.png"
              "screenshots/laptop-light.png"
              "screenshots/phone-dark.png"
              "screenshots/phone-light.png" ]

        assertPng (Path.Combine(mediumOutput, "screenshots", "laptop-light.png")) 1308 840
        assertPng (Path.Combine(mediumOutput, "screenshots", "phone-light.png")) 462 956

        [ "laptop-light-home-scroll.webp", 1308, 840
          "laptop-dark-home-scroll.webp", 1308, 840
          "phone-light-home-scroll.webp", 462, 956
          "phone-dark-home-scroll.webp", 462, 956 ]
        |> List.iter (fun (name, width, height) ->
            let path = Path.Combine(mediumOutput, "animations", name)
            assertWebP path width height None 1 None)

    [<Test; NonParallelizable; Category("Smoke")>]
    let ``the packaged binary should capture the smoke fixture`` () =
        use directory = createTemporaryDirectory "smoke"
        assertBinaryExists ()

        run binaryPath [ "--version" ] directory.Path [] (TimeSpan.FromSeconds 30.0)
        |> fun result ->
            requireSuccess result
            result.StandardOutput.Trim() |> should equal "viset 0.1.0"

        run binaryPath [ "--help" ] directory.Path [] (TimeSpan.FromSeconds 30.0)
        |> fun result ->
            requireSuccess result
            result.StandardOutput |> should contain "viset capture CAPTURE.lua"

        let script =
            writeScript
                directory
                "smoke.lua"
                """--[[
# viset
version = 1
output = "smoke.png"

[devices.desktop]

[devices.desktop.viewport]
width = 320
height = 240
]]

viset.page.navigate("data:text/html,<style>html{background:transparent}body{color:%23126;font:32px sans-serif}</style><h1>Viset</h1>")
viset.page.wait_for("document.readyState === 'complete'", "10s")
viset.snapshot()
"""

        let outputDirectory = Path.Combine(directory.Path, "output")
        let port, result = runCapture directory script outputDirectory [] []
        requireSuccess result
        requirePortClosed port
        assertInventory outputDirectory [ "smoke.png" ]
        assertPng (Path.Combine(outputDirectory, "smoke.png")) 320 240
