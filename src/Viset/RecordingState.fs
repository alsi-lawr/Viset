namespace Viset

open System.Diagnostics
open System.Threading
open System.Threading.Tasks

type internal ActiveRecordingSegment =
    { TimelineOffset: int
      Stopwatch: Stopwatch
      StopSignal: CancellationTokenSource
      CaptureLoop: Task }

type internal RecordingLifecycle =
    | Idle
    | Active of ActiveRecordingSegment
    | Stopped
    | Finalized

module internal RecordingState =
    let isActive state =
        match state with
        | Active _ -> true
        | Idle
        | Stopped
        | Finalized -> false

    let canStart state =
        match state with
        | Finalized -> Error "The recording has already been finalized."
        | Active _ -> Error "The recording is already started."
        | Idle
        | Stopped -> Ok()

    let start segment state =
        canStart state |> Result.map (fun () -> Active segment)

    let stop state =
        match state with
        | Finalized -> Error "The recording has already been finalized."
        | Idle
        | Stopped -> Error "The recording is already stopped."
        | Active segment -> Ok(segment, Stopped)

    let finalize state =
        match state with
        | Finalized -> Error "The recording has already been finalized."
        | Active _ -> Error "The active recording must be stopped before it is finalized."
        | Idle
        | Stopped -> Ok Finalized

    let dispose state =
        match state with
        | Active segment -> Some segment, Finalized
        | Idle
        | Stopped
        | Finalized -> None, Finalized
