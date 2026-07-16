namespace Viset

open System
open System.Diagnostics
open System.Globalization

[<DebuggerDisplay("TomlValue")>]
type TomlValue =
    | String of string
    | Integer of int64
    | Float of double
    | Boolean of bool
    | DateTime of string
    | Array of TomlValue list
    | Table of (string * TomlValue) list

    override value.ToString() =
        match value with
        | String text -> text
        | Integer number -> number.ToString(CultureInfo.InvariantCulture)
        | Float number -> number.ToString("R", CultureInfo.InvariantCulture)
        | Boolean flag -> if flag then "true" else "false"
        | DateTime text -> text
        | Array _ -> "array"
        | Table _ -> "table"

type Dimensions =
    { Width: int
      Height: int }

    override dimensions.ToString() =
        String.Concat(
            dimensions.Width.ToString(CultureInfo.InvariantCulture),
            "x",
            dimensions.Height.ToString(CultureInfo.InvariantCulture)
        )

type Device =
    { Name: string
      Mobile: bool
      Touch: bool
      DeviceScale: double
      Viewport: Dimensions
      Frame: Dimensions option }

    override device.ToString() = device.Name

[<DebuggerDisplay("BuiltInFrameStyle")>]
type BuiltInFrameStyle =
    | Automatic
    | Phone
    | Laptop

    override style.ToString() =
        match style with
        | Automatic -> "auto"
        | Phone -> "phone"
        | Laptop -> "laptop"

[<DebuggerDisplay("FrameSource")>]
type FrameSource =
    | CustomFrame of path: string
    | BuiltInFrame of style: BuiltInFrameStyle

    override source.ToString() =
        match source with
        | CustomFrame path -> path
        | BuiltInFrame style -> String.Concat("builtin:", style.ToString())

[<DebuggerDisplay("CaptureFormat")>]
type CaptureFormat =
    | Png
    | WebP

    override format.ToString() =
        match format with
        | Png -> "png"
        | WebP -> "webp"

[<DebuggerDisplay("RecordingBackend")>]
type RecordingBackend =
    | CdpScreencast

    override _.ToString() = "screencast"

type CaptureRequest =
    { ScriptPath: string
      OutputPath: string option
      BrowserPath: string option
      Force: bool }

    override request.ToString() = request.ScriptPath

type InitRequest =
    { TargetDirectory: string
      Interactive: bool
      Force: bool }

    override request.ToString() = request.TargetDirectory

[<DebuggerDisplay("Command")>]
type Command =
    | Capture of CaptureRequest
    | Init of InitRequest
    | BrowserInstall
    | Help
    | Version

    override command.ToString() =
        match command with
        | Capture request -> request.ScriptPath
        | Init request -> request.TargetDirectory
        | BrowserInstall -> "browser install"
        | Help -> "--help"
        | Version -> "--version"

type PlannedCapture =
    { Format: CaptureFormat
      OutputRelativePath: string
      OutputPath: string
      Device: Device
      Axes: (string * TomlValue) list
      Data: (string * TomlValue) list }

    override capture.ToString() = capture.OutputPath

type CapturePlan =
    { ScriptPath: string
      ScriptDirectory: string
      OutputPath: string
      FrameSource: FrameSource option
      BrowserPath: string option
      BrowserArguments: string list
      FramesPerSecond: int
      Captures: PlannedCapture list
      Force: bool }

    override plan.ToString() = plan.OutputPath

type CapturePerformanceMetrics =
    { Backend: RecordingBackend
      FrameCount: int
      UniqueFrameCount: int
      ActiveDuration: TimeSpan
      CaptureDurations: TimeSpan list
      MissedSlots: int
      DuplicatedFrames: int
      DroppedFrames: int }

    override metrics.ToString() =
        String.Concat(
            metrics.FrameCount.ToString(CultureInfo.InvariantCulture),
            " frames, ",
            metrics.ActiveDuration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
            " ms"
        )

type CaptureOutputResult =
    { Path: string
      Format: CaptureFormat
      FrameTicksMs: int list
      Performance: CapturePerformanceMetrics option
      AnimationUpdateDurations: TimeSpan list }

    override result.ToString() = result.Path

type CaptureRunResult =
    { Outputs: CaptureOutputResult list }

    override result.ToString() =
        result.Outputs
        |> List.map (fun output -> output.Path)
        |> String.concat Environment.NewLine
