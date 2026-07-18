namespace Viset

open System
open System.Diagnostics
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

type internal SourceFrame =
    { Frame: CompressedFrame
      AcquisitionDuration: TimeSpan }

type internal IContinuousFrameSource =
    abstract StartAsync: CancellationToken -> Task
    abstract CaptureAsync: CancellationToken -> Task<SourceFrame>
    abstract StopAsync: CancellationToken -> Task
    abstract DroppedFrames: int

type internal ScreencastFrameSource(session: CaptureSession, source: WebPSource) =
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
                        { Frame =
                            { Format =
                                match source with
                                | PngScreencast -> PngImage
                                | JpegScreencast _ -> JpegImage
                              Bytes = frame.Bytes }
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
                    do! session.Page.StartScreencastAsync(source, cancellationToken)
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
