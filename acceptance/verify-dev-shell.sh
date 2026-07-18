#!/usr/bin/env bash
set -euo pipefail

for executable in clang dotnet ffmpeg lua-language-server nixfmt pkg-config python3 tree-sitter; do
  executable_path=$(command -v "$executable")

  case "$executable_path" in
    /nix/store/*) ;;
    *)
      printf 'development shell executable is not from the Nix store: %s -> %s\n' \
        "$executable" "$executable_path" >&2
      exit 1
      ;;
  esac
done

test -n "$VISET_BROWSER"
test -x "$VISET_BROWSER"
"$VISET_BROWSER" --version >/dev/null

case "$VISET_BROWSER" in
  /nix/store/*) ;;
  *)
    printf 'development shell browser is not from the Nix store: %s\n' "$VISET_BROWSER" >&2
    exit 1
    ;;
esac

printf 'development shell: Nix tools and browser present\n'
