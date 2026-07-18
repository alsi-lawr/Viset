# Viset

Script reproducible browser screenshots and animations from a capture matrix.

Viset turns one trusted Lua file into one or more PNG screenshots or animated
WebPs. A strict TOML header describes devices, matrix axes, output paths, and
media settings; imperative Lua drives the page and decides when to capture.

![An animated WebP produced by Viset](benchmarks/assets/libwebp_anim-1600x900.webp)

> **Status:** Viset is pre-release software. The supported route today is to
> build or run it from this repository with Nix; release archives and package
> channels are not published yet.

## What it does

- Expands declared devices and arbitrary TOML matrix axes in deterministic
  order.
- Captures still PNGs and continuous, pauseable animated WebPs through Chrome
  DevTools Protocol.
- Supports PNG or JPEG screencast input, three WebP encoders, and spooled or
  bounded live processing without changing the conservative defaults.
- Runs top-level Lua with page, HTTP, process, emulation, snapshot, and recording
  APIs.
- Writes ordinary user-owned files directly; Viset does not create output
  manifests or ownership metadata.

## Build and run

From a repository checkout on x86_64 Linux, aarch64 Linux, or Apple Silicon
macOS:

```sh
nix build
./result/bin/viset --version
```

The Nix package supplies the matching browser route. To use another Chrome or
Chromium executable, set `VISET_BROWSER` or pass `--browser PATH`.

Create and run a capture:

```sh
./result/bin/viset init demo
./result/bin/viset capture demo/capture.lua
```

The generated project captures a self-contained page to
`demo/output/example.png`.

## A capture file

Every capture is a `.lua` file whose first non-whitespace content is a TOML
header:

```lua
--[[
# viset
version = 1
output = "output/{device}-{theme}.png"

[devices.desktop]

[devices.desktop.viewport]
width = 1280
height = 720

[matrix]
theme = ["light", "dark"]

[data]
url = "https://example.com"
]]

viset.page.navigate(viset.context.data.url)
viset.page.wait_for("document.readyState === 'complete'", "10s")
viset.snapshot()
```

This writes two files for the desktop device, one for each `theme` value. PNG
captures call `viset.snapshot()` exactly once. WebP captures create exactly one
recording with `viset.record()` and may show or hide recording with
`start()`, `stop()`, and `during()`.

Capture files are trusted, unsandboxed local Lua programs. They run with Lua's
standard libraries and may start processes or access the network. Do not run an
untrusted capture file.

## Commands

```text
viset capture CAPTURE.lua [--output DIR] [--browser PATH] [--force]
viset init [DIR] [-i|--interactive] [--force]
viset browser install
viset --version
viset --help
```

`--output DIR` overrides the TOML `output_root`. Existing declared outputs are
rejected before capture unless `--force` is supplied.

## Examples and reference

- [Minimal PNG example](examples/minimal/)
- [Device and theme matrix with PNG and WebP outputs](examples/medium/)
- [Capture format reference](docs/capture-format.md)
- [Trusted Lua API reference](docs/lua-api.md)
- [Benchmark reports and retained evidence](benchmarks/)

## Performance boundary

Viset reports acquisition and WebP production metrics for recordings. A strict
1600x900 60 FPS acquisition P95 at or below 16.67 ms is a future optimisation
target, not a current guarantee.

The default WebP path remains PNG screencast input, `libwebp_full`, the spooled
pipeline, lossy quality 75, and method 0. FFmpeg is optional and never bundled.

Full capture smoke is qualified on both supported Linux systems. The Apple
Silicon package, application, development shell, and locked browser startup are
qualified, while full Darwin capture remains a deferred investigation.

## Development

Enter the pinned development environment, then use the locked tools:

```sh
nix develop
dotnet restore Viset.slnx --locked-mode
dotnet tool restore
dotnet build Viset.slnx --configuration Release --no-restore
dotnet test tests/Viset.Tests/Viset.Tests.fsproj --configuration Release --no-restore
dotnet fantomas --check src tests benchmarks
dotnet csharpier check src/Viset.Serialization
nix fmt -- --check flake.nix
python3 .config/verify-documentation.py
```

The full current-system fixture is `acceptance/run.sh`; it requires a selected
Chrome or Chromium executable and the media tools supplied by `nix develop`.

## License

Viset is available under the [MIT License](LICENSE).
