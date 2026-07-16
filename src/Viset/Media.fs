namespace Viset

open System
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open ImageMagick

type EncodedAnimation =
    { Bytes: byte array
      FrameTicksMs: int list }

    override animation.ToString() =
        String.Concat(animation.FrameTicksMs.Length.ToString(CultureInfo.InvariantCulture), " frames")

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private WebPConfig =
    val mutable Lossless: int
    val mutable Quality: float32
    val mutable Method: int
    val mutable ImageHint: int
    val mutable TargetSize: int
    val mutable TargetPsnr: float32
    val mutable Segments: int
    val mutable SnsStrength: int
    val mutable FilterStrength: int
    val mutable FilterSharpness: int
    val mutable FilterType: int
    val mutable AutoFilter: int
    val mutable AlphaCompression: int
    val mutable AlphaFiltering: int
    val mutable AlphaQuality: int
    val mutable Pass: int
    val mutable ShowCompressed: int
    val mutable Preprocessing: int
    val mutable Partitions: int
    val mutable PartitionLimit: int
    val mutable EmulateJpegSize: int
    val mutable ThreadLevel: int
    val mutable LowMemory: int
    val mutable NearLossless: int
    val mutable Exact: int
    val mutable UseDeltaPalette: int
    val mutable UseSharpYuv: int
    val mutable MinimumQuality: int
    val mutable MaximumQuality: int

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private WebPPicture =
    val mutable UseArgb: int
    val mutable Colorspace: int
    val mutable Width: int
    val mutable Height: int
    val mutable Y: nativeint
    val mutable U: nativeint
    val mutable V: nativeint
    val mutable YStride: int
    val mutable UvStride: int
    val mutable A: nativeint
    val mutable AStride: int
    val mutable Padding1A: uint32
    val mutable Padding1B: uint32
    val mutable Argb: nativeint
    val mutable ArgbStride: int
    val mutable Padding2A: uint32
    val mutable Padding2B: uint32
    val mutable Padding2C: uint32
    val mutable Writer: nativeint
    val mutable CustomPointer: nativeint
    val mutable ExtraInfoType: int
    val mutable ExtraInfo: nativeint
    val mutable Statistics: nativeint
    val mutable ErrorCode: int
    val mutable ProgressHook: nativeint
    val mutable UserData: nativeint
    val mutable Padding3A: uint32
    val mutable Padding3B: uint32
    val mutable Padding3C: uint32
    val mutable Padding4: nativeint
    val mutable Padding5: nativeint
    val mutable Padding6A: uint32
    val mutable Padding6B: uint32
    val mutable Padding6C: uint32
    val mutable Padding6D: uint32
    val mutable Padding6E: uint32
    val mutable Padding6F: uint32
    val mutable Padding6G: uint32
    val mutable Padding6H: uint32
    val mutable Memory: nativeint
    val mutable MemoryArgb: nativeint
    val mutable Padding7A: nativeint
    val mutable Padding7B: nativeint

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private WebPMuxAnimationParameters =
    val mutable BackgroundColor: uint32
    val mutable LoopCount: int

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private WebPData =
    val mutable Bytes: nativeint
    val mutable Size: unativeint

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private WebPMuxFrameInfo =
    val mutable Bitstream: WebPData
    val mutable XOffset: int
    val mutable YOffset: int
    val mutable Duration: int
    val mutable ChunkId: int
    val mutable DisposeMethod: int
    val mutable BlendMethod: int
    val mutable Padding: uint32

