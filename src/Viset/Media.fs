namespace Viset

open System
open System.Globalization
open ImageMagick
open ImageMagick.Formats

type EncodedAnimation =
    { Bytes: byte array
      FrameTicksMs: int list }

    override animation.ToString() =
        String.Concat(animation.FrameTicksMs.Length.ToString(CultureInfo.InvariantCulture), " frames")

module Media =
    let frameTicksMilliseconds framesPerSecond frameCount =
        if framesPerSecond <= 0 then
            invalidArg (nameof framesPerSecond) "Frames per second must be positive."

        if frameCount <= 0 then
            invalidArg (nameof frameCount) "Frame count must be positive."

        let cumulative frameNumber =
            (int64 frameNumber * 1000L + int64 framesPerSecond / 2L) / int64 framesPerSecond

        [ for index in 0 .. frameCount - 1 do
              yield int (cumulative (index + 1) - cumulative index) ]

    let validatePng (bytes: byte array) =
        ArgumentNullException.ThrowIfNull bytes

        if bytes.Length = 0 then
            invalidArg (nameof bytes) "PNG bytes must not be empty."

        use image = new MagickImage(bytes, MagickFormat.Png)

        if image.Width = 0u || image.Height = 0u then
            invalidArg (nameof bytes) "PNG dimensions must be positive."

        bytes

    let encodeAnimatedWebP framesPerSecond (frames: byte array list) =
        if List.isEmpty frames then
            invalidArg (nameof frames) "Animated WebP requires at least one frame."

        let ticks = frameTicksMilliseconds framesPerSecond frames.Length
        use images = new MagickImageCollection()
        let mutable expectedWidth = None
        let mutable expectedHeight = None

        List.zip frames ticks
        |> List.iter (fun (bytes, delay) ->
            validatePng bytes |> ignore
            let image = new MagickImage(bytes, MagickFormat.Png)

            match expectedWidth, expectedHeight with
            | None, None ->
                expectedWidth <- Some image.Width
                expectedHeight <- Some image.Height
            | Some width, Some height when image.Width = width && image.Height = height -> ()
            | _ ->
                image.Dispose()
                invalidArg (nameof frames) "Animated WebP frames must have identical dimensions."

            image.Strip()
            image.Alpha AlphaOption.On
            image.Format <- MagickFormat.WebP
            image.AnimationDelay <- uint32 delay
            image.AnimationTicksPerSecond <- 1000
            image.AnimationIterations <- 0u
            images.Add image)

        let defines = WebPWriteDefines()
        defines.Lossless <- Nullable true
        defines.Exact <- Nullable true
        defines.AlphaQuality <- Nullable 100
        defines.Method <- Nullable 6

        { Bytes = images.ToByteArray defines
          FrameTicksMs = ticks }
