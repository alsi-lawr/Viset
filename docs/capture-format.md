# Capture format

A Viset capture is one trusted `.lua` file. Its first non-whitespace content
must be a long comment containing a versioned TOML document:

```lua
--[[
# viset
version = 1
output_root = "output"
output = "animations/{device}-{theme}.webp"
frame = "builtin:auto"
frames_per_second = 30
browser_arguments = []

[webp]
source = "png_screencast"
encoder = "libwebp_full"
pipeline = "spooled"
mode = "lossy"
quality = 75
method = 0

[devices.laptop]
mobile = false
touch = false
device_scale = 1.0

[devices.laptop.viewport]
width = 1280
height = 720

[matrix]
theme = ["light", "dark"]

[data]
url = "https://example.com"
]]
```

The marker is exactly `# viset`, and version 1 is the only supported format.
Unknown properties in the fixed schema are errors. The `matrix` and `data`
tables are intentionally open and may contain nested TOML values.

## Top-level fields

| Field | Required | Meaning |
| --- | --- | --- |
| `version` | yes | Must be `1`. |
| `output` | yes | Project-relative `.png` or `.webp` path template. |
| `output_root` | no | Base directory. Relative values resolve from the capture file; the default is its directory. |
| `frame` | no | Custom frame HTML path or `builtin:auto`, `builtin:phone`, or `builtin:laptop`. |
| `frames_per_second` | WebP only | Integer from 1 through 60. Defaults to 30. |
| `browser_arguments` | no | Additional browser arguments. Defaults to an empty array. |
| `devices` | yes | One or more named device tables. |
| `matrix` | no | Arbitrary named expansion axes. Each value must be a non-empty TOML array. |
| `data` | no | Arbitrary values exposed as `viset.context.data`. |
| `webp` | WebP only | WebP source, encoder, pipeline, and quality settings. |

`--output DIR` overrides `output_root` for one invocation.

## Devices and matrix expansion

Every `[devices.<name>]` declaration is an implicit expansion axis. Devices run
in declaration order, followed by the cartesian product of matrix axes in their
declaration order. `matrix.device` is invalid because devices already expand
automatically.

Each device requires a viewport:

```toml
[devices.phone]
mobile = true
touch = true
device_scale = 2.0

[devices.phone.viewport]
width = 390
height = 844

[devices.phone.frame]
width = 430
height = 900
```

`mobile` and `touch` default to `false`; `device_scale` defaults to `1.0` and
must be positive. Width and height are positive integers. A device-level
`frame` sets the final canvas dimensions. A top-level built-in frame derives
appropriate dimensions unless a device supplies explicit frame dimensions.
Custom frame HTML requires explicit frame dimensions for every device.

`builtin:auto` selects the phone frame when `mobile = true` and the laptop
frame otherwise. Framing is applied to PNG snapshots and every WebP source
frame.

### Custom frame HTML

A custom `frame` path resolves from the capture-file directory. Viset injects a
read-only `window.visetFrame` object containing `device`, `current`,
`subscribe(callback)`, and `update()`. `current.image_url` addresses the latest
source image. The frame must add a `data-frame-ready` attribute after its first
render and after every update; Viset clears that marker before supplying the
next image and waits for it before capture.

## Output templates and ownership

`{device}` and each matrix-axis name may appear in `output`. Placeholder values
must be scalar. Expanded paths must be unique, relative, use forward slashes,
and contain no empty, hidden, dot, traversal, or unsafe segments.

Viset preflights every declared destination. If any exists, capture stops before
browser work unless `--force` is present. Successful output replaces the file
directly. Viset creates no output manifest, ownership marker, history, or
rollback data.

Matrix and data values may be strings, booleans, finite numbers, dates or times,
arrays, and tables. Integers must fit Lua's exact safe-integer range. Dates and
times are exposed to Lua as strings.

## WebP configuration

The `[webp]` table is valid only when `output` ends in `.webp`.

| Field | Values | Default |
| --- | --- | --- |
| `source` | `png_screencast`, `jpeg_screencast` | `png_screencast` |
| `source_quality` | Integer 0 through 100; JPEG source only | 95 |
| `encoder` | `libwebp_full`, `libwebp_anim`, `ffmpeg` | `libwebp_full` |
| `pipeline` | `spooled`, `live` | `spooled` |
| `mode` | `lossy`, `lossless` | `lossy` |
| `quality` | Number 0 through 100 | 75 for lossy; 50 effort for lossless |
| `method` | Integer 0 through 6 | 0 |

`libwebp_full` coalesces exact consecutive source frames while preserving their
combined duration. The live pipeline keeps at most eight pending compressed
frames in memory and spills overflow to temporary disk in index order.

The `ffmpeg` encoder requires a usable `ffmpeg` with `libwebp_anim` on `PATH`.
Viset validates it before starting the browser and does not bundle it. Still
capture always produces PNG; browser-produced WebP is not a supported source.

## Browser arguments

`browser_arguments` passes additional values to Chrome or Chromium. Empty or
control-character values are rejected. Viset owns browser isolation, so
arguments that set the remote-debugging port or pipe, or the user-data
directory, are rejected.

Use `--browser PATH` or `VISET_BROWSER` to select an executable. Otherwise Viset
uses the packaged browser route or its locked browser metadata.

## Lua execution

After planning all outputs, Viset runs the capture file once in a fresh Lua
state for each expanded output. The selected device, matrix values, data, and
absolute output path are available through `viset.context`. See the
[trusted Lua API reference](lua-api.md).
