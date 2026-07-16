#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
binary=${1:-"$root/src/Viset/bin/Release/net10.0/linux-x64/publish/viset"}

if [[ ! -x "$binary" ]]; then
  printf 'fixture binary is not executable: %s\n' "$binary" >&2
  exit 2
fi

browser=${VISET_BROWSER:-}
if [[ -z "$browser" ]]; then
  browser=$(command -v google-chrome || command -v chromium || command -v chromium-browser || true)
fi

if [[ -z "$browser" ]]; then
  printf 'fixture requires VISET_BROWSER or a discoverable Chrome/Chromium\n' >&2
  exit 2
fi

python=${VISET_PYTHON:-$(command -v python3)}
port=$(
  "$python" - <<'PY'
import socket
with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    print(listener.getsockname()[1])
PY
)

work="$root/.agent-workspace/core-fixture"
output="$work/output"
log="$work/hooks.log"
rm -rf "$work"
mkdir -p "$work"

export VISET_BROWSER="$browser"
export VISET_FIXTURE_PORT="$port"
export VISET_FIXTURE_ROOT="$root/acceptance"
export VISET_FIXTURE_LOG="$log"
export VISET_PYTHON="$python"

"$binary" capture "$root/acceptance/matrix.toml" --output "$output"
"$python" "$root/acceptance/verify-manifest.py" "$output"

red_before=$(sha256sum "$output/screenshots/red.png" | cut -d' ' -f1)
blue_before=$(sha256sum "$output/screenshots/blue.png" | cut -d' ' -f1)
animation_before=$(sha256sum "$output/animations/motion.webp" | cut -d' ' -f1)
"$binary" capture "$root/acceptance/matrix.toml" --output "$output" --only fixture-animation
"$python" "$root/acceptance/verify-manifest.py" "$output"
[[ "$red_before" == "$(sha256sum "$output/screenshots/red.png" | cut -d' ' -f1)" ]]
[[ "$blue_before" == "$(sha256sum "$output/screenshots/blue.png" | cut -d' ' -f1)" ]]
[[ "$animation_before" == "$(sha256sum "$output/animations/motion.webp" | cut -d' ' -f1)" ]]

managed_dir="$root/src/Viset/bin/Release/net10.0"
rid_dir="$managed_dir/linux-x64"
LD_LIBRARY_PATH="$rid_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  dotnet fsi \
    --reference:"$managed_dir/Magick.NET.Core.dll" \
    --reference:"$managed_dir/Magick.NET-Q8-AnyCPU.dll" \
    "$root/acceptance/verify-media.fsx" "$output"

expected_hooks=22
actual_hooks=$(wc -l < "$log")
if [[ "$actual_hooks" -ne "$expected_hooks" ]]; then
  printf 'expected %d lifecycle log entries, got %d\n' "$expected_hooks" "$actual_hooks" >&2
  cat "$log" >&2
  exit 1
fi

mapfile -t hook_lines < "$log"
expected_prefixes=(
  "prepare:screenshots/red"
  "start:screenshots/red:"
  "ready:screenshots/red"
  "open:screenshots/red"
  "stop:screenshots/red"
  "prepare:screenshots/blue"
  "start:screenshots/blue:"
  "ready:screenshots/blue"
  "open:screenshots/blue"
  "stop:screenshots/blue"
  "prepare:animations/motion"
  "start:animations/motion:"
  "ready:animations/motion"
  "open:animations/motion"
  "workflow:animations/motion"
  "stop:animations/motion"
  "prepare:animations/motion"
  "start:animations/motion:"
  "ready:animations/motion"
  "open:animations/motion"
  "workflow:animations/motion"
  "stop:animations/motion"
)

for index in "${!expected_prefixes[@]}"; do
  if [[ "${hook_lines[$index]}" != "${expected_prefixes[$index]}"* ]]; then
    printf 'unexpected hook order at line %d: %s\n' "$((index + 1))" "${hook_lines[$index]}" >&2
    exit 1
  fi
done

while IFS=: read -r action _ process_id; do
  if [[ "$action" == "start" ]] && kill -0 "$process_id" 2>/dev/null; then
    printf 'fixture child process remains alive: %s\n' "$process_id" >&2
    exit 1
  fi
done < "$log"

failure_output="$work/failure-output"
failure_log="$work/failure-hooks.log"
export VISET_FIXTURE_LOG="$failure_log"
export VISET_FIXTURE_FAIL=1

if "$binary" capture "$root/acceptance/matrix.toml" --output "$failure_output" --only fixture-animation; then
  printf 'forced fixture failure unexpectedly succeeded\n' >&2
  exit 1
fi

unset VISET_FIXTURE_FAIL
grep -q '^stop:animations/motion$' "$failure_log"
failure_pid=$(awk -F: '/^start:/{print $3}' "$failure_log")
if kill -0 "$failure_pid" 2>/dev/null; then
  printf 'forced-failure child process remains alive: %s\n' "$failure_pid" >&2
  exit 1
fi

if [[ -e "$failure_output/.viset" || -e "$failure_output/manifest.toml" ]]; then
  printf 'forced failure wrote owned output metadata\n' >&2
  exit 1
fi

unowned="$work/unowned"
unowned_log="$work/unowned-hooks.log"
mkdir -p "$unowned"
printf 'preserve\n' > "$unowned/sentinel.txt"
export VISET_FIXTURE_LOG="$unowned_log"

if "$binary" capture "$root/acceptance/matrix.toml" --output "$unowned" --only fixture-animation; then
  printf 'unowned output root unexpectedly succeeded\n' >&2
  exit 1
fi

grep -q '^preserve$' "$unowned/sentinel.txt"
[[ ! -e "$unowned_log" ]]

printf 'fixture output: %s\n' "$output"