module private WebPNative =
    [<Literal>]
    let private EncoderAbiVersion = 0x0210

    [<Literal>]
    let private MuxAbiVersion = 0x0109

    [<Literal>]
    let private MuxSuccess = 1

    [<Literal>]
    let private AnimationFrameChunk = 3

    [<Literal>]
    let private DisposeToBackground = 1

    [<Literal>]
    let private NoBlend = 1

    [<Literal>]
    let private CopyData = 1

    [<Literal>]
    let MaximumDimension = 16383

    [<DllImport("libwebp", EntryPoint = "WebPConfigInitInternal", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPConfigInitInternal(WebPConfig& config, int preset, float32 quality, int abiVersion)

    [<DllImport("libwebp", EntryPoint = "WebPValidateConfig", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPValidateConfig(WebPConfig& config)

    [<DllImport("libwebp", EntryPoint = "WebPPictureInitInternal", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPPictureInitInternal(WebPPicture& picture, int abiVersion)

    [<DllImport("libwebp", EntryPoint = "WebPPictureImportRGBA", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPPictureImportRgba(WebPPicture& picture, nativeint rgba, int stride)

    [<DllImport("libwebp", EntryPoint = "WebPPictureFree", CallingConvention = CallingConvention.Cdecl)>]
    extern void private WebPPictureFree(WebPPicture& picture)

    [<DllImport("libwebp", EntryPoint = "WebPMemoryWriterInit", CallingConvention = CallingConvention.Cdecl)>]
    extern void private WebPMemoryWriterInit(nativeint writer)

    [<DllImport("libwebp", EntryPoint = "WebPMemoryWriterClear", CallingConvention = CallingConvention.Cdecl)>]
    extern void private WebPMemoryWriterClear(nativeint writer)

    [<DllImport("libwebp", EntryPoint = "WebPEncode", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPEncode(WebPConfig& config, WebPPicture& picture)

    [<DllImport("libwebp", EntryPoint = "WebPFree", CallingConvention = CallingConvention.Cdecl)>]
    extern void private WebPFree(nativeint pointer)

    [<DllImport("libwebpmux", EntryPoint = "WebPNewInternal", CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint private WebPMuxNewInternal(int abiVersion)

    [<DllImport("libwebpmux", EntryPoint = "WebPMuxSetCanvasSize", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPMuxSetCanvasSize(nativeint mux, int width, int height)

    [<DllImport("libwebpmux", EntryPoint = "WebPMuxSetAnimationParams", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPMuxSetAnimationParameters(nativeint mux, WebPMuxAnimationParameters& parameters)

    [<DllImport("libwebpmux", EntryPoint = "WebPMuxPushFrame", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPMuxPushFrame(nativeint mux, WebPMuxFrameInfo& frame, int copyData)

    [<DllImport("libwebpmux", EntryPoint = "WebPMuxAssemble", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPMuxAssemble(nativeint mux, WebPData& data)

    [<DllImport("libwebpmux", EntryPoint = "WebPMuxDelete", CallingConvention = CallingConvention.Cdecl)>]
    extern void private WebPMuxDelete(nativeint mux)

    type Encoder =
        private
            { Handle: nativeint
              Width: int
              Height: int
              Config: WebPConfig
              MemoryWriter: nativeint }

    type private NativeLibraries =
        { SharpYuv: nativeint
          WebP: nativeint
          WebPMux: nativeint }

    let private nativeFileName library =
        if OperatingSystem.IsWindows() then
            String.Concat(library, ".dll")
        elif OperatingSystem.IsMacOS() then
            String.Concat(library, ".dylib")
        else
            String.Concat(library, ".so")

    let private loadNativeLibrary library =
        let fileName = nativeFileName library

        let candidates =
            [ Path.Combine(AppContext.BaseDirectory, fileName)
              Path.Combine(
                  AppContext.BaseDirectory,
                  "runtimes",
                  RuntimeInformation.RuntimeIdentifier,
                  "native",
                  fileName
              ) ]

        match candidates |> List.tryFind File.Exists with
        | Some path -> NativeLibrary.Load path
        | None ->
            invalidOp (
                String.Concat(
                    "The packaged libwebp sidecar '",
                    fileName,
                    "' was not found. Looked in: ",
                    String.Join(", ", candidates)
                )
            )

    let private nativeLibraries =
        lazy
            (let libraries =
                { SharpYuv = loadNativeLibrary "libsharpyuv"
                  WebP = loadNativeLibrary "libwebp"
                  WebPMux = loadNativeLibrary "libwebpmux" }

             let resolver =
                 DllImportResolver(fun libraryName _ _ ->
                     match libraryName with
                     | "libwebp" -> libraries.WebP
                     | "libwebpmux" -> libraries.WebPMux
                     | _ -> 0n)

             NativeLibrary.SetDllImportResolver(typeof<EncodedAnimation>.Assembly, resolver)
             libraries)

    let private ensureNativeLibraries () = nativeLibraries.Value |> ignore

    let private validateLayouts () =
        let expectedPictureSize = if IntPtr.Size = 8 then 256 else 172
        let expectedFrameInfoSize = if IntPtr.Size = 8 then 48 else 36

        let layouts =
            [ "WebPConfig", Marshal.SizeOf<WebPConfig>(), 116
              "WebPPicture", Marshal.SizeOf<WebPPicture>(), expectedPictureSize
              "WebPData", Marshal.SizeOf<WebPData>(), IntPtr.Size * 2
              "WebPMuxFrameInfo", Marshal.SizeOf<WebPMuxFrameInfo>(), expectedFrameInfoSize ]

        layouts
        |> List.iter (fun (name, actual, expected) ->
            if actual <> expected then
                invalidOp (
                    String.Format(
                        CultureInfo.InvariantCulture,
                        "The {0} interop layout is {1} bytes; libwebp requires {2} bytes.",
                        name,
                        actual,
                        expected
                    )
                ))

    let private checkMux operation result =
        if result <> MuxSuccess then
            invalidOp (
                String.Format(CultureInfo.InvariantCulture, "{0} failed with libwebpmux result {1}.", operation, result)
            )

    let private readWriterSize writer =
        if IntPtr.Size = 8 then
            Marshal.ReadInt64(writer, IntPtr.Size) |> uint64
        else
            Marshal.ReadInt32(writer, IntPtr.Size) |> uint32 |> uint64

    let private copyEncodedFrame writer =
        let pointer = Marshal.ReadIntPtr writer
        let size = readWriterSize writer

        if pointer = 0n || size = 0UL then
            invalidOp "libwebp encoded an empty animation frame."

        if size > uint64 Int32.MaxValue then
            invalidOp "A WebP frame exceeds Viset's managed output size limit."

        let output = Array.zeroCreate<byte> (int size)
        Marshal.Copy(pointer, output, 0, output.Length)
        output

    let create width height =
        if
            width <= 0
            || width > MaximumDimension
            || height <= 0
            || height > MaximumDimension
        then
            invalidArg (nameof width) "Animated WebP dimensions must be between 1 and 16383 pixels."

        ensureNativeLibraries ()
        validateLayouts ()

        let mutable config = Unchecked.defaultof<WebPConfig>

        if WebPConfigInitInternal(&config, 0, 75.0f, EncoderAbiVersion) = 0 then
            invalidOp "libwebp rejected the encoder ABI version."

        config.Lossless <- 1
        config.Quality <- 75.0f
        config.Method <- 6
        config.AlphaQuality <- 100
        config.Exact <- 1

        if WebPValidateConfig(&config) = 0 then
            invalidOp "libwebp rejected Viset's lossless encoder configuration."

        let handle = WebPMuxNewInternal MuxAbiVersion

        if handle = 0n then
            invalidOp "libwebpmux could not create an animation muxer."

        try
            WebPMuxSetCanvasSize(handle, width, height)
            |> checkMux "Setting the animated WebP canvas"

            let mutable parameters = Unchecked.defaultof<WebPMuxAnimationParameters>
            parameters.LoopCount <- 0
            parameters.BackgroundColor <- 0u

            WebPMuxSetAnimationParameters(handle, &parameters)
            |> checkMux "Setting the animated WebP parameters"

            { Handle = handle
              Width = width
              Height = height
              Config = config
              MemoryWriter = NativeLibrary.GetExport(nativeLibraries.Value.WebP, "WebPMemoryWrite") }
        with _ ->
            WebPMuxDelete handle
            reraise ()

    let private encodeFrame encoder (rgba: byte array) =
        ArgumentNullException.ThrowIfNull rgba

        let expectedLength = int64 encoder.Width * int64 encoder.Height * 4L

        if rgba.LongLength <> expectedLength then
            invalidArg (nameof rgba) "RGBA frame data does not match the animation dimensions."

        let mutable picture = Unchecked.defaultof<WebPPicture>

        if WebPPictureInitInternal(&picture, EncoderAbiVersion) = 0 then
            invalidOp "libwebp rejected the picture ABI version."

        picture.Width <- encoder.Width
        picture.Height <- encoder.Height
        picture.UseArgb <- 1

        try
            let writer = Marshal.AllocHGlobal(if IntPtr.Size = 8 then 32 else 16)
            let mutable writerInitialized = false

            try
                WebPMemoryWriterInit writer
                writerInitialized <- true
                picture.Writer <- encoder.MemoryWriter
                picture.CustomPointer <- writer

                let pixels = GCHandle.Alloc(rgba, GCHandleType.Pinned)

                try
                    if WebPPictureImportRgba(&picture, pixels.AddrOfPinnedObject(), encoder.Width * 4) = 0 then
                        invalidOp "libwebp could not import an RGBA animation frame."

                    let mutable config = encoder.Config

                    if WebPEncode(&config, &picture) = 0 then
                        invalidOp (
                            String.Format(
                                CultureInfo.InvariantCulture,
                                "libwebp could not encode an animation frame (error {0}).",
                                picture.ErrorCode
                            )
                        )

                    copyEncodedFrame writer
                finally
                    pixels.Free()
            finally
                if writerInitialized then
                    WebPMemoryWriterClear writer

                Marshal.FreeHGlobal writer
        finally
            WebPPictureFree(&picture)

    let addFrame encoder duration (rgba: byte array) =
        let encoded = encodeFrame encoder rgba
        let bytes = GCHandle.Alloc(encoded, GCHandleType.Pinned)

        try
            let mutable frame = Unchecked.defaultof<WebPMuxFrameInfo>
            frame.Bitstream.Bytes <- bytes.AddrOfPinnedObject()
            frame.Bitstream.Size <- unativeint encoded.Length
            frame.Duration <- duration
            frame.ChunkId <- AnimationFrameChunk
            frame.DisposeMethod <- DisposeToBackground
            frame.BlendMethod <- NoBlend

            WebPMuxPushFrame(encoder.Handle, &frame, CopyData)
            |> checkMux "Adding an animated WebP frame"
        finally
            bytes.Free()

    let assemble encoder =
        let mutable data = Unchecked.defaultof<WebPData>

        try
            WebPMuxAssemble(encoder.Handle, &data)
            |> checkMux "Assembling the animated WebP"

            let size = uint64 data.Size

            if size = 0UL then
                invalidOp "libwebp assembled an empty animation."

            if size > uint64 Int32.MaxValue then
                invalidOp "Animated WebP output exceeds Viset's managed output size limit."

            let output = Array.zeroCreate<byte> (int size)
            Marshal.Copy(data.Bytes, output, 0, output.Length)
            output
        finally
            if data.Bytes <> 0n then
                WebPFree data.Bytes

    let dispose encoder = WebPMuxDelete encoder.Handle

    let dimensions encoder = encoder.Width, encoder.Height

module Media =
    type private DecodedPng =
        { Width: int
          Height: int
          Rgba: byte array }

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

    let private decodePng (bytes: byte array) =
        ArgumentNullException.ThrowIfNull bytes

        if bytes.Length = 0 then
            invalidArg (nameof bytes) "PNG bytes must not be empty."

        use image = new MagickImage(bytes, MagickFormat.Png)

        if image.Width = 0u || image.Height = 0u then
            invalidArg (nameof bytes) "PNG dimensions must be positive."

        if
            image.Width > uint WebPNative.MaximumDimension
            || image.Height > uint WebPNative.MaximumDimension
        then
            invalidArg (nameof bytes) "Animated WebP dimensions must be between 1 and 16383 pixels."

        image.Strip()
        image.Alpha AlphaOption.On
        use pixels = image.GetPixels()

        let rgba =
            pixels.ToByteArray(PixelMapping.RGBA)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "ImageMagick returned no RGBA frame data.")

        { Width = int image.Width
          Height = int image.Height
          Rgba = rgba }

    let encodeAnimatedWebP framesPerSecond (frames: byte array list) =
        if List.isEmpty frames then
            invalidArg (nameof frames) "Animated WebP requires at least one frame."

        let ticks = frameTicksMilliseconds framesPerSecond frames.Length
        let mutable encoder = None

        try
            (frames, ticks)
            ||> List.iter2 (fun bytes tick ->
                let frame = decodePng bytes

                let activeEncoder =
                    match encoder with
                    | None ->
                        let created = WebPNative.create frame.Width frame.Height
                        encoder <- Some created
                        created
                    | Some created when WebPNative.dimensions created = (frame.Width, frame.Height) -> created
                    | Some _ -> invalidArg (nameof frames) "Animated WebP frames must have identical dimensions."

                WebPNative.addFrame activeEncoder tick frame.Rgba)

            let activeEncoder =
                encoder
                |> Option.defaultWith (fun () -> invalidOp "Animation encoder was not created.")

            { Bytes = WebPNative.assemble activeEncoder
              FrameTicksMs = ticks }
        finally
            encoder |> Option.iter WebPNative.dispose
