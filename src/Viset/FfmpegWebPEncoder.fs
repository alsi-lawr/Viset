namespace Viset

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Threading
open System.Threading.Tasks

module internal FfmpegWebPEncoder =
    let private ffmpegCodec format =
        match format with
        | PngImage -> "png"
        | JpegImage -> "mjpeg"

    let encodeAsync
        executable
        (options: WebPOptions)
        framesPerSecond
        frameCount
        (readFrameAsync: int -> CancellationToken -> Task<CompressedFrame>)
        spilledFrameCount
        (cancellationToken: CancellationToken)
        =
        task {
            let total = Stopwatch.StartNew()
            let ticks = WebPEncoding.frameTicksMilliseconds framesPerSecond frameCount
            let! firstFrame = readFrameAsync 0 cancellationToken
            ImageDecoder.validateImage firstFrame |> ignore

            let outputPath =
                Path.Combine(Path.GetTempPath(), String.Concat("viset-", Guid.NewGuid().ToString("N"), ".webp"))

            let startInfo = ProcessStartInfo(executable)
            startInfo.UseShellExecute <- false
            startInfo.CreateNoWindow <- true
            startInfo.RedirectStandardInput <- true
            startInfo.RedirectStandardError <- true

            let lossless =
                match options.Mode with
                | Lossy _ -> false
                | Lossless _ -> true

            let arguments =
                [ "-hide_banner"
                  "-loglevel"
                  "error"
                  "-y"
                  "-f"
                  "image2pipe"
                  "-framerate"
                  framesPerSecond.ToString(CultureInfo.InvariantCulture)
                  "-vcodec"
                  ffmpegCodec firstFrame.Format
                  "-i"
                  "pipe:0"
                  "-an"
                  "-c:v"
                  "libwebp_anim"
                  "-lossless"
                  if lossless then "1" else "0"
                  "-compression_level"
                  options.Method.ToString(CultureInfo.InvariantCulture)
                  "-quality"
                  options.Mode.Quality.ToString("0.###", CultureInfo.InvariantCulture)
                  "-pix_fmt"
                  if lossless then "bgra" else "yuva420p"
                  "-loop"
                  "0"
                  "-frames:v"
                  frameCount.ToString(CultureInfo.InvariantCulture)
                  outputPath ]

            arguments |> List.iter startInfo.ArgumentList.Add

            use ffmpegProcess = new Process(StartInfo = startInfo)
            let mutable started = false

            try
                if not (ffmpegProcess.Start()) then
                    invalidOp "ffmpeg could not be started."

                started <- true
                let standardError = ffmpegProcess.StandardError.ReadToEndAsync()

                try
                    for index in 0 .. frameCount - 1 do
                        let! frame =
                            if index = 0 then
                                Task.FromResult firstFrame
                            else
                                readFrameAsync index cancellationToken

                        if frame.Format <> firstFrame.Format then
                            invalidOp "FFmpeg animation input frames must use one compressed image format."

                        do! ffmpegProcess.StandardInput.BaseStream.WriteAsync(frame.Bytes, cancellationToken)

                    ffmpegProcess.StandardInput.Close()
                    do! ffmpegProcess.WaitForExitAsync cancellationToken
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    if not ffmpegProcess.HasExited then
                        ffmpegProcess.Kill true

                    raise (OperationCanceledException cancellationToken)
                | writeError ->
                    if not ffmpegProcess.HasExited then
                        ffmpegProcess.Kill true

                    do! ffmpegProcess.WaitForExitAsync CancellationToken.None

                    let! errorText = standardError

                    raise (
                        InvalidOperationException(
                            String.Concat("ffmpeg could not consume Viset's compressed frames: ", errorText.Trim()),
                            writeError
                        )
                    )

                let! errorText = standardError

                if ffmpegProcess.ExitCode <> 0 then
                    invalidOp (String.Concat("ffmpeg WebP encoding failed: ", errorText.Trim()))

                let! encodedBytes = File.ReadAllBytesAsync(outputPath, cancellationToken)
                let container = WebPContainer.parse encodedBytes

                WebPContainer.durationPatch WebPEncoding.MaximumFrameDurationMilliseconds (List.sum ticks) container
                |> Option.iter (fun patch ->
                    encodedBytes[patch.Offset] <- byte patch.Duration
                    encodedBytes[patch.Offset + 1] <- byte (patch.Duration >>> 8)
                    encodedBytes[patch.Offset + 2] <- byte (patch.Duration >>> 16))

                total.Stop()

                return
                    { Bytes = encodedBytes
                      FrameTicksMs = ticks
                      Metrics =
                        PerformanceMetrics.webP
                            { Encoder = options.Encoder
                              Pipeline = options.Pipeline
                              FrameCount = frameCount
                              EncodedFrameCount = WebPContainer.frameCount container
                              SpilledFrameCount = spilledFrameCount
                              WorkerCount = 1
                              DecodeDurations = []
                              EncodeDurations = []
                              MuxDuration = TimeSpan.Zero
                              TotalDuration = total.Elapsed } }
            finally
                if started && not ffmpegProcess.HasExited then
                    ffmpegProcess.Kill true

                if File.Exists outputPath then
                    File.Delete outputPath
        }
