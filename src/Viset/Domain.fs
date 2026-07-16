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

[<DebuggerDisplay("CaptureKind")>]
type CaptureKind =
    | Still
    | Animation of workflow: string

    override kind.ToString() =
        match kind with
        | Still -> "still"
        | Animation workflow -> workflow

type MatrixDefinition =
    { Id: string
      NameTemplate: string
      Kind: CaptureKind
      Axes: (string * TomlValue list) list
      Data: (string * TomlValue) list }

    override definition.ToString() = definition.Id

type CaptureRequest =
    { MatrixPath: string
      OutputPath: string option
      OnlyDefinitionId: string option
      BrowserPath: string option }

    override request.ToString() = request.MatrixPath

[<DebuggerDisplay("Command")>]
type Command =
    | Capture of CaptureRequest
    | BrowserInstall
    | Help
    | Version

    override command.ToString() =
        match command with
        | Capture request -> request.MatrixPath
        | BrowserInstall -> "browser install"
        | Help -> "--help"
        | Version -> "--version"

type PlannedCapture =
    { DefinitionId: string
      Kind: CaptureKind
      LogicalName: string
      OutputRelativePath: string
      Device: Device
      Axes: (string * TomlValue) list
      Data: (string * TomlValue) list }

    override capture.ToString() = capture.OutputRelativePath

type CapturePlan =
    { MatrixPath: string
      OutputPath: string
      AdapterPath: string
      FramePath: string option
      BrowserPath: string option
      BrowserArguments: string list
      FramesPerSecond: int
      DefinitionIds: string list
      SelectedDefinitionIds: string list
      Captures: PlannedCapture list
      Warnings: string list }

    override plan.ToString() = plan.OutputPath
