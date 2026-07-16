namespace Viset

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

type RecordedAnimation =
    { Encoded: EncodedAnimation
      Metrics: CapturePerformanceMetrics }

    override animation.ToString() = animation.Metrics.ToString()

type internal SourceFrame =
    { Bytes: byte array
      AcquisitionDuration: TimeSpan }

type internal IContinuousFrameSource =
    abstract StartAsync: CancellationToken -> Task
    abstract CaptureAsync: CancellationToken -> Task<SourceFrame>
    abstract StopAsync: CancellationToken -> Task
    abstract DroppedFrames: int

type internal ScreencastFrameSource(session: CaptureSession) =
    let mutable active = false
    let mutable segmentCancellation: CancellationTokenSource option = None
    let mutable segmentFrames: Channel<SourceFrame> option = None
    let mutable pumpTask: Task option = None
    let mutable droppedFrames = 0

    let createFrameChannel () =
        let options = BoundedChannelOptions(1)
        options.SingleReader <- true
        options.SingleWriter <- true
        options.FullMode <- BoundedChannelFullMode.Wait
        Channel.CreateBounded<SourceFrame> options

    let pumpAsync (frames: Channel<SourceFrame>) (cancellationToken: CancellationToken) =
        let rec pump () =
            task {
                try
                    let acquisition = Stopwatch.StartNew()
                    let! frame = session.Page.ReadScreencastFrameAsync cancellationToken
                    do! session.Page.AcknowledgeScreencastFrameAsync(frame.SessionId, cancellationToken)
                    acquisition.Stop()

                    let sourceFrame =
                        { Bytes = frame.Bytes
                          AcquisitionDuration = acquisition.Elapsed }

                    let mutable discarded = Unchecked.defaultof<SourceFrame>

                    while frames.Reader.TryRead(&discarded) do
                        Interlocked.Increment(&droppedFrames) |> ignore

                    if not (frames.Writer.TryWrite sourceFrame) then
                        invalidOp "The screencast frame buffer rejected a frame."

                    return! pump ()
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    frames.Writer.TryComplete() |> ignore
                | error ->
                    frames.Writer.TryComplete error |> ignore
                    return raise error
            }

        pump () :> Task

    let clearSegment () =
        segmentCancellation |> Option.iter (fun value -> value.Dispose())
        segmentCancellation <- None
        segmentFrames <- None
        pumpTask <- None

    let stopAsync (cancellationToken: CancellationToken) =
        task {
            if not active then
                invalidOp "The screencast source is already stopped."

            active <- false

            let cancellation =
                segmentCancellation
                |> Option.defaultWith (fun () -> invalidOp "The active screencast source has no cancellation signal.")

            let frames =
                segmentFrames
                |> Option.defaultWith (fun () -> invalidOp "The active screencast source has no frame buffer.")

            let pump =
                pumpTask
                |> Option.defaultWith (fun () -> invalidOp "The active screencast source has no frame pump.")

            let failures = ResizeArray<Exception>()
            cancellation.Cancel()

            try
                do! pump
            with error ->
                failures.Add error

            try
                do! session.Page.StopScreencastAsync cancellationToken
            with error ->
                failures.Add error

            let mutable discarded = Unchecked.defaultof<SourceFrame>

            while frames.Reader.TryRead(&discarded) do
                Interlocked.Increment(&droppedFrames) |> ignore

            clearSegment ()

            match List.ofSeq failures with
            | [] -> ()
            | [ error ] -> return raise error
            | errors ->
                return
                    raise (
                        InvalidOperationException(
                            String.Concat(
                                "Stopping the screencast source produced multiple failures: ",
                                String.Join(" ", errors |> List.map (fun error -> error.Message))
                            ),
                            AggregateException errors
                        )
                    )
        }

    interface IContinuousFrameSource with
        member _.StartAsync(cancellationToken) =
            task {
                if active then
                    invalidOp "The screencast source is already started."

                let cancellation = CancellationTokenSource.CreateLinkedTokenSource cancellationToken

                let frames = createFrameChannel ()

                try
                    do! session.Page.StartScreencastAsync cancellationToken
                    segmentCancellation <- Some cancellation
                    segmentFrames <- Some frames
                    pumpTask <- Some(pumpAsync frames cancellation.Token)
                    active <- true
                with error ->
                    cancellation.Cancel()
                    cancellation.Dispose()
                    return raise error
            }

        member _.CaptureAsync(cancellationToken) =
            task {
                if not active then
                    invalidOp "The screencast source must be started before capturing a frame."

                let frames =
                    segmentFrames
                    |> Option.defaultWith (fun () -> invalidOp "The active screencast source has no frame buffer.")

                try
                    return! frames.Reader.ReadAsync(cancellationToken).AsTask()
                with :? ChannelClosedException as error ->
                    match Option.ofObj error.InnerException with
                    | Some innerError -> return raise innerError
                    | None -> return raise error
            }

        member _.StopAsync(cancellationToken) = stopAsync cancellationToken

        member _.DroppedFrames = Volatile.Read(&droppedFrames)

