#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
python=${VISET_PYTHON:-$(command -v python3)}
work="$root/.agent-workspace/core-fixture"
output="$work/output"
rm -rf "$work"
mkdir -p "$work"

dotnet test "$root/tests/Viset.Tests/Viset.Tests.fsproj" \
  --configuration Release \
  --no-restore

if [[ ${1:-} == "--publish-current" ]]; then
  rid=${2:-linux-x64}
  binary="$root/src/Viset/bin/Release/net10.0/$rid/publish/viset"
  publish_log="$work/aot-publish.log"

  set +e
  dotnet publish "$root/src/Viset/Viset.fsproj" \
    --configuration Release \
    --runtime "$rid" \
    --no-restore \
    2>&1 | tee "$publish_log"
  publish_status=${PIPESTATUS[0]}
  set -e

  "$python" "$root/acceptance/verify-aot-log.py" "$publish_log"

  if [[ "$publish_status" -ne 0 ]]; then
    exit "$publish_status"
  fi
else
  binary=${1:-"$root/src/Viset/bin/Release/net10.0/linux-x64/publish/viset"}
fi

if [[ ! -x "$binary" ]]; then
  printf 'fixture binary is not executable: %s\n' "$binary" >&2
  exit 2
fi

"$python" "$root/acceptance/verify-native-sidecars.py" "$binary"

[[ "$("$binary" --version)" == "viset 0.1.0" ]]
"$binary" --help | grep -q '^  viset capture CAPTURE.lua '

cat > "$work/unknown-property.lua" <<'LUA'
--[[
# viset
version = 1
output = "capture.png"
mystery_option = true

[devices.desktop]

[devices.desktop.viewport]
width = 320
height = 240
]]

viset.snapshot()
LUA

if "$binary" capture "$work/unknown-property.lua" > "$work/unknown-property.log" 2>&1; then
  printf 'capture with an unknown TOML property unexpectedly succeeded\n' >&2
  exit 1
fi
grep -Fq "Unknown TOML property 'capture.mystery_option'." "$work/unknown-property.log"

cat > "$work/invalid-webp-method.lua" <<'LUA'
--[[
# viset
version = 1
output = "capture.webp"

[webp]
method = 7

[devices.desktop]

[devices.desktop.viewport]
width = 320
height = 240
]]

local recording = viset.record()
recording:start()
recording:during("100ms")
recording:stop()
LUA

if "$binary" capture "$work/invalid-webp-method.lua" > "$work/invalid-webp-method.log" 2>&1; then
  printf 'capture with invalid webp.method unexpectedly succeeded\n' >&2
  exit 1
fi
grep -Fq 'webp.method must be between 0 and 6.' "$work/invalid-webp-method.log"

"$python" - "$work" <<'PY'
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
cases = {
    "invalid-webp-source": 'source = "browser_webp"',
    "invalid-webp-source-quality": 'source_quality = 95',
    "invalid-webp-encoder": 'encoder = "libwebp"',
    "invalid-webp-pipeline": 'pipeline = "streaming"',
    "invalid-webp-mode": 'mode = "near_lossless"',
    "invalid-webp-quality": 'quality = 101',
    "removed-webp-lossless": 'lossless = true',
}
template = '''--[[
# viset
version = 1
output = "capture.webp"

[webp]
{configuration}

[devices.desktop]

[devices.desktop.viewport]
width = 320
height = 240
]]

local recording = viset.record()
recording:start()
recording:during("100ms")
recording:stop()
'''
for name, configuration in cases.items():
    (root / f"{name}.lua").write_text(template.format(configuration=configuration))
PY

while IFS='|' read -r name expected; do
  if "$binary" capture "$work/$name.lua" > "$work/$name.log" 2>&1; then
    printf 'capture with %s unexpectedly succeeded\n' "$name" >&2
    exit 1
  fi
  grep -Fq "$expected" "$work/$name.log"
done <<'CASES'
invalid-webp-source|Unknown webp.source 'browser_webp'; expected png_screencast or jpeg_screencast.
invalid-webp-source-quality|webp.source_quality is valid only when webp.source = 'jpeg_screencast'.
invalid-webp-encoder|Unknown webp.encoder 'libwebp'; expected libwebp_full, libwebp_anim, or ffmpeg.
invalid-webp-pipeline|Unknown webp.pipeline 'streaming'; expected spooled or live.
invalid-webp-mode|Unknown webp.mode 'near_lossless'; expected lossy or lossless.
invalid-webp-quality|webp.quality must be a finite number between 0 and 100.
removed-webp-lossless|Unknown TOML property 'webp.lossless'.
CASES

mkdir -p "$work/no-ffmpeg-path"
if PATH="$work/no-ffmpeg-path" \
  "$binary" capture "$root/acceptance/animation-ffmpeg.lua" \
    > "$work/missing-ffmpeg.log" 2>&1; then
  printf 'ffmpeg capture unexpectedly passed without ffmpeg on PATH\n' >&2
  exit 1
