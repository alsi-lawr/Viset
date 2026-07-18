namespace Viset

open System
open System.Threading
open System.Threading.Tasks

module Media =
    let frameTicksMilliseconds = WebPEncoding.frameTicksMilliseconds

    let validateImage = ImageDecoder.validateImage

    let validatePng = ImageDecoder.validatePng

    let encodeAnimatedWebPAsync
        (options: WebPOptions)
        framesPerSecond
        frameCount
        (readFrameAsync: int -> CancellationToken -> Task<CompressedFrame>)
        spilledFrameCount
        (cancellationToken: CancellationToken)
        =
        if frameCount <= 0 then
            invalidArg (nameof frameCount) "Animated WebP requires at least one frame."

        ArgumentNullException.ThrowIfNull readFrameAsync

        match options.Encoder with
        | LibWebPFull ->
            LibWebPFullEncoder.encodeAsync
                options
                framesPerSecond
                frameCount
                readFrameAsync
                spilledFrameCount
                cancellationToken
        | LibWebPAnim ->
            LibWebPAnimEncoder.encodeAsync
                options
                framesPerSecond
                frameCount
                readFrameAsync
                spilledFrameCount
                cancellationToken
        | Ffmpeg executable ->
            FfmpegWebPEncoder.encodeAsync
                executable
                options
                framesPerSecond
                frameCount
                readFrameAsync
                spilledFrameCount
                cancellationToken