type private FrameSpool() =
    let root =
        Path.Combine(Path.GetTempPath(), String.Concat("viset-recording-", Guid.NewGuid().ToString("N")))

    let paths = ResizeArray<string>()
    let mutable disposed = 0

    do Directory.CreateDirectory root |> ignore

    member _.Count = paths.Count

    member _.AddAsync(bytes: byte array, cancellationToken: CancellationToken) =
        task {
            ArgumentNullException.ThrowIfNull bytes

            if bytes.Length = 0 then
                invalidArg (nameof bytes) "Recorded frame bytes must not be empty."

            let index = paths.Count

            let path =
                Path.Combine(root, String.Concat("frame-", index.ToString("D8", CultureInfo.InvariantCulture), ".png"))

            do! File.WriteAllBytesAsync(path, bytes, cancellationToken)
            paths.Add path
            return index
        }

    member _.ReadAsync(index: int, cancellationToken: CancellationToken) =
        if index < 0 || index >= paths.Count then
            invalidArg (nameof index) "Recorded frame index is outside the spool."

        File.ReadAllBytesAsync(paths[index], cancellationToken)

    member private _.DisposeCore() =
        if Interlocked.Exchange(&disposed, 1) = 0 && Directory.Exists root then
            Directory.Delete(root, true)

    interface IDisposable with
        member this.Dispose() = this.DisposeCore()

