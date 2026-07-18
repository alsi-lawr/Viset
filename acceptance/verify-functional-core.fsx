#load "../src/Viset/Domain.fs"
#load "../src/Viset/PerformanceMetrics.fs"
#load "../src/Viset/WebPEncoding.fs"
#load "../src/Viset/WebPContainer.fs"
#load "../src/Viset/FrameCoalescing.fs"
#load "../src/Viset/RecordingState.fs"
#load "../src/Viset/RecordingTimeline.fs"

open System
open System.Buffers.Binary
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Viset

let assertEqual expected actual message =
    if actual <> expected then
        failwithf "%s Expected %A, got %A." message expected actual

let assertError expected result =
    match result with
    | Error message -> assertEqual expected message "Unexpected transition diagnostic."
    | Ok _ -> failwithf "Expected transition failure: %s" expected

let assertThrows expected action =
    try
        action ()
        failwithf "Expected failure: %s" expected
    with error when error.Message = expected ->
        ()

let writeUInt32LittleEndian (bytes: byte array) offset value =
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, sizeof<uint32>), value)

let writeUInt32LittleEndianTo (writer: BinaryWriter) value =
    let bytes = Array.zeroCreate<byte> sizeof<uint32>
    writeUInt32LittleEndian bytes 0 value
    writer.Write bytes

