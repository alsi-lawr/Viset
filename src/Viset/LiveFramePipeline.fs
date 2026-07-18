namespace Viset

open System
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

type internal LiveFramePipeline(session: CaptureSession) =
    let root =
        Path.Combine(Path.GetTempPath(), String.Concat("viset-live-recording-", Guid.NewGuid().ToString("N")))

    let options = UnboundedChannelOptions(SingleReader = true, SingleWriter = true)
    let queue = Channel.CreateUnbounded<int> options
    let pending = Collections.Generic.Dictionary<int, PendingFrame>()
    let preparedFrames = ResizeArray<StoredFrame>()
    let syncRoot = obj ()
    let processorCancellation = new CancellationTokenSource()
    let mutable nextIndex = 0
    let mutable memoryFrameCount = 0
    let mutable spilledFrameCount = 0
    let mutable completed = false
    let mutable disposed = 0

    do Directory.CreateDirectory root |> ignore

    let takePending index =
        lock syncRoot (fun () ->
            let mutable pendingFrame = Unchecked.defaultof<PendingFrame>

            if not (pending.TryGetValue(index, &pendingFrame)) then
                invalidOp "The live recording pipeline lost a queued frame."

            pending.Remove index |> ignore

            match pendingFrame with
            | InMemory frame ->
                memoryFrameCount <- memoryFrameCount - 1
                InMemory frame
            | OnDisk stored -> OnDisk stored)

    let processAsync () =
        task {
            try
                let mutable reading = true

                while reading do
                    let! available = queue.Reader.WaitToReadAsync(processorCancellation.Token).AsTask()

                    if not available then
                        reading <- false
                    else
                        let mutable index = 0

                        while queue.Reader.TryRead(&index) do
                            let pendingFrame = takePending index

                            let! source =
                                match pendingFrame with
                                | InMemory frame -> Task.FromResult frame
                                | OnDisk stored -> RecordingPipeline.readAsync stored processorCancellation.Token

                            let! prepared = session.PrepareFrameAsync(source, processorCancellation.Token)

                            let! stored =
                                RecordingPipeline.writeAsync root "prepared-" index prepared processorCancellation.Token

                            if preparedFrames.Count <> index then
                                invalidOp "The live recording pipeline produced frames out of order."

                            preparedFrames.Add stored
            with error ->
                queue.Writer.TryComplete error |> ignore
                return raise error
        }

    let processorTask = processAsync ()

    interface IRecordingFramePipeline with
        member _.AddAsync(frame, cancellationToken) =
            task {
                if completed then
                    invalidOp "The live recording pipeline is already complete."

                if processorTask.IsFaulted then
                    let processorError =
                        processorTask.Exception
                        |> Option.ofObj
                        |> Option.map _.GetBaseException()
                        |> Option.defaultWith (fun () -> invalidOp "The live recording processor failed.")

                    raise processorError

                cancellationToken.ThrowIfCancellationRequested()
                let index = nextIndex

                let reserveMemory =
                    lock syncRoot (fun () ->
                        if memoryFrameCount < RecordingPipeline.LiveMemoryFrameLimit then
                            memoryFrameCount <- memoryFrameCount + 1
                            true
                        else
                            false)

                let! pendingFrame =
                    task {
                        if reserveMemory then
                            return InMemory frame
                        else
                            let! stored = RecordingPipeline.writeAsync root "overflow-" index frame cancellationToken

                            Interlocked.Increment(&spilledFrameCount) |> ignore
                            return OnDisk stored
                    }

                lock syncRoot (fun () -> pending.Add(index, pendingFrame))

                if not (queue.Writer.TryWrite index) then
                    lock syncRoot (fun () ->
                        pending.Remove index |> ignore

                        match pendingFrame with
                        | InMemory _ -> memoryFrameCount <- memoryFrameCount - 1
                        | OnDisk _ -> ())

                    invalidOp "The live recording pipeline rejected a frame."

                nextIndex <- nextIndex + 1
                return index
            }

        member _.CompleteAsync(cancellationToken) =
            task {
                if not completed then
                    completed <- true
                    queue.Writer.TryComplete() |> ignore

                do! processorTask.WaitAsync cancellationToken
            }

        member _.ReadAsync(index, cancellationToken) =
            if not completed || not processorTask.IsCompletedSuccessfully then
                invalidOp "The live recording pipeline must complete before frames are read."

            if index < 0 || index >= preparedFrames.Count then
                invalidArg (nameof index) "Recorded frame index is outside the live pipeline."

            RecordingPipeline.readAsync preparedFrames[index] cancellationToken

        member _.Count = nextIndex
        member _.SpilledFrameCount = Volatile.Read(&spilledFrameCount)

        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                processorCancellation.Cancel()
                let mutable failure = None

                try
                    processorTask.GetAwaiter().GetResult()
                with
                | :? OperationCanceledException -> ()
                | error -> failure <- Some error

                processorCancellation.Dispose()

                if Directory.Exists root then
                    Directory.Delete(root, true)

                failure |> Option.iter raise