fi
grep -Fq "webp.encoder = 'ffmpeg' requires ffmpeg" "$work/missing-ffmpeg.log"

cat > "$work/redundant-device-axis.lua" <<'LUA'
--[[
# viset
version = 1
output = "{device}.png"

[devices.desktop]

[devices.desktop.viewport]
width = 320
height = 240

[matrix]
device = ["desktop"]
]]

viset.snapshot()
LUA

if "$binary" capture "$work/redundant-device-axis.lua" > "$work/redundant-device-axis.log" 2>&1; then
  printf 'capture with matrix.device unexpectedly succeeded\n' >&2
  exit 1
fi
grep -Fq 'matrix.device is redundant' "$work/redundant-device-axis.log"

browser=${VISET_BROWSER:-}
if [[ -z "$browser" ]]; then
  browser=$(command -v google-chrome || command -v chromium || command -v chromium-browser || true)
fi

if [[ -z "$browser" ]]; then
  printf 'fixture requires VISET_BROWSER or a discoverable Chrome/Chromium\n' >&2
  exit 2
fi

free_port() {
  "$python" - <<'PY'
import socket
with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
}

assert_port_closed() {
  VISET_CHECK_PORT=$1 "$python" - <<'PY'
import os
import socket
with socket.socket() as client:
    client.settimeout(0.2)
    assert client.connect_ex(("127.0.0.1", int(os.environ["VISET_CHECK_PORT"]))) != 0
PY
}

run_capture() {
  local script=$1
  local destination=$2
  shift 2
  local port
  local status=0
  port=$(free_port)

  VISET_BROWSER="$browser" \
  VISET_PYTHON="$python" \
  VISET_FIXTURE_PORT="$port" \
  VISET_FIXTURE_ROOT="$root/acceptance" \
    "$binary" capture "$script" --output "$destination" "$@" || status=$?

  assert_port_closed "$port"
  return "$status"
}

run_capture "$root/acceptance/stills.lua" "$output"
run_capture "$root/acceptance/animation.lua" "$output"

anim_output="$work/libwebp-anim-output"
run_capture "$root/acceptance/animation-libwebp-anim.lua" "$anim_output"

ffmpeg_output="$work/ffmpeg-output"
run_capture "$root/acceptance/animation-ffmpeg.lua" "$ffmpeg_output"

coalescing_output="$work/coalescing-output"
run_capture "$root/acceptance/coalescing.lua" "$coalescing_output" \
  2>&1 | tee "$work/coalescing.log"
grep -Eq 'webp_metrics: .* frames=12 encoded=2 ' "$work/coalescing.log"

live_output="$work/live-output"
run_capture "$root/acceptance/animation-live-spill.lua" "$live_output" \
  2>&1 | tee "$work/live-spill.log"
grep -Eq 'webp_metrics: .* pipeline=live .* spilled=[1-9][0-9]* ' "$work/live-spill.log"

"$python" "$root/acceptance/verify-output.py" \
  "$output" \
  --expected-animation-duration-ms 400 \
  --max-animation-duration-ms 800 \
  --min-animation-frames 2 \
  --media-size screenshots/red.png=400x300 \
  --media-size screenshots/blue.png=400x300 \
  screenshots/red.png \
  screenshots/blue.png \
  animations/motion.webp

"$python" "$root/acceptance/verify-output.py" \
  "$anim_output" \
  --expected-animation-duration-ms 400 \
  --min-animation-frames 2 \
  --media-size animations/motion-libwebp-anim.webp=400x300 \
  animations/motion-libwebp-anim.webp

"$python" "$root/acceptance/verify-output.py" \
  "$ffmpeg_output" \
  --expected-animation-duration-ms 400 \
  --max-animation-duration-ms 800 \
  --min-animation-frames 2 \
  --media-size animations/motion.webp=400x300 \
  animations/motion.webp

"$python" "$root/acceptance/verify-output.py" \
  "$coalescing_output" \
  --expected-animation-duration-ms 400 \
  --max-animation-frames 2 \
  --media-size animations/coalesced.webp=320x240 \
  animations/coalesced.webp

"$python" "$root/acceptance/verify-output.py" \
  "$live_output" \
  --expected-animation-duration-ms 400 \
  --media-size animations/live-spill.webp=1600x900 \
  animations/live-spill.webp

[[ "$(sha256sum "$output/screenshots/red.png" | cut -d' ' -f1)" != \
   "$(sha256sum "$output/screenshots/blue.png" | cut -d' ' -f1)" ]]
[[ ! -e "$output/.viset" ]]
[[ ! -e "$output/manifest.toml" ]]

if run_capture "$root/acceptance/stills.lua" "$output"; then
  printf 'existing output unexpectedly succeeded without --force\n' >&2
  exit 1
fi
run_capture "$root/acceptance/stills.lua" "$output" --force