let verify () =
    let segment =
        { TimelineOffset = 0
          Stopwatch = Stopwatch()
          StopSignal = new CancellationTokenSource()
          CaptureLoop = Task.CompletedTask }

    let active = RecordingState.start segment Idle |> Result.defaultWith failwith
    assertEqual true (RecordingState.isActive active) "Active lifecycle state was not observable."

    RecordingState.stop Idle |> assertError "The recording is already stopped."

    RecordingState.finalize active
    |> assertError "The active recording must be stopped before it is finalized."

    RecordingState.canStart active
    |> assertError "The recording is already started."

    let stoppedSegment, stopped =
        RecordingState.stop active |> Result.defaultWith failwith

    assertEqual segment stoppedSegment "Stop did not return the active segment."
    assertEqual false (RecordingState.isActive stopped) "Stopped lifecycle remained active."
    RecordingState.stop stopped |> assertError "The recording is already stopped."

    let restarted = RecordingState.start segment stopped |> Result.defaultWith failwith
    assertEqual true (RecordingState.isActive restarted) "Stopped recording did not restart."

    let _, stoppedAgain = RecordingState.stop restarted |> Result.defaultWith failwith
    let finalized = RecordingState.finalize stoppedAgain |> Result.defaultWith failwith

    RecordingState.canStart finalized
    |> assertError "The recording has already been finalized."

    RecordingState.stop finalized
    |> assertError "The recording has already been finalized."

    RecordingState.finalize finalized
    |> assertError "The recording has already been finalized."

    let disposedSegment, disposed = RecordingState.dispose active
    assertEqual (Some segment) disposedSegment "Disposal did not expose the active segment."

    RecordingState.canStart disposed
    |> assertError "The recording has already been finalized."

    segment.StopSignal.Dispose()

    let interval = TimeSpan.FromMilliseconds 100.0

    let timeline =
        RecordingTimeline.empty
        |> RecordingTimeline.appendFrame 0
        |> RecordingTimeline.capture 0 1 1
        |> RecordingTimeline.capture 0 3 2

    assertEqual [| 0; 1; 1; 2 |] (RecordingTimeline.toArray timeline) "Timeline capture reduction changed."
    assertEqual 1 (RecordingTimeline.missedSlots timeline) "Timeline missed-slot count changed."
    assertEqual 1 (RecordingTimeline.duplicatedFrames timeline) "Timeline duplication count changed."

    let closed =
        RecordingTimeline.closeSegment interval 0 (TimeSpan.FromMilliseconds 300.0) timeline

    assertEqual [| 0; 1; 1 |] (RecordingTimeline.toArray closed) "Timeline close reduction changed."
    assertEqual (TimeSpan.FromMilliseconds 300.0) (RecordingTimeline.activeDuration closed) "Active duration changed."

    let frame bytes = { Format = PngImage; Bytes = bytes }

    let coalescing = FrameCoalescing.start (frame [| 1uy; 2uy |]) 34
    let coalescing, first = FrameCoalescing.step (frame [| 1uy; 2uy |]) 33 coalescing
    assertEqual None first "Equal compressed source bytes were not coalesced."
    let coalescing, first = FrameCoalescing.step (frame [| 3uy |]) 33 coalescing

    let first =
        first
        |> Option.defaultWith (fun () -> failwith "Changed source did not complete a run.")

    assertEqual 0 first.Sequence "First coalesced sequence changed."
    assertEqual 67L first.Duration "First coalesced duration changed."
    let second = FrameCoalescing.finish coalescing
    assertEqual 1 second.Sequence "Second coalesced sequence changed."
    assertEqual 33L second.Duration "Second coalesced duration changed."
    assertEqual [ 50; 17 ] (FrameCoalescing.splitDuration 50 67L) "Duration splitting changed."

    let largeCoalescing = FrameCoalescing.start (frame [| 4uy |]) Int32.MaxValue

    let largeCoalescing, emitted =
        FrameCoalescing.step (frame [| 4uy |]) Int32.MaxValue largeCoalescing

    assertEqual None emitted "A large equal source unexpectedly completed its run."
    assertEqual 4294967294L (FrameCoalescing.finish largeCoalescing).Duration "Coalesced duration overflowed."

    let maximumDuration = WebPEncoding.MaximumFrameDurationMilliseconds

    assertEqual
        [ maximumDuration; 1 ]
        (FrameCoalescing.splitDuration maximumDuration (int64 maximumDuration + 1L))
        "Maximum WebP duration splitting changed."

    let animationFrame duration =
        let data = Array.zeroCreate<byte> 16
        data[12] <- byte duration
        data[13] <- byte (duration >>> 8)
        data[14] <- byte (duration >>> 16)
        Encoding.ASCII.GetBytes("ANMF"), data

    let webP =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.ASCII, true)
        writer.Write(Encoding.ASCII.GetBytes("RIFF"))
        writeUInt32LittleEndianTo writer 0u
        writer.Write(Encoding.ASCII.GetBytes("WEBP"))

        for tag, data in [ animationFrame 10; animationFrame 20 ] do
            writer.Write tag
            writeUInt32LittleEndianTo writer (uint32 data.Length)
            writer.Write data

        writer.Flush()
        let bytes = stream.ToArray()
        writeUInt32LittleEndian bytes 4 (uint32 (bytes.Length - 8))
        bytes

    let container = WebPContainer.parse webP
    assertEqual 2 (WebPContainer.frameCount container) "Animation frame count changed."

    let patch =
        WebPContainer.durationPatch WebPEncoding.MaximumFrameDurationMilliseconds 40 container
        |> Option.defaultWith (fun () -> failwith "Expected a duration patch.")

    assertEqual 30 patch.Duration "Duration patch calculation changed."
    assertEqual 20 (int webP[patch.Offset]) "Container analysis mutated encoded bytes."

    let invalidRiffSize = Array.copy webP
    writeUInt32LittleEndian invalidRiffSize 4 0u

    assertThrows "An encoder returned a WebP container with an invalid RIFF size." (fun () ->
        WebPContainer.parse invalidRiffSize |> ignore)

    let truncatedChunk = Array.copy webP
    writeUInt32LittleEndian truncatedChunk 16 UInt32.MaxValue

    assertThrows "An encoder returned a truncated WebP chunk." (fun () -> WebPContainer.parse truncatedChunk |> ignore)

    let missingFrame =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.ASCII, true)
        writer.Write(Encoding.ASCII.GetBytes("RIFF"))
        writeUInt32LittleEndianTo writer 4u
        writer.Write(Encoding.ASCII.GetBytes("WEBP"))
        writer.Flush()
        stream.ToArray()

    assertThrows "An encoder returned a WebP container without an image frame." (fun () ->
        missingFrame |> WebPContainer.parse |> WebPContainer.frameCount |> ignore)

    let captureMetrics =
        PerformanceMetrics.capture
            { Source = PngScreencast
              Pipeline = Spooled
              FrameCount = 3
              UniqueFrameCount = 2
              ActiveDuration = TimeSpan.FromMilliseconds 100.0
              CaptureDurations = [ TimeSpan.FromMilliseconds 1.0 ]
              MissedSlots = 1
              DuplicatedFrames = 1
              DroppedFrames = 0 }

    assertEqual 3 captureMetrics.FrameCount "Capture metrics construction changed."
    assertEqual 1 captureMetrics.MissedSlots "Capture metrics observations changed."

    let productionMetrics =
        PerformanceMetrics.webP
            { Encoder = LibWebPFull
              Pipeline = Live
              FrameCount = 3
              EncodedFrameCount = 2
              SpilledFrameCount = 1
              WorkerCount = 2
              DecodeDurations = []
              EncodeDurations = []
              MuxDuration = TimeSpan.FromMilliseconds 1.0
              TotalDuration = TimeSpan.FromMilliseconds 2.0 }

    assertEqual 2 productionMetrics.EncodedFrameCount "Production metrics construction changed."
    assertEqual 1 productionMetrics.SpilledFrameCount "Production metrics observations changed."

    printfn "functional core: lifecycle, timeline, coalescing, container, metrics passed"

verify ()
