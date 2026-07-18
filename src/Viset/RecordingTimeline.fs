namespace Viset

open System

type internal RecordingTimeline =
    private
        { ReverseFrameIndices: int list
          FrameCount: int
          ActiveDuration: TimeSpan
          MissedSlots: int
          DuplicatedFrames: int }

module internal RecordingTimeline =
    let empty =
        { ReverseFrameIndices = []
          FrameCount = 0
          ActiveDuration = TimeSpan.Zero
          MissedSlots = 0
          DuplicatedFrames = 0 }

    let frameCount timeline = timeline.FrameCount
    let hasFrames timeline = timeline.FrameCount > 0
    let activeDuration timeline = timeline.ActiveDuration
    let missedSlots timeline = timeline.MissedSlots
    let duplicatedFrames timeline = timeline.DuplicatedFrames

    let appendFrame frameIndex timeline =
        { timeline with
            ReverseFrameIndices = frameIndex :: timeline.ReverseFrameIndices
            FrameCount = timeline.FrameCount + 1 }

    let private appendDuplicates count timeline =
        if count <= 0 then
            timeline
        else
            match timeline.ReverseFrameIndices with
            | [] -> invalidOp "A recording cannot duplicate a frame before its first capture."
            | previous :: _ ->
                { timeline with
                    ReverseFrameIndices = List.replicate count previous @ timeline.ReverseFrameIndices
                    FrameCount = timeline.FrameCount + count
                    MissedSlots = timeline.MissedSlots + count
                    DuplicatedFrames = timeline.DuplicatedFrames + count }

    let capture timelineOffset elapsedSlots frameIndex timeline =
        let segmentFrameCount = timeline.FrameCount - timelineOffset
        let missed = max 0 (elapsedSlots - segmentFrameCount)
        timeline |> appendDuplicates missed |> appendFrame frameIndex

    let closeSegment (interval: TimeSpan) timelineOffset (segmentElapsed: TimeSpan) (timeline: RecordingTimeline) =
        let targetCount =
            max
                1
                (int (
                    Math.Round(
                        segmentElapsed.TotalMilliseconds / interval.TotalMilliseconds,
                        MidpointRounding.AwayFromZero
                    )
                ))

        let targetTotal = timelineOffset + targetCount

        let adjusted =
            if timeline.FrameCount > targetTotal then
                let excess = timeline.FrameCount - targetTotal

                { timeline with
                    ReverseFrameIndices = timeline.ReverseFrameIndices |> List.skip excess
                    FrameCount = targetTotal }
            else
                timeline |> appendDuplicates (targetTotal - timeline.FrameCount)

        { adjusted with
            ActiveDuration = adjusted.ActiveDuration + segmentElapsed }

    let toArray timeline =
        timeline.ReverseFrameIndices |> List.rev |> List.toArray