failure_output="$work/failure-output"
failure_port=$(free_port)
if VISET_BROWSER="$browser" \
  VISET_PYTHON="$python" \
  VISET_FIXTURE_PORT="$failure_port" \
  VISET_FIXTURE_ROOT="$root/acceptance" \
  VISET_FIXTURE_FAIL=1 \
    "$binary" capture "$root/acceptance/animation.lua" --output "$failure_output"; then
  printf 'forced fixture failure unexpectedly succeeded\n' >&2
  exit 1
fi
assert_port_closed "$failure_port"
[[ ! -e "$failure_output/animations/motion.webp" ]]

active_failure_output="$work/active-failure-output"
active_failure_port=$(free_port)
if VISET_BROWSER="$browser" \
  VISET_PYTHON="$python" \
  VISET_FIXTURE_PORT="$active_failure_port" \
  VISET_FIXTURE_ROOT="$root/acceptance" \
  VISET_FIXTURE_FAIL_ACTIVE=1 \
    "$binary" capture "$root/acceptance/animation.lua" --output "$active_failure_output"; then
  printf 'forced active recording failure unexpectedly succeeded\n' >&2
  exit 1
fi
assert_port_closed "$active_failure_port"
[[ ! -e "$active_failure_output/animations/motion.webp" ]]

init_project="$work/init-project"
"$binary" init "$init_project"
[[ -f "$init_project/capture.lua" ]]
[[ -f "$init_project/README.md" ]]
[[ -f "$init_project/.luarc.json" ]]
[[ -f "$init_project/.viset/viset.d.lua" ]]
[[ -f "$init_project/.viset/nvim/queries/lua/injections.scm" ]]
grep -q '^/output/$' "$init_project/.gitignore"
grep -Fq '"runtime.version": "Lua 5.2"' "$init_project/.luarc.json"
grep -Fq -- '---@class VisetApi' "$init_project/.viset/viset.d.lua"
grep -Fq '@_javascript "javascript"' "$init_project/.viset/nvim/queries/lua/injections.scm"
grep -Fq 'injection.language "toml"' "$init_project/.viset/nvim/queries/lua/injections.scm"
VISET_BROWSER="$browser" "$binary" capture "$init_project/capture.lua"
[[ -f "$init_project/output/example.png" ]]
grep -q 'viset capture capture.lua' "$init_project/README.md"
grep -q 'output/example.png' "$init_project/README.md"

interactive_project="$work/interactive-project"
printf '%s\n' \
  'data:text/html,%3Ch1%3EInteractive%3C%2Fh1%3E' \
  'capture.png' \
  '1024' \
  '640' \
  | "$binary" init "$interactive_project" --interactive
grep -q '^output = "capture.png"$' "$interactive_project/capture.lua"
grep -q '^width = 1024$' "$interactive_project/capture.lua"
grep -q '^height = 640$' "$interactive_project/capture.lua"
grep -q '^/capture.png$' "$interactive_project/.gitignore"

minimal_output="$work/minimal-example"
minimal_port=$(free_port)
VISET_BROWSER="$browser" VISET_EXAMPLE_PORT="$minimal_port" VISET_EXAMPLE_PYTHON="$python" \
  "$binary" capture "$root/examples/minimal/capture.lua" --output "$minimal_output"
assert_port_closed "$minimal_port"
"$python" "$root/acceptance/verify-output.py" \
  "$minimal_output" \
  --media-size screenshots/home.png=960x600 \
  screenshots/home.png

medium_output="$work/medium-example"
medium_port=$(free_port)
VISET_BROWSER="$browser" VISET_EXAMPLE_PORT="$medium_port" VISET_EXAMPLE_PYTHON="$python" \
  "$binary" capture "$root/examples/medium/screenshots.lua" --output "$medium_output"
assert_port_closed "$medium_port"
medium_port=$(free_port)
VISET_BROWSER="$browser" VISET_EXAMPLE_PORT="$medium_port" VISET_EXAMPLE_PYTHON="$python" \
  "$binary" capture "$root/examples/medium/home-scroll.lua" --output "$medium_output"
assert_port_closed "$medium_port"
"$python" "$root/acceptance/verify-output.py" \
  "$medium_output" \
  --media-size screenshots/laptop-light.png=1308x840 \
  --media-size screenshots/phone-light.png=462x956 \
  --media-size animations/laptop-light-home-scroll.webp=1308x840 \
  --media-size animations/laptop-dark-home-scroll.webp=1308x840 \
  --media-size animations/phone-light-home-scroll.webp=462x956 \
  --media-size animations/phone-dark-home-scroll.webp=462x956 \
  screenshots/laptop-light.png \
  screenshots/laptop-dark.png \
  screenshots/phone-light.png \
  screenshots/phone-dark.png \
  animations/laptop-light-home-scroll.webp \
  animations/laptop-dark-home-scroll.webp \
  animations/phone-light-home-scroll.webp \
  animations/phone-dark-home-scroll.webp

printf 'fixture output: %s\n' "$output"
