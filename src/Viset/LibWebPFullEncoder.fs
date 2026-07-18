namespace Viset

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

module internal LibWebPFullEncoder =
    type private FullFrameWork =
        { Sequence: int
          Source: CompressedFrame
          Duration: int64
          Decoded: (ImageDecoder.DecodedFrame * TimeSpan) option }

    type private FullEncodedFrame =
        { Sequence: int
          Duration: int64
          Bytes: byte array
          DecodeDuration: TimeSpan
          EncodeDuration: TimeSpan }

    let private addFullFrameWithDuration (encoder: WebPNative.FullEncoder) duration bytes =
        let durations =
            duration
            |> FrameCoalescing.splitDuration WebPEncoding.MaximumFrameDurationMilliseconds

        durations
        |> List.iter (fun current -> WebPNative.addFullFrame encoder current bytes)

        durations.Length

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
            let firstDecoded = ImageDecoder.decodeMeasured firstSource
            let firstFrame, _ = firstDecoded
            let encoder = WebPNative.createFull options firstFrame.Width firstFrame.Height

            try
                let workerLimit = max 1 (min 4 Environment.ProcessorCount)
                let pending = ResizeArray<FullFrameWork>(workerLimit)
                let decodeDurations = ResizeArray<TimeSpan>()
                let encodeDurations = ResizeArray<TimeSpan>()
                let mutable encodedSourceRunCount = 0
                let mutable encodedFrameCount = 0
                let mutable muxDuration = TimeSpan.Zero

                let flushAsync () =
                    task {
                        if pending.Count > 0 then
                            let jobs =
                                pending
                                |> Seq.map (fun work ->
                                    Task.Run(fun () ->
                                        cancellationToken.ThrowIfCancellationRequested()

                                        let frame, decodeDuration =
                                            match work.Decoded with
                                            | Some decoded -> decoded
                                            | None -> ImageDecoder.decodeMeasured work.Source

                                        if WebPNative.fullDimensions encoder <> (frame.Width, frame.Height) then
                                            invalidArg
                                                (nameof frameCount)
                                                "Animated WebP frames must have identical dimensions."

                                        let encode = Stopwatch.StartNew()
                                        let bytes = WebPNative.encodeFullFrame encoder frame.Rgba
                                        encode.Stop()

                                        { Sequence = work.Sequence
                                          Duration = work.Duration
                                          Bytes = bytes
                                          DecodeDuration = decodeDuration
                                          EncodeDuration = encode.Elapsed }))
                                |> Seq.toArray

                            let! completed = Task.WhenAll jobs
                            let mux = Stopwatch.StartNew()

                            for frame in completed |> Array.sortBy _.Sequence do
                                encodedFrameCount <-
                                    encodedFrameCount + addFullFrameWithDuration encoder frame.Duration frame.Bytes

                                encodedSourceRunCount <- encodedSourceRunCount + 1
                                decodeDurations.Add frame.DecodeDuration
                                encodeDurations.Add frame.EncodeDuration

                            mux.Stop()
                            muxDuration <- muxDuration + mux.Elapsed
                            pending.Clear()
                    }

                let mutable coalescing = FrameCoalescing.start firstSource ticks[0]

                let scheduleAsync (run: CoalescedSourceRun) =
                    task {
                        pending.Add
                            { Sequence = run.Sequence
                              Source = run.Source
                              Duration = run.Duration
                              Decoded = if run.Sequence = 0 then Some firstDecoded else None }

                        if pending.Count = workerLimit then
                            do! flushAsync ()
                    }

                for index in 1 .. frameCount - 1 do
                    cancellationToken.ThrowIfCancellationRequested()
                    let! current = readFrameAsync index cancellationToken
                    let next, completed = FrameCoalescing.step current ticks[index] coalescing
                    coalescing <- next

                    match completed with
                    | Some run -> do! scheduleAsync run
                    | None -> ()

                do! scheduleAsync (FrameCoalescing.finish coalescing)
                do! flushAsync ()
                cancellationToken.ThrowIfCancellationRequested()

                let mux = Stopwatch.StartNew()
                let bytes = WebPNative.assembleFull encoder
                mux.Stop()
                muxDuration <- muxDuration + mux.Elapsed
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
                              WorkerCount = min workerLimit encodedSourceRunCount
                              DecodeDurations = List.ofSeq decodeDurations
                              EncodeDurations = List.ofSeq encodeDurations
                              MuxDuration = muxDuration
                              TotalDuration = total.Elapsed } }
            finally
                WebPNative.disposeFull encoder
        }
