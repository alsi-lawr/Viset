namespace Viset.EndToEnd

open System
open System.Buffers.Binary
open System.IO
open FsUnit

type AnimationFrame =
    { Duration: int
      Width: int
      Height: int }

module MediaAssertions =
    let private uint24 (bytes: byte array) offset =
        int bytes[offset]
        ||| (int bytes[offset + 1] <<< 8)
        ||| (int bytes[offset + 2] <<< 16)

    let private require condition message =
        if not condition then
            invalidOp message

    let private animatedWebP (bytes: byte array) =
        require (bytes.Length >= 12) "WebP output is shorter than its container header."
        require (bytes[0..3] = [| 82uy; 73uy; 70uy; 70uy |]) "WebP output does not have a RIFF header."
        require (bytes[8..11] = [| 87uy; 69uy; 66uy; 80uy |]) "WebP output does not have a WEBP header."

        require
            (int (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4))) + 8 = bytes.Length)
            "WebP output has an invalid RIFF size."

        let mutable canvas = None
        let frames = ResizeArray<AnimationFrame>()
        let mutable offset = 12

        while offset + 8 <= bytes.Length do
            let identifier = bytes[offset .. offset + 3]

            let size =
                int (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4)))

            let start = offset + 8
            let finish = start + size
            require (finish <= bytes.Length) "WebP output contains a truncated chunk."

            if identifier = [| 86uy; 80uy; 56uy; 88uy |] then
                require (size = 10) "WebP output has an invalid VP8X chunk."
                canvas <- Some(uint24 bytes (start + 4) + 1, uint24 bytes (start + 7) + 1)
            elif identifier = [| 65uy; 78uy; 77uy; 70uy |] then
                require (size >= 16) "WebP output has an invalid ANMF chunk."

                frames.Add
                    { Duration = uint24 bytes (start + 12)
                      Width = uint24 bytes (start + 6) + 1
                      Height = uint24 bytes (start + 9) + 1 }

            offset <- finish + size % 2

        require (offset = bytes.Length) "WebP output has trailing or malformed chunks."

        canvas
        |> Option.defaultWith (fun () -> invalidOp "WebP output has no canvas dimensions."),
        frames |> Seq.toList

    let assertInventory root expected =
        let actual =
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            |> Seq.sort
            |> Seq.toList

        actual |> should equal (expected |> List.sort)

    let assertPng path width height =
        let bytes = File.ReadAllBytes path

        bytes[0..7]
        |> should equal [| 137uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy |]

        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) |> should equal width
        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) |> should equal height

    let assertWebP path width height expectedDuration minimumFrames maximumFrames =
        let canvas, frames = File.ReadAllBytes path |> animatedWebP
        canvas |> should equal (width, height)
        frames.Length |> should be (greaterThanOrEqualTo minimumFrames)
        frames |> List.forall (fun frame -> frame.Duration > 0) |> should equal true

        match expectedDuration with
        | Some duration -> frames |> List.sumBy _.Duration |> should equal duration
        | None -> ()

        match maximumFrames with
        | Some maximum -> frames.Length |> should be (lessThanOrEqualTo maximum)
        | None -> ()
