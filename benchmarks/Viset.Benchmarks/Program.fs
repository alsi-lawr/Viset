namespace Viset.Benchmarks

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Threading
open System.Threading.Tasks
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Columns
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Environments
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Running
open BenchmarkDotNet.Toolchains.NativeAot
open Viset

module private BrowserFixture =
    [<Literal>]
    let Width = 1180

    [<Literal>]
    let Height = 720

    let private html =
        """<!doctype html><html><head><meta charset="utf-8"><style>
html,body{width:100%;height:100%;margin:0;overflow:hidden}
body{display:grid;place-items:center;color:white;font:700 64px system-ui}
</style></head><body><span>0</span><script>
const label=document.querySelector('span');
let frame=0;
function tick(now){
  frame+=1;
  const hue=(now/8)%360;
  document.body.style.background=`linear-gradient(135deg,hsl(${hue} 80% 35%),hsl(${(hue+120)%360} 80% 25%))`;
  label.textContent=String(frame);
  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);
</script></body></html>"""

    let private findRepositoryRoot () =
        let rec find (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "browser-lock.toml")) then
                directory.FullName
            else
                match directory.Parent |> Option.ofObj with
                | Some parent -> find parent
                | None -> invalidOp "Could not locate the Viset repository root."

        find (DirectoryInfo Environment.CurrentDirectory)

    let launchAsync () =
        task {
            let root = findRepositoryRoot ()
            let lockPath = Path.Combine(root, "browser-lock.toml")
            let! resolved = BrowserResolution.resolveAsync None (Some lockPath) CancellationToken.None

            let browser =
                match resolved with
                | Ok value -> value
                | Error message -> invalidOp message

            let options = BrowserSessionOptions(browser.ExecutablePath, Array.empty<string>)
            let! session = BrowserSession.LaunchAsync(options, CancellationToken.None)

            try
                do! session.ConfigureEmulationAsync(Width, Height, 1.0, false, false, CancellationToken.None)

                let uri =
                    Uri(String.Concat("data:text/html;charset=utf-8,", Uri.EscapeDataString html))

                do! session.NavigateAsync(uri, CancellationToken.None)

                let! ready =
                    session.EvaluateAsync(
                        "new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))",
                        CancellationToken.None
                    )

                match ready with
                | Ok _ -> return session
                | Error error -> return raise (InvalidOperationException(error.ToString()))
            with error ->
                do! (session :> IAsyncDisposable).DisposeAsync().AsTask()
                return raise error
        }

    let disposeAsync (session: BrowserSession) =
        (session :> IAsyncDisposable).DisposeAsync().AsTask()

type CaptureBenchmarkConfig() as this =
    inherit ManualConfig()

    do
        let core = Job.Default.WithRuntime(CoreRuntime.Core10_0).WithId("CoreCLR")

        let nativeArguments =
            [| MsBuildArgument("/p:RestoreLockedMode=false") :> Argument
               MsBuildArgument("/p:RestorePackagesWithLockFile=false") :> Argument
               MsBuildArgument("/p:NuGetLockFilePath=BenchmarkDotNet.nativeaot.ignore.lock.json") :> Argument
               MsBuildArgument("/p:SelfContained=true") :> Argument |]
            :> IReadOnlyList<Argument>

        let native =
            Job.Default
                .WithRuntime(NativeAotRuntime.Net10_0)
                .WithToolchain(NativeAotToolchain.Net10_0)
                .WithArguments(nativeArguments)
                .WithId("NativeAOT")

        this.AddJob(core, native) |> ignore
        this.AddColumn(StatisticColumn.P95, StatisticColumn.P100) |> ignore

[<MemoryDiagnoser>]
[<Config(typeof<CaptureBenchmarkConfig>)>]
type ScreenshotBenchmarks() =
    let mutable session: BrowserSession option = None

    [<GlobalSetup>]
    member _.Setup() =
        task {
            let! launched = BrowserFixture.launchAsync ()
            session <- Some launched
        }

    [<GlobalCleanup>]
    member _.Cleanup() =
        task {
            match session with
            | Some browser -> do! BrowserFixture.disposeAsync browser
            | None -> ()
        }

    [<Benchmark>]
    member _.CaptureScreenshot() =
        task {
            let browser =
                session
                |> Option.defaultWith (fun () -> invalidOp "Benchmark browser is not ready.")

            let! bytes = browser.CapturePngAsync CancellationToken.None
            return bytes.Length
        }

[<MemoryDiagnoser>]
[<Config(typeof<CaptureBenchmarkConfig>)>]
type ScreencastBenchmarks() =
    let mutable session: BrowserSession option = None

    [<GlobalSetup>]
    member _.Setup() =
        task {
            let! launched = BrowserFixture.launchAsync ()
            do! launched.StartScreencastAsync(BrowserFixture.Width, BrowserFixture.Height, CancellationToken.None)

            session <- Some launched
        }

    [<GlobalCleanup>]
    member _.Cleanup() =
        task {
            match session with
            | Some browser ->
                try
                    do! browser.StopScreencastAsync CancellationToken.None
                finally
                    BrowserFixture.disposeAsync browser |> fun work -> work.GetAwaiter().GetResult()
            | None -> ()
        }

    [<Benchmark>]
    member _.ReadScreencastFrame() =
        task {
            let browser =
                session
                |> Option.defaultWith (fun () -> invalidOp "Benchmark browser is not ready.")

            let! frame = browser.ReadScreencastFrameAsync CancellationToken.None
            do! browser.AcknowledgeScreencastFrameAsync(frame.SessionId, CancellationToken.None)
            return frame.Bytes.Length
        }

