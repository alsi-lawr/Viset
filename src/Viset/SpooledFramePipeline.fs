namespace Viset

open System
open System.IO
open System.Threading

type internal SpooledFramePipeline(session: CaptureSession) =
    let root =
        Path.Combine(Path.GetTempPath(), String.Concat("viset-spooled-recording-", Guid.NewGuid().ToString("N")))

    let sourceFrames = ResizeArray<StoredFrame>()
    let preparedFrames = ResizeArray<StoredFrame>()
    let mutable completed = false
    let mutable disposed = 0

    do Directory.CreateDirectory root |> ignore

    interface IRecordingFramePipeline with
        member _.AddAsync(frame, cancellationToken) =
            task {
                if completed then
                    invalidOp "The spooled recording pipeline is already complete."

                let index = sourceFrames.Count
                let! stored = RecordingPipeline.writeAsync root "source-" index frame cancellationToken
                sourceFrames.Add stored
                return index
            }

        member _.CompleteAsync(cancellationToken) =
            task {
                if not completed then
                    completed <- true

                    for index in 0 .. sourceFrames.Count - 1 do
                        let! source = RecordingPipeline.readAsync sourceFrames[index] cancellationToken
                        let! prepared = session.PrepareFrameAsync(source, cancellationToken)
                        let! stored = RecordingPipeline.writeAsync root "prepared-" index prepared cancellationToken
                        preparedFrames.Add stored
            }

        member _.ReadAsync(index, cancellationToken) =
            if not completed || preparedFrames.Count <> sourceFrames.Count then
                invalidOp "The spooled recording pipeline must complete before frames are read."

            if index < 0 || index >= preparedFrames.Count then
                invalidArg (nameof index) "Recorded frame index is outside the spooled pipeline."

            RecordingPipeline.readAsync preparedFrames[index] cancellationToken

        member _.Count = sourceFrames.Count
        member _.SpilledFrameCount = 0

        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 && Directory.Exists root then
                Directory.Delete(root, true)
