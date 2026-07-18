namespace Viset

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

module internal LibWebPAnimEncoder =
    let encodeAsync
        (options: WebPOptions)
        framesPerSecond
        frameCount
        (readFrameAsync: int -> CancellationToken -> Task<CompressedFrame>)
        spilledFrameCount
        (cancellationToken: CancellationToken)
        =
        task {
            let total = Stopwatch.StartNew()

            let ticks =
                WebPEncoding.frameTicksMilliseconds framesPerSecond frameCount |> List.toArray

            let! firstSource = readFrameAsync 0 cancellationToken
            let firstFrame, firstDecodeDuration = ImageDecoder.decodeMeasured firstSource
            let encoder = WebPNative.createAnimation options firstFrame.Width firstFrame.Height

            try
                let decodeDurations = ResizeArray<TimeSpan>(frameCount)
                let encodeDurations = ResizeArray<TimeSpan>(frameCount)
                let mutable timestamp = 0

                for index in 0 .. frameCount - 1 do
                    cancellationToken.ThrowIfCancellationRequested()

                    let! frame, decodeDuration =
                        if index = 0 then
                            Task.FromResult(firstFrame, firstDecodeDuration)
                        else
                            task {
                                let! source = readFrameAsync index cancellationToken
                                return ImageDecoder.decodeMeasured source
                            }

                    if WebPNative.animationDimensions encoder <> (frame.Width, frame.Height) then
                        invalidArg (nameof frameCount) "Animated WebP frames must have identical dimensions."

                    let encode = Stopwatch.StartNew()
                    WebPNative.addAnimationFrame encoder timestamp frame.Rgba
                    encode.Stop()
                    decodeDurations.Add decodeDuration
                    encodeDurations.Add encode.Elapsed

                    if timestamp > Int32.MaxValue - ticks[index] then
                        invalidArg (nameof frameCount) "Animated WebP timeline exceeds libwebp_anim's timestamp limit."

                    timestamp <- timestamp + ticks[index]

                let mux = Stopwatch.StartNew()
                let bytes = WebPNative.assembleAnimation encoder timestamp
                let encodedFrameCount = bytes |> WebPContainer.parse |> WebPContainer.frameCount
                mux.Stop()
                total.Stop()

                return
                    { Bytes = bytes
                      FrameTicksMs = List.ofArray ticks
                      Metrics =
                        PerformanceMetrics.webP
                            { Encoder = options.Encoder
                              Pipeline = options.Pipeline
                              FrameCount = frameCount
                              EncodedFrameCount = encodedFrameCount
                              SpilledFrameCount = spilledFrameCount
                              WorkerCount = 1
                              DecodeDurations = List.ofSeq decodeDurations
                              EncodeDurations = List.ofSeq encodeDurations
                              MuxDuration = mux.Elapsed
                              TotalDuration = total.Elapsed } }
            finally
                WebPNative.disposeAnimation encoder
        }
