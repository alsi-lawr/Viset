#!/usr/bin/env bash
set -euo pipefail

for executable in clang dotnet ffmpeg lua-language-server nixfmt pkg-config python3 tree-sitter; do
  command -v "$executable" >/dev/null
done

test -n "$VISET_BROWSER"
test -x "$VISET_BROWSER"

printf 'development shell: tools and browser present\n'
