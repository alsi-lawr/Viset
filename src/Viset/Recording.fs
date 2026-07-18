namespace Viset

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

type RecordedAnimation =
    { Encoded: EncodedAnimation
      Metrics: CapturePerformanceMetrics }

    override animation.ToString() = animation.Metrics.ToString()

type RecordingController
    private
    (
        session: CaptureSession,
        framesPerSecond: int,
        webPOptions: WebPOptions,
        frameSource: IContinuousFrameSource,
        cancellationToken: CancellationToken
    ) =
    let interval = TimeSpan.FromSeconds(1.0 / double framesPerSecond)

    let pipeline: IRecordingFramePipeline =
        match webPOptions.Pipeline with
        | Spooled -> new SpooledFramePipeline(session)
        | Live -> new LiveFramePipeline(session)

    let captureDurations = ResizeArray<TimeSpan>()
    let mutable lifecycle = Idle
    let mutable timeline = RecordingTimeline.empty
    let mutable disposed = 0

    do
        if framesPerSecond < 1 || framesPerSecond > 60 then
            invalidArg (nameof framesPerSecond) "Recording FPS must be between 1 and 60."

    let transition result =
        match result with
        | Ok value -> value
        | Error message -> invalidOp message

    let captureAndStoreAsync (captureCancellation: CancellationToken) =
        task {
            let! frame = frameSource.CaptureAsync captureCancellation
            captureDurations.Add frame.AcquisitionDuration
            let! index = pipeline.AddAsync(frame.Frame, captureCancellation)
            return index
        }

    let delayUntilAsync (stopwatch: Stopwatch) slot (delayCancellation: CancellationToken) =
        task {
            let due = TimeSpan.FromTicks(interval.Ticks * int64 slot)
            let remaining = due - stopwatch.Elapsed

            if remaining > TimeSpan.Zero then
                do! Task.Delay(remaining, delayCancellation)
        }

    let startCaptureLoop timelineOffset (stopwatch: Stopwatch) (delayCancellation: CancellationToken) =
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

                        timeline <- RecordingTimeline.capture timelineOffset elapsedSlots frameIndex timeline
                        return! run (elapsedSlots + 1)
                with :? OperationCanceledException when delayCancellation.IsCancellationRequested ->
                    ()
            }

        run 1 :> Task

    member _.IsActive = RecordingState.isActive lifecycle
    member _.HasStarted = RecordingTimeline.hasFrames timeline

    member _.StartAsync() =
        task {
            RecordingState.canStart lifecycle |> transition

            do! frameSource.StartAsync cancellationToken

            try
                let! firstFrame = captureAndStoreAsync cancellationToken
                let timelineOffset = RecordingTimeline.frameCount timeline
                timeline <- RecordingTimeline.appendFrame firstFrame timeline
                let stopwatch = Stopwatch.StartNew()

                let stopSignal = CancellationTokenSource.CreateLinkedTokenSource cancellationToken

                let segment =
                    { TimelineOffset = timelineOffset
                      Stopwatch = stopwatch
                      StopSignal = stopSignal
                      CaptureLoop = startCaptureLoop timelineOffset stopwatch stopSignal.Token }

                lifecycle <- RecordingState.start segment lifecycle |> transition
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
            let segment, nextState = RecordingState.stop lifecycle |> transition
            lifecycle <- nextState
            let segmentElapsed = segment.Stopwatch.Elapsed
            segment.StopSignal.Cancel()
            let failures = ResizeArray<Exception>()

            try
                do! segment.CaptureLoop
            with error ->
                failures.Add error

            try
                do! frameSource.StopAsync CancellationToken.None
            with error ->
                failures.Add error

            segment.Stopwatch.Stop()

            timeline <- RecordingTimeline.closeSegment interval segment.TimelineOffset segmentElapsed timeline

            segment.StopSignal.Dispose()

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
            match lifecycle with
            | Active _ -> do! this.StopAsync()
            | Idle
            | Stopped
            | Finalized -> ()

            lifecycle <- RecordingState.finalize lifecycle |> transition

            if RecordingTimeline.frameCount timeline < 2 then
                invalidOp "A WebP recording must contain at least two visible timeline frames."

            do! pipeline.CompleteAsync cancellationToken
            let frameIndices = RecordingTimeline.toArray timeline

            let readFrameAsync logicalIndex readCancellation =
                if logicalIndex < 0 || logicalIndex >= frameIndices.Length then
                    invalidArg (nameof logicalIndex) "Logical frame index is outside the recording timeline."

                pipeline.ReadAsync(frameIndices[logicalIndex], readCancellation)

            let! encoded =
                Media.encodeAnimatedWebPAsync
                    webPOptions
                    framesPerSecond
                    frameIndices.Length
                    readFrameAsync
                    pipeline.SpilledFrameCount
                    cancellationToken

            return
                { Encoded = encoded
                  Metrics =
                    PerformanceMetrics.capture
                        { Source = webPOptions.Source
                          Pipeline = webPOptions.Pipeline
                          FrameCount = frameIndices.Length
                          UniqueFrameCount = pipeline.Count
                          ActiveDuration = RecordingTimeline.activeDuration timeline
                          CaptureDurations = List.ofSeq captureDurations
                          MissedSlots = RecordingTimeline.missedSlots timeline
                          DuplicatedFrames = RecordingTimeline.duplicatedFrames timeline
                          DroppedFrames = frameSource.DroppedFrames } }
        }

    member private _.DisposeCore() =
        if Interlocked.Exchange(&disposed, 1) = 0 then
            let failures = ResizeArray<Exception>()
            let activeSegment, nextState = RecordingState.dispose lifecycle
            lifecycle <- nextState

            activeSegment
            |> Option.iter (fun segment ->
                segment.StopSignal.Cancel()

                try
                    segment.CaptureLoop.GetAwaiter().GetResult()
                with
                | :? OperationCanceledException -> ()
                | error -> failures.Add error

                try
                    frameSource.StopAsync(CancellationToken.None).GetAwaiter().GetResult()
                with error ->
                    failures.Add error

                segment.StopSignal.Dispose())

            try
                pipeline.Dispose()
            with error ->
                failures.Add error

            match List.ofSeq failures with
            | [] -> ()
            | [ error ] -> raise error
            | errors ->
                raise (
                    InvalidOperationException(
                        "Disposing the recording produced multiple failures.",
                        AggregateException errors
                    )
                )

    interface IDisposable with
        member this.Dispose() = this.DisposeCore()

    static member CreateScreencast
        (session: CaptureSession, framesPerSecond: int, webPOptions: WebPOptions, cancellationToken: CancellationToken)
        =
        let source =
            ScreencastFrameSource(session, webPOptions.Source) :> IContinuousFrameSource

        new RecordingController(session, framesPerSecond, webPOptions, source, cancellationToken)
