namespace Viset

open System
open System.Globalization
open System.IO
open System.Runtime.InteropServices

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
type private WebPAnimEncoderOptions =
    val mutable AnimationParameters: WebPMuxAnimationParameters
    val mutable MinimizeSize: int
    val mutable KeyFrameMinimum: int
    val mutable KeyFrameMaximum: int
    val mutable AllowMixed: int
    val mutable Verbose: int
    val mutable PaddingA: uint32
    val mutable PaddingB: uint32
    val mutable PaddingC: uint32
    val mutable PaddingD: uint32

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

module internal WebPNative =
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

    [<DllImport("libwebpmux",
                EntryPoint = "WebPAnimEncoderOptionsInitInternal",
                CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPAnimEncoderOptionsInitInternal(WebPAnimEncoderOptions& options, int abiVersion)

    [<DllImport("libwebpmux", EntryPoint = "WebPAnimEncoderNewInternal", CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint private WebPAnimEncoderNewInternal(
        int width,
        int height,
        WebPAnimEncoderOptions& options,
        int abiVersion
    )

    [<DllImport("libwebpmux", EntryPoint = "WebPAnimEncoderAdd", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPAnimEncoderAdd(
        nativeint encoder,
        WebPPicture& picture,
        int timestampMilliseconds,
        WebPConfig& config
    )

    [<DllImport("libwebpmux", EntryPoint = "WebPAnimEncoderAdd", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPAnimEncoderFinish(
        nativeint encoder,
        nativeint picture,
        int timestampMilliseconds,
        nativeint config
    )

    [<DllImport("libwebpmux", EntryPoint = "WebPAnimEncoderAssemble", CallingConvention = CallingConvention.Cdecl)>]
    extern int private WebPAnimEncoderAssemble(nativeint encoder, WebPData& data)

    [<DllImport("libwebpmux", EntryPoint = "WebPAnimEncoderGetError", CallingConvention = CallingConvention.Cdecl)>]
    extern nativeint private WebPAnimEncoderGetError(nativeint encoder)

    [<DllImport("libwebpmux", EntryPoint = "WebPAnimEncoderDelete", CallingConvention = CallingConvention.Cdecl)>]
    extern void private WebPAnimEncoderDelete(nativeint encoder)

    type FullEncoder =
        private
            { Handle: nativeint
              Width: int
              Height: int
              Config: WebPConfig
              MemoryWriter: nativeint }

    type AnimationEncoder =
        private
            { Handle: nativeint
              Width: int
              Height: int
              Config: WebPConfig }

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
              "WebPMuxFrameInfo", Marshal.SizeOf<WebPMuxFrameInfo>(), expectedFrameInfoSize
              "WebPAnimEncoderOptions", Marshal.SizeOf<WebPAnimEncoderOptions>(), 44 ]

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

    let private validateDimensions width height =
        if
            width <= 0
            || width > MaximumDimension
            || height <= 0
            || height > MaximumDimension
        then
            invalidArg (nameof width) "Animated WebP dimensions must be between 1 and 16383 pixels."

    let private createConfig (options: WebPOptions) =
        let mutable config = Unchecked.defaultof<WebPConfig>

        ensureNativeLibraries ()
        validateLayouts ()

        if WebPConfigInitInternal(&config, 0, 75.0f, EncoderAbiVersion) = 0 then
            invalidOp "libwebp rejected the encoder ABI version."

        config.Lossless <-
            match options.Mode with
            | Lossy _ -> 0
            | Lossless _ -> 1

        config.Quality <- float32 options.Mode.Quality
        config.Method <- options.Method
        config.AlphaQuality <- 100
        config.Exact <- 1
        config.ThreadLevel <- 0

        if WebPValidateConfig(&config) = 0 then
            invalidOp "libwebp rejected Viset's encoder configuration."

        config

    let createFull (options: WebPOptions) width height =
        validateDimensions width height
        let config = createConfig options

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

    let encodeFullFrame (encoder: FullEncoder) (rgba: byte array) =
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

    let addFullFrame (encoder: FullEncoder) duration (encoded: byte array) =
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

    let private copyData (operation: string) (data: WebPData) =
        let size = uint64 data.Size

        if size = 0UL then
            invalidOp (String.Concat(operation, " returned empty output."))

        if size > uint64 Int32.MaxValue then
            invalidOp "Animated WebP output exceeds Viset's managed output size limit."

        let output = Array.zeroCreate<byte> (int size)
        Marshal.Copy(data.Bytes, output, 0, output.Length)
        output

    let assembleFull (encoder: FullEncoder) =
        let mutable data = Unchecked.defaultof<WebPData>

        try
            WebPMuxAssemble(encoder.Handle, &data)
            |> checkMux "Assembling the animated WebP"

            copyData "libwebpmux" data
        finally
            if data.Bytes <> 0n then
                WebPFree data.Bytes

    let disposeFull (encoder: FullEncoder) = WebPMuxDelete encoder.Handle

    let fullDimensions (encoder: FullEncoder) = encoder.Width, encoder.Height

    let private animationError encoder operation =
        let pointer = WebPAnimEncoderGetError encoder

        let reason =
            if pointer = 0n then
                "unknown error"
            else
                Marshal.PtrToStringUTF8 pointer
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.defaultValue "unknown error"

        invalidOp (String.Concat(operation, " failed: ", reason))

    let createAnimation (options: WebPOptions) width height =
        validateDimensions width height
        let config = createConfig options
        let mutable encoderOptions = Unchecked.defaultof<WebPAnimEncoderOptions>

        if WebPAnimEncoderOptionsInitInternal(&encoderOptions, MuxAbiVersion) = 0 then
            invalidOp "libwebp_anim rejected the mux ABI version."

        encoderOptions.AnimationParameters.LoopCount <- 0
        encoderOptions.AnimationParameters.BackgroundColor <- 0u

        let handle =
            WebPAnimEncoderNewInternal(width, height, &encoderOptions, MuxAbiVersion)

        if handle = 0n then
            invalidOp "libwebp_anim could not create an animation encoder."

        { Handle = handle
          Width = width
          Height = height
          Config = config }

    let addAnimationFrame (encoder: AnimationEncoder) timestampMilliseconds (rgba: byte array) =
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
            let pixels = GCHandle.Alloc(rgba, GCHandleType.Pinned)

            try
                if WebPPictureImportRgba(&picture, pixels.AddrOfPinnedObject(), encoder.Width * 4) = 0 then
                    invalidOp "libwebp could not import an RGBA animation frame."

                let mutable config = encoder.Config

                if WebPAnimEncoderAdd(encoder.Handle, &picture, timestampMilliseconds, &config) = 0 then
                    animationError encoder.Handle "Adding a libwebp_anim frame"
            finally
                pixels.Free()
        finally
            WebPPictureFree(&picture)

    let assembleAnimation (encoder: AnimationEncoder) finalTimestampMilliseconds =
        if WebPAnimEncoderFinish(encoder.Handle, 0n, finalTimestampMilliseconds, 0n) = 0 then
            animationError encoder.Handle "Finalizing libwebp_anim timestamps"

        let mutable data = Unchecked.defaultof<WebPData>

        try
            if WebPAnimEncoderAssemble(encoder.Handle, &data) = 0 then
                animationError encoder.Handle "Assembling libwebp_anim output"

            copyData "libwebp_anim" data
        finally
            if data.Bytes <> 0n then
                WebPFree data.Bytes

    let disposeAnimation (encoder: AnimationEncoder) = WebPAnimEncoderDelete encoder.Handle

    let animationDimensions (encoder: AnimationEncoder) = encoder.Width, encoder.Height