type private ComparisonResult =
    { Backend: string
      FramesPerSecond: int
      TargetFrames: int
      UniqueFrames: int
      DuplicatedFrames: int
      DroppedFrames: int
      P95: TimeSpan
      P99: TimeSpan
      Maximum: TimeSpan }

module private Comparison =
    let private percentile value (durations: TimeSpan list) =
        match durations |> List.sort with
        | [] -> TimeSpan.Zero
        | sorted ->
            let index =
                int (Math.Ceiling(value * double sorted.Length)) - 1
                |> max 0
                |> min (sorted.Length - 1)

            sorted[index]

    let private result backend fps target unique durations =
        { Backend = backend
          FramesPerSecond = fps
          TargetFrames = target
          UniqueFrames = unique
          DuplicatedFrames = max 0 (target - unique)
          DroppedFrames = max 0 (unique - target)
          P95 = percentile 0.95 durations
          P99 = percentile 0.99 durations
          Maximum = durations |> List.fold max TimeSpan.Zero }

    let private screenshotAsync fps duration =
        task {
            let! browser = BrowserFixture.launchAsync ()

            try
                let interval = TimeSpan.FromSeconds(1.0 / double fps)
                let stopwatch = Stopwatch.StartNew()
                let durations = ResizeArray<TimeSpan>()
                let mutable uniqueFrames = 0
                let mutable nextSlot = 0

                while stopwatch.Elapsed < duration do
                    let due = TimeSpan.FromTicks(interval.Ticks * int64 nextSlot)
                    let remaining = due - stopwatch.Elapsed

                    if remaining > TimeSpan.Zero then
                        do! Task.Delay remaining

                    let acquisition = Stopwatch.StartNew()
                    let! _ = browser.CapturePngAsync CancellationToken.None
                    acquisition.Stop()
                    durations.Add acquisition.Elapsed
                    uniqueFrames <- uniqueFrames + 1

                    nextSlot <-
                        int (Math.Floor(stopwatch.Elapsed.TotalMilliseconds / interval.TotalMilliseconds))
                        + 1

                let target =
                    int (Math.Round(duration.TotalSeconds * double fps, MidpointRounding.AwayFromZero))

                return result "captureScreenshot" fps target uniqueFrames (List.ofSeq durations)
            finally
                BrowserFixture.disposeAsync browser |> fun work -> work.GetAwaiter().GetResult()
        }

    let private screencastAsync fps duration =
        task {
            let! browser = BrowserFixture.launchAsync ()

            try
                do! browser.StartScreencastAsync(BrowserFixture.Width, BrowserFixture.Height, CancellationToken.None)

                let stopwatch = Stopwatch.StartNew()
                let waits = ResizeArray<TimeSpan>()
                let mutable uniqueFrames = 0

                while stopwatch.Elapsed < duration do
                    let wait = Stopwatch.StartNew()
                    let! frame = browser.ReadScreencastFrameAsync CancellationToken.None
                    wait.Stop()
                    waits.Add wait.Elapsed
                    uniqueFrames <- uniqueFrames + 1
                    do! browser.AcknowledgeScreencastFrameAsync(frame.SessionId, CancellationToken.None)

                do! browser.StopScreencastAsync CancellationToken.None

                let target =
                    int (Math.Round(duration.TotalSeconds * double fps, MidpointRounding.AwayFromZero))

                return result "startScreencast" fps target uniqueFrames (List.ofSeq waits)
            finally
                BrowserFixture.disposeAsync browser |> fun work -> work.GetAwaiter().GetResult()
        }

    let runAsync () =
        task {
            let duration = TimeSpan.FromSeconds 3.0
            let results = ResizeArray<ComparisonResult>()

            for fps in [ 30; 60 ] do
                let! screenshot = screenshotAsync fps duration
                results.Add screenshot
                let! screencast = screencastAsync fps duration
                results.Add screencast

            Console.Out.WriteLine(
                "backend           fps target unique duplicate dropped p95_ms p99_ms max_ms target_15ms"
            )

            for item in results do
                Console.Out.WriteLine(
                    String.Format(
                        CultureInfo.InvariantCulture,
                        "{0,-17} {1,3} {2,6} {3,6} {4,9} {5,7} {6,6:0.00} {7,6:0.00} {8,6:0.00} {9}",
                        item.Backend,
                        item.FramesPerSecond,
                        item.TargetFrames,
                        item.UniqueFrames,
                        item.DuplicatedFrames,
                        item.DroppedFrames,
                        item.P95.TotalMilliseconds,
                        item.P99.TotalMilliseconds,
                        item.Maximum.TotalMilliseconds,
                        if item.P95.TotalMilliseconds <= 15.0 then
                            "pass"
                        else
                            "fail"
                    )
                )
        }

module Program =
    [<EntryPoint>]
    let main arguments =
        if arguments |> Array.contains "--compare" then
            Comparison.runAsync().GetAwaiter().GetResult()
            0
        else
            BenchmarkSwitcher.FromAssembly(typeof<ScreenshotBenchmarks>.Assembly).Run(arguments)
            |> ignore

            0
