namespace Viset.Tests

open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open FsUnit
open NUnit.Framework
open Viset
open Viset.Tests.TestSupport

module RecordingStateTests =
    let private segment stopSignal =
        { TimelineOffset = 0
          Stopwatch = Stopwatch()
          StopSignal = stopSignal
          CaptureLoop = Task.CompletedTask }

    [<Test>]
    let ``starting an idle recording should activate its segment`` () =
        use stopSignal = new CancellationTokenSource()
        let active = RecordingState.start (segment stopSignal) Idle |> resultValue

        RecordingState.isActive active |> should equal true

    [<Test>]
    let ``stopping and restarting a recording should preserve legal transitions`` () =
        use stopSignal = new CancellationTokenSource()
        let expectedSegment = segment stopSignal
        let active = RecordingState.start expectedSegment Idle |> resultValue
        let stoppedSegment, stopped = RecordingState.stop active |> resultValue
        let restarted = RecordingState.start expectedSegment stopped |> resultValue

        stoppedSegment |> should equal expectedSegment
        RecordingState.isActive stopped |> should equal false
        RecordingState.isActive restarted |> should equal true

    [<Test>]
    let ``illegal recording transitions should return stable diagnostics`` () =
        use stopSignal = new CancellationTokenSource()
        let active = RecordingState.start (segment stopSignal) Idle |> resultValue
        let _, stopped = RecordingState.stop active |> resultValue
        let finalized = RecordingState.finalize stopped |> resultValue

        RecordingState.stop Idle
        |> resultError
        |> should equal "The recording is already stopped."

        RecordingState.finalize active
        |> resultError
        |> should equal "The active recording must be stopped before it is finalized."

        RecordingState.canStart active
        |> resultError
        |> should equal "The recording is already started."

        RecordingState.stop stopped
        |> resultError
        |> should equal "The recording is already stopped."

        RecordingState.canStart finalized
        |> resultError
        |> should equal "The recording has already been finalized."

        RecordingState.stop finalized
        |> resultError
        |> should equal "The recording has already been finalized."

        RecordingState.finalize finalized
        |> resultError
        |> should equal "The recording has already been finalized."

    [<Test>]
    let ``disposing an active recording should expose its segment and finalize the state`` () =
        use stopSignal = new CancellationTokenSource()
        let expectedSegment = segment stopSignal
        let active = RecordingState.start expectedSegment Idle |> resultValue
        let disposedSegment, disposed = RecordingState.dispose active

        disposedSegment |> should equal (Some expectedSegment)

        RecordingState.canStart disposed
        |> resultError
        |> should equal "The recording has already been finalized."
