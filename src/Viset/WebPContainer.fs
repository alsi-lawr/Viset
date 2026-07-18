namespace Viset

open System
open System.Buffers.Binary

type internal WebPChunkKind =
    | AnimationFrame
    | StillImage
    | Other

type internal WebPChunk =
    { Kind: WebPChunkKind
      Offset: int
      DataOffset: int
      DataSize: int
      AnimationDuration: int option
      AnimationDurationOffset: int option }

type internal WebPContainer = private { Chunks: WebPChunk list }

type internal WebPDurationPatch = { Offset: int; Duration: int }

module internal WebPContainer =
    let private chunkIs (bytes: byte array) offset a b c d =
        bytes[offset] = a
        && bytes[offset + 1] = b
        && bytes[offset + 2] = c
        && bytes[offset + 3] = d

    let private readDuration (bytes: byte array) offset =
        int bytes[offset]
        ||| (int bytes[offset + 1] <<< 8)
        ||| (int bytes[offset + 2] <<< 16)

    let parse (bytes: byte array) =
        ArgumentNullException.ThrowIfNull bytes

        if
            bytes.Length < 12
            || not (chunkIs bytes 0 0x52uy 0x49uy 0x46uy 0x46uy)
            || not (chunkIs bytes 8 0x57uy 0x45uy 0x42uy 0x50uy)
        then
            invalidOp "An encoder returned an invalid WebP container."

        let declaredLength =
            uint64 (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, sizeof<uint32>)))
            + 8UL

        if declaredLength <> uint64 bytes.LongLength then
            invalidOp "An encoder returned a WebP container with an invalid RIFF size."

        let rec readChunks offset reversed =
            if offset = bytes.Length then
                { Chunks = List.rev reversed }
            elif offset + 8 > bytes.Length then
                invalidOp "An encoder returned a malformed WebP container."
            else
                let dataSize64 =
                    uint64 (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, sizeof<uint32>)))

                let dataOffset64 = uint64 offset + 8UL
                let nextOffset64 = dataOffset64 + dataSize64 + dataSize64 % 2UL

                if nextOffset64 > uint64 bytes.LongLength then
                    invalidOp "An encoder returned a truncated WebP chunk."

                if dataSize64 > uint64 Int32.MaxValue then
                    invalidOp "An encoder returned a WebP chunk that exceeds Viset's managed size limit."

                let dataOffset = int dataOffset64
                let dataSize = int dataSize64

                let kind, duration, durationOffset =
                    if chunkIs bytes offset 0x41uy 0x4euy 0x4duy 0x46uy then
                        if dataSize < 16 then
                            invalidOp "An encoder returned a truncated animated WebP frame header."

                        let valueOffset = dataOffset + 12
                        AnimationFrame, Some(readDuration bytes valueOffset), Some valueOffset
                    elif
                        chunkIs bytes offset 0x56uy 0x50uy 0x38uy 0x20uy
                        || chunkIs bytes offset 0x56uy 0x50uy 0x38uy 0x4cuy
                    then
                        StillImage, None, None
                    else
                        Other, None, None

                let chunk =
                    { Kind = kind
                      Offset = offset
                      DataOffset = dataOffset
                      DataSize = dataSize
                      AnimationDuration = duration
                      AnimationDurationOffset = durationOffset }

                readChunks (int nextOffset64) (chunk :: reversed)

        readChunks 12 []

    let frameCount container =
        let animationFrames =
            container.Chunks
            |> List.sumBy (fun chunk -> if chunk.Kind = AnimationFrame then 1 else 0)

        if animationFrames > 0 then
            animationFrames
        elif container.Chunks |> List.exists (fun chunk -> chunk.Kind = StillImage) then
            1
        else
            invalidOp "An encoder returned a WebP container without an image frame."

    let durationPatch maximumDuration expectedDuration container =
        let frames =
            container.Chunks
            |> List.choose (fun chunk ->
                match chunk.Kind, chunk.AnimationDuration, chunk.AnimationDurationOffset with
                | AnimationFrame, Some duration, Some offset -> Some(duration, offset)
                | _ -> None)

        match List.tryLast frames with
        | None -> None
        | Some(currentDuration, durationOffset) ->
            let actualDuration = frames |> List.sumBy fst
            let adjusted = currentDuration + expectedDuration - actualDuration

            if adjusted <= 0 || adjusted > maximumDuration then
                invalidOp "FFmpeg produced a WebP timeline that Viset could not normalize without losing duration."

            Some
                { Offset = durationOffset
                  Duration = adjusted }
