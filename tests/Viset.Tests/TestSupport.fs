namespace Viset.Tests

open System
open System.Buffers.Binary
open System.IO
open System.Text
open FsUnit

module TestSupport =
    let resultValue result =
        match result with
        | Ok value -> value
        | Error message -> failwithf "Expected success, got: %s" message

    let resultError (result: Result<'value, string>) =
        match result with
        | Ok _ -> failwith "Expected failure, got success."
        | Error message -> message

    let shouldFailWithMessage expectedMessage (action: unit -> unit) =
        let actualMessage =
            try
                action ()
                None
            with :? InvalidOperationException as error ->
                Some error.Message

        actualMessage |> should equal (Some expectedMessage)

    let writeUInt32LittleEndian (bytes: byte array) offset value =
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, sizeof<uint32>), value)

    let writeUInt32LittleEndianTo (writer: BinaryWriter) value =
        let bytes = Array.zeroCreate<byte> sizeof<uint32>
        writeUInt32LittleEndian bytes 0 value
        writer.Write bytes

    let animationFrame duration =
        let data = Array.zeroCreate<byte> 16
        data[12] <- byte duration
        data[13] <- byte (duration >>> 8)
        data[14] <- byte (duration >>> 16)
        Encoding.ASCII.GetBytes("ANMF"), data

    let animatedWebP () =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.ASCII, true)
        writer.Write(Encoding.ASCII.GetBytes("RIFF"))
        writeUInt32LittleEndianTo writer 0u
        writer.Write(Encoding.ASCII.GetBytes("WEBP"))

        for tag, data in [ animationFrame 10; animationFrame 20 ] do
            writer.Write tag
            writeUInt32LittleEndianTo writer (uint32 data.Length)
            writer.Write data

        writer.Flush()
        let bytes = stream.ToArray()
        writeUInt32LittleEndian bytes 4 (uint32 (bytes.Length - 8))
        bytes

    let webPWithoutImageFrame () =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.ASCII, true)
        writer.Write(Encoding.ASCII.GetBytes("RIFF"))
        writeUInt32LittleEndianTo writer 4u
        writer.Write(Encoding.ASCII.GetBytes("WEBP"))
        writer.Flush()
        stream.ToArray()
