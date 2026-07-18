namespace Viset

open System

type internal CoalescedSourceRun =
    { Sequence: int
      Source: CompressedFrame
      Duration: int64 }

type internal FrameCoalescingState =
    private
        { Sequence: int
          Source: CompressedFrame
          Duration: int64 }

module internal FrameCoalescing =
    let private exactSameSource (left: CompressedFrame) (right: CompressedFrame) =
        left.Format = right.Format
        && (obj.ReferenceEquals(left.Bytes, right.Bytes)
            || left.Bytes.AsSpan().SequenceEqual(right.Bytes.AsSpan()))

    let start source duration =
        { Sequence = 0
          Source = source
          Duration = int64 duration }

    let step source duration (state: FrameCoalescingState) : FrameCoalescingState * CoalescedSourceRun option =
        if exactSameSource state.Source source then
            { state with
                Duration = Checked.(+) state.Duration (int64 duration) },
            None
        else
            let completed: CoalescedSourceRun =
                { Sequence = state.Sequence
                  Source = state.Source
                  Duration = state.Duration }

            { Sequence = state.Sequence + 1
              Source = source
              Duration = int64 duration },
            Some completed

    let finish (state: FrameCoalescingState) : CoalescedSourceRun =
        { Sequence = state.Sequence
          Source = state.Source
          Duration = state.Duration }

    let splitDuration maximumDuration (duration: int64) =
        if maximumDuration <= 0 then
            invalidArg (nameof maximumDuration) "The maximum frame duration must be positive."

        let maximumDuration64 = int64 maximumDuration

        let rec split remaining reversed =
            if remaining <= 0L then
                List.rev reversed
            else
                let current = min remaining maximumDuration64
                split (remaining - current) (int current :: reversed)

        split duration []
