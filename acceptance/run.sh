#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
python=${VISET_PYTHON:-$(command -v python3)}
work="$root/.agent-workspace/core-fixture"
output="$work/output"
rm -rf "$work"
mkdir -p "$work"

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

[[ "$("$binary" --version)" == "viset 0.1.0" ]]
"$binary" --help | grep -q '^  viset capture CAPTURE.lua '

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

"$python" "$root/acceptance/verify-output.py" \
  "$output" \
  --fps 60 \
  --max-animation-duration-ms 800 \
  --media-size screenshots/red.png=400x300 \
  --media-size screenshots/blue.png=400x300 \
  screenshots/red.png \
  screenshots/blue.png \
  animations/motion.webp

[[ "$(sha256sum "$output/screenshots/red.png" | cut -d' ' -f1)" != \
   "$(sha256sum "$output/screenshots/blue.png" | cut -d' ' -f1)" ]]
[[ ! -e "$output/.viset" ]]
[[ ! -e "$output/manifest.toml" ]]

managed_dir="$root/src/Viset/bin/Release/net10.0"
rid_dir="$managed_dir/linux-x64"
LD_LIBRARY_PATH="$rid_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  dotnet fsi \
    --reference:"$managed_dir/Magick.NET.Core.dll" \
    --reference:"$managed_dir/Magick.NET-Q8-AnyCPU.dll" \
    "$root/acceptance/verify-media.fsx" "$output"

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

init_project="$work/init-project"
"$binary" init "$init_project"
[[ -f "$init_project/capture.lua" ]]
[[ -f "$init_project/README.md" ]]
grep -q '^/output/$' "$init_project/.gitignore"
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
