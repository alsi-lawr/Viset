# Contributing to Viset

Thanks for helping improve Viset. Keep changes focused, preserve the trusted
single-file capture contract, and add verification at the narrowest useful
level.

## Development environment

The Nix development shell pins the .NET SDK, browser, media tools, formatters,
and supporting utilities:

```sh
git clone https://github.com/alsi-lawr/Viset.git
cd Viset
nix develop
dotnet restore Viset.slnx --locked-mode
dotnet tool restore
```

## Build and test

Build the complete solution with warnings treated as errors:

```sh
dotnet build Viset.slnx --configuration Release --no-restore
```

Run the deterministic unit suite:

```sh
dotnet test tests/Viset.Tests/Viset.Tests.fsproj \
  --configuration Release --no-build --no-restore
```

Publish the current target, then run the end-to-end CLI and browser/media suite:

```sh
dotnet publish src/Viset/Viset.fsproj \
  --configuration Release \
  --runtime linux-x64 \
  --no-restore
VISET_END_TO_END_BINARY="$PWD/src/Viset/bin/Release/net10.0/linux-x64/publish/viset" \
  dotnet test tests/Viset.EndToEnd/Viset.EndToEnd.fsproj \
    --configuration Release \
    --no-restore
```

Set `VISET_BROWSER` when Chrome or Chromium is not discoverable. CI may set
`VISET_END_TO_END_BROWSER_ARGUMENTS` to newline-separated platform arguments.

F# tests use FsUnit and backticked, plain-English identifiers containing
`should`.

## Formatting and documentation

```sh
dotnet fantomas --check src tests benchmarks
dotnet csharpier check src/Viset.Serialization
nix fmt -- --check flake.nix
python3 .config/verify-documentation.py
```

Run formatters without `--check` when applying formatting changes. Public
installation or feature claims must have a working command or committed
evidence behind them.

## Native AOT and Nix

The portability workflow publishes Native AOT on each matching runner. For a
current-system Linux check:

```sh
dotnet publish src/Viset/Viset.fsproj \
  --configuration Release \
  --runtime linux-x64 \
  --no-restore
nix flake check --print-build-logs
```

Do not add CoreCLR fallback, warning suppression, cross-OS publication, or an
unverified package route to make a check pass.

## Pull requests

- Explain the user-visible outcome and any compatibility boundary.
- Keep generated, benchmark, and test evidence reviewable.
- Report the checks actually run and any platform not exercised.
- Avoid unrelated cleanup, broad formatting, or speculative abstractions.