type RecordingController
    private
    (
        session: CaptureSession,
        framesPerSecond: int,
        frameSource: IContinuousFrameSource,
        cancellationToken: CancellationToken
    ) =
    let interval = TimeSpan.FromSeconds(1.0 / double framesPerSecond)
    let spool = new FrameSpool()
    let timeline = ResizeArray<int>()
    let captureDurations = ResizeArray<TimeSpan>()
    let mutable active = false
    let mutable finalized = false
    let mutable segmentOffset = 0
    let mutable segmentStopwatch: Stopwatch option = None
    let mutable stopDelay: CancellationTokenSource option = None
    let mutable captureLoop: Task option = None
    let mutable activeDuration = TimeSpan.Zero
    let mutable missedSlots = 0
    let mutable duplicatedFrames = 0
    let mutable disposed = 0

    do
        if framesPerSecond < 1 || framesPerSecond > 60 then
            invalidArg (nameof framesPerSecond) "Recording FPS must be between 1 and 60."

    let appendDuplicate () =
        if timeline.Count = 0 then
            invalidOp "A recording cannot duplicate a frame before its first capture."

        timeline.Add timeline[timeline.Count - 1]
        missedSlots <- missedSlots + 1
        duplicatedFrames <- duplicatedFrames + 1

    let captureAndStoreAsync (captureCancellation: CancellationToken) =
        task {
            let! frame = frameSource.CaptureAsync captureCancellation
            captureDurations.Add frame.AcquisitionDuration
            let! index = spool.AddAsync(frame.Bytes, captureCancellation)
            return index
        }

    let delayUntilAsync (stopwatch: Stopwatch) slot (delayCancellation: CancellationToken) =
        task {
            let due = TimeSpan.FromTicks(interval.Ticks * int64 slot)
            let remaining = due - stopwatch.Elapsed

            if remaining > TimeSpan.Zero then
                do! Task.Delay(remaining, delayCancellation)
        }

    let startCaptureLoop (stopwatch: Stopwatch) (delayCancellation: CancellationToken) =
        let rec run nextSlot =
            task {
                try
                    do! delayUntilAsync stopwatch nextSlot delayCancellation

                    if not delayCancellation.IsCancellationRequested then
                        let! frameIndex = captureAndStoreAsync delayCancellation

                        let elapsedSlots =
                            max
                                nextSlot
                                (int (Math.Floor(stopwatch.Elapsed.TotalMilliseconds / interval.TotalMilliseconds)))

                        let segmentCount = timeline.Count - segmentOffset

                        for _ in segmentCount .. elapsedSlots - 1 do
                            appendDuplicate ()

                        timeline.Add frameIndex
                        return! run (elapsedSlots + 1)
                with :? OperationCanceledException when delayCancellation.IsCancellationRequested ->
                    ()
            }

        run 1 :> Task

    member _.IsActive = active
    member _.HasStarted = timeline.Count > 0

    member _.StartAsync() =
        task {
            if finalized then
                invalidOp "The recording has already been finalized."

            if active then
                invalidOp "The recording is already started."

            do! frameSource.StartAsync cancellationToken

            try
                let! firstFrame = captureAndStoreAsync cancellationToken
                segmentOffset <- timeline.Count
                timeline.Add firstFrame
                let stopwatch = Stopwatch.StartNew()

                let delayCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource cancellationToken

                segmentStopwatch <- Some stopwatch
                stopDelay <- Some delayCancellation
                captureLoop <- Some(startCaptureLoop stopwatch delayCancellation.Token)
                active <- true
            with error ->
                try
                    do! frameSource.StopAsync CancellationToken.None
                with cleanupError ->
                    return
                        raise (
                            InvalidOperationException(
                                String.Concat(error.Message, " Screencast cleanup also failed: ", cleanupError.Message),
                                error
                            )
                        )

                return raise error
        }

    member _.StopAsync() =
        task {
            if finalized then
                invalidOp "The recording has already been finalized."

            if not active then
                invalidOp "The recording is already stopped."

            let stopwatch =
                segmentStopwatch
                |> Option.defaultWith (fun () -> invalidOp "The active recording has no segment clock.")

            let segmentElapsed = stopwatch.Elapsed

            let delayCancellation =
                stopDelay
                |> Option.defaultWith (fun () -> invalidOp "The active recording has no stop signal.")

            active <- false
            delayCancellation.Cancel()
            let failures = ResizeArray<Exception>()

            match captureLoop with
            | Some loop ->
                try
                    do! loop
                with error ->
                    failures.Add error
            | None -> ()

            try
                do! frameSource.StopAsync CancellationToken.None
            with error ->
                failures.Add error

            stopwatch.Stop()
            activeDuration <- activeDuration + segmentElapsed

            let targetCount =
                max
                    1
                    (int (
                        Math.Round(
                            segmentElapsed.TotalMilliseconds / interval.TotalMilliseconds,
                            MidpointRounding.AwayFromZero
                        )
                    ))

            let targetTotal = segmentOffset + targetCount

            while timeline.Count > targetTotal do
                timeline.RemoveAt(timeline.Count - 1)

            while timeline.Count < targetTotal do
                appendDuplicate ()

            delayCancellation.Dispose()
            segmentStopwatch <- None
            stopDelay <- None
            captureLoop <- None

            match List.ofSeq failures with
            | [] -> ()
            | [ error ] -> return raise error
            | errors ->
                return
                    raise (
                        InvalidOperationException(
                            String.Concat(
                                "Stopping the recording produced multiple failures: ",
                                String.Join(" ", errors |> List.map (fun error -> error.Message))
                            ),
                            AggregateException errors
                        )
                    )
        }

    member this.FinalizeAsync(cancellationToken: CancellationToken) =
        task {
            if finalized then
                invalidOp "The recording has already been finalized."

            if active then
                do! this.StopAsync()

            finalized <- true

            if timeline.Count < 2 then
                invalidOp "A WebP recording must contain at least two visible timeline frames."

            let frames = ResizeArray<byte array>(timeline.Count)

            for frameIndex in timeline do
                let! raw = spool.ReadAsync(frameIndex, cancellationToken)
                let! framed = session.FramePngAsync(raw, cancellationToken)
                frames.Add framed

            let encoded = Media.encodeAnimatedWebP framesPerSecond (List.ofSeq frames)

            return
                { Encoded = encoded
                  Metrics =
                    { Backend = CdpScreencast
                      FrameCount = timeline.Count
                      UniqueFrameCount = spool.Count
                      ActiveDuration = activeDuration
                      CaptureDurations = List.ofSeq captureDurations
                      MissedSlots = missedSlots
                      DuplicatedFrames = duplicatedFrames
                      DroppedFrames = frameSource.DroppedFrames } }
        }

    member private _.DisposeCore() =
        if Interlocked.Exchange(&disposed, 1) = 0 then
            stopDelay |> Option.iter (fun value -> value.Cancel())
            stopDelay |> Option.iter (fun value -> value.Dispose())
            (spool :> IDisposable).Dispose()

    interface IDisposable with
        member this.Dispose() = this.DisposeCore()

    static member CreateScreencast
        (session: CaptureSession, framesPerSecond: int, cancellationToken: CancellationToken)
        =
        let source = ScreencastFrameSource(session) :> IContinuousFrameSource
        new RecordingController(session, framesPerSecond, source, cancellationToken)
