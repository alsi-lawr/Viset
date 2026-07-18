namespace Viset

module internal WebPEncoding =
    [<Literal>]
    let MaximumFrameDurationMilliseconds = 16777215

    let frameTicksMilliseconds framesPerSecond frameCount =
        if framesPerSecond <= 0 then
            invalidArg (nameof framesPerSecond) "Frames per second must be positive."

        if frameCount <= 0 then
            invalidArg (nameof frameCount) "Frame count must be positive."

        let cumulative frameNumber =
            (int64 frameNumber * 1000L + int64 framesPerSecond / 2L) / int64 framesPerSecond

        [ for index in 0 .. frameCount - 1 do
              yield int (cumulative (index + 1) - cumulative index) ]
