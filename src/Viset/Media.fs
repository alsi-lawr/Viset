namespace Viset

open System
open System.Buffers.Binary
open System.Globalization
open System.Text
open ImageMagick
open ImageMagick.Formats

type EncodedAnimation =
    { Bytes: byte array
      FrameTicksMs: int list }

    override animation.ToString() =
        String.Concat(animation.FrameTicksMs.Length.ToString(CultureInfo.InvariantCulture), " frames")

module Media =
    let private webpMaximumDimension = 1 <<< 24
    let private riffIdentifier = [| 0x52uy; 0x49uy; 0x46uy; 0x46uy |]
    let private webpIdentifier = [| 0x57uy; 0x45uy; 0x42uy; 0x50uy |]
    let private webpAlphaIdentifier = [| 0x41uy; 0x4cuy; 0x50uy; 0x48uy |]
    let private webpLossyIdentifier = [| 0x56uy; 0x50uy; 0x38uy; 0x20uy |]
    let private webpLosslessIdentifier = [| 0x56uy; 0x50uy; 0x38uy; 0x4cuy |]

    let private appendUInt24 (destination: ResizeArray<byte>) value =
        if value < 0 || value >= webpMaximumDimension then
            invalidArg (nameof value) "WebP 24-bit values must be between 0 and 16777215."

        destination.Add(byte value)
        destination.Add(byte (value >>> 8))
        destination.Add(byte (value >>> 16))

    let private appendUInt32 (destination: ResizeArray<byte>) (value: uint32) =
        destination.Add(byte value)
        destination.Add(byte (value >>> 8))
        destination.Add(byte (value >>> 16))
        destination.Add(byte (value >>> 24))

    let private appendAscii (destination: ResizeArray<byte>) (value: string) =
        let bytes = Encoding.ASCII.GetBytes value

        if bytes.Length <> 4 then
            invalidArg (nameof value) "WebP chunk identifiers must contain four ASCII bytes."

        destination.AddRange bytes

    let private appendChunk (destination: ResizeArray<byte>) identifier (payload: byte array) =
        appendAscii destination identifier
        appendUInt32 destination (uint32 payload.Length)
        destination.AddRange payload

        if payload.Length % 2 <> 0 then
            destination.Add 0uy

    let private extractWebPImageChunks (bytes: byte array) =
        if
            bytes.Length < 20
            || not (bytes.AsSpan(0, 4).SequenceEqual riffIdentifier)
            || not (bytes.AsSpan(8, 4).SequenceEqual webpIdentifier)
            || uint64 (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4))) + 8UL
               <> uint64 bytes.Length
        then
            invalidOp "ImageMagick returned an invalid standalone WebP frame."

        let chunks = ResizeArray<byte>()
        let mutable offset = 12
        let mutable foundBitstream = false

        while offset + 8 <= bytes.Length do
            let identifier = bytes.AsSpan(offset, 4)

            let length =
                uint64 (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4)))

            let paddedLength = length + length % 2UL
            let nextOffset = uint64 offset + 8UL + paddedLength

            if nextOffset > uint64 bytes.Length || paddedLength > uint64 Int32.MaxValue then
                invalidOp "ImageMagick returned a malformed standalone WebP frame."

            let chunkLength = int paddedLength

            let isAlpha = identifier.SequenceEqual webpAlphaIdentifier
            let isLossy = identifier.SequenceEqual webpLossyIdentifier
            let isLossless = identifier.SequenceEqual webpLosslessIdentifier

            if isAlpha || isLossy || isLossless then
                chunks.AddRange(bytes.AsSpan(offset, 8 + chunkLength).ToArray())
                foundBitstream <- foundBitstream || isLossy || isLossless

            offset <- int nextOffset

        if offset <> bytes.Length then
            invalidOp "ImageMagick returned a malformed standalone WebP frame."

        if not foundBitstream then
            invalidOp "ImageMagick returned a standalone WebP frame without an image bitstream."

        chunks.ToArray()

    let private encodeStandaloneWebP (defines: WebPWriteDefines) (bytes: byte array) =
        use image = new MagickImage(bytes, MagickFormat.Png)
        image.Strip()
        image.Alpha AlphaOption.On
        image.Format <- MagickFormat.WebP
        image.ToByteArray defines |> extractWebPImageChunks

    let private muxAnimatedWebP width height ticks frameChunks =
        if
            width <= 0
            || width > webpMaximumDimension
            || height <= 0
            || height > webpMaximumDimension
        then
            invalidArg (nameof width) "Animated WebP dimensions must be between 1 and 16777216."

        let body = ResizeArray<byte>()
        let extendedHeader = ResizeArray<byte>()
        extendedHeader.Add 0x12uy
        extendedHeader.AddRange(Array.zeroCreate 3)
        appendUInt24 extendedHeader (width - 1)
        appendUInt24 extendedHeader (height - 1)
        appendChunk body "VP8X" (extendedHeader.ToArray())

        let animationHeader = Array.zeroCreate<byte> 6
        appendChunk body "ANIM" animationHeader

        List.zip ticks frameChunks
        |> List.iter (fun (delay, chunks) ->
            let frame = ResizeArray<byte>()
            appendUInt24 frame 0
            appendUInt24 frame 0
            appendUInt24 frame (width - 1)
            appendUInt24 frame (height - 1)
            appendUInt24 frame delay
            frame.Add 0x03uy
            frame.AddRange chunks
            appendChunk body "ANMF" (frame.ToArray()))

        let riffLength = int64 body.Count + 4L

        if riffLength > int64 UInt32.MaxValue then
            invalidOp "Animated WebP output exceeds the RIFF size limit."

        let output = ResizeArray<byte>()
        appendAscii output "RIFF"
        appendUInt32 output (uint32 riffLength)
        appendAscii output "WEBP"
        output.AddRange body
        output.ToArray()

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
        let mutable expectedWidth = None
        let mutable expectedHeight = None
        let defines = WebPWriteDefines()
        defines.Lossless <- Nullable true
        defines.Exact <- Nullable true
        defines.AlphaQuality <- Nullable 100
        defines.Method <- Nullable 6

        let encodedFrames =
            frames
            |> List.map (fun bytes ->
                validatePng bytes |> ignore
                use image = new MagickImage(bytes, MagickFormat.Png)

                match expectedWidth, expectedHeight with
                | None, None ->
                    expectedWidth <- Some image.Width
                    expectedHeight <- Some image.Height
                | Some width, Some height when image.Width = width && image.Height = height -> ()
                | _ -> invalidArg (nameof frames) "Animated WebP frames must have identical dimensions."

                encodeStandaloneWebP defines bytes)

        let width =
            expectedWidth
            |> Option.defaultWith (fun () -> invalidOp "Animation width was not determined.")

        let height =
            expectedHeight
            |> Option.defaultWith (fun () -> invalidOp "Animation height was not determined.")

        { Bytes = muxAnimatedWebP (int width) (int height) ticks encodedFrames
          FrameTicksMs = ticks }
