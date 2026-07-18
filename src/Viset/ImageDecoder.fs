namespace Viset

open System
open System.Diagnostics
open StbImageSharp

module internal ImageDecoder =
    type DecodedFrame =
        { Width: int
          Height: int
          Rgba: byte array }

    let validateImage (frame: CompressedFrame) =
        ArgumentNullException.ThrowIfNull frame.Bytes

        if frame.Bytes.Length = 0 then
            invalidArg
                (nameof frame)
                (String.Concat(frame.Format.ToString().ToUpperInvariant(), " bytes must not be empty."))

        let signatureMatches =
            match frame.Format with
            | PngImage ->
                frame.Bytes.Length >= 8
                && frame.Bytes[0] = 0x89uy
                && frame.Bytes[1] = 0x50uy
                && frame.Bytes[2] = 0x4euy
                && frame.Bytes[3] = 0x47uy
                && frame.Bytes[4] = 0x0duy
                && frame.Bytes[5] = 0x0auy
                && frame.Bytes[6] = 0x1auy
                && frame.Bytes[7] = 0x0auy
            | JpegImage ->
                frame.Bytes.Length >= 3
                && frame.Bytes[0] = 0xffuy
                && frame.Bytes[1] = 0xd8uy
                && frame.Bytes[2] = 0xffuy

        if not signatureMatches then
            invalidArg
                (nameof frame)
                (String.Concat("Bytes do not contain the declared ", frame.Format.ToString(), " format."))

        let image = ImageResult.FromMemory(frame.Bytes, ColorComponents.RedGreenBlueAlpha)

        if image.Width <= 0 || image.Height <= 0 then
            invalidArg (nameof frame) "Compressed image dimensions must be positive."

        frame

    let validatePng (bytes: byte array) =
        validateImage { Format = PngImage; Bytes = bytes } |> ignore

        bytes

    let private decodeFrame (frame: CompressedFrame) =
        ArgumentNullException.ThrowIfNull frame.Bytes

        if frame.Bytes.Length = 0 then
            invalidArg (nameof frame) "Compressed image bytes must not be empty."

        let image = ImageResult.FromMemory(frame.Bytes, ColorComponents.RedGreenBlueAlpha)

        if
            image.Width <= 0
            || image.Width > WebPNative.MaximumDimension
            || image.Height <= 0
            || image.Height > WebPNative.MaximumDimension
        then
            invalidArg (nameof frame) "Animated WebP dimensions must be between 1 and 16383 pixels."

        { Width = image.Width
          Height = image.Height
          Rgba = image.Data }

    let decodeMeasured frame =
        let stopwatch = Stopwatch.StartNew()
        let decoded = decodeFrame frame
        stopwatch.Stop()
        decoded, stopwatch.Elapsed
