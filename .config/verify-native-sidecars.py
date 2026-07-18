#!/usr/bin/env python3
from __future__ import annotations

import argparse
import pathlib
import sys
import tomllib


def current_platform() -> str:
    if sys.platform.startswith("linux"):
        return "linux"
    if sys.platform == "darwin":
        return "macos"
    if sys.platform in {"cygwin", "win32"}:
        return "windows"
    raise RuntimeError(f"unsupported sidecar verification platform: {sys.platform}")


parser = argparse.ArgumentParser()
parser.add_argument("binary", type=pathlib.Path)
parser.add_argument("--platform", choices=["linux", "macos", "windows"])
arguments = parser.parse_args()

manifest_path = pathlib.Path(__file__).with_name("native-sidecars.toml")
manifest = tomllib.loads(manifest_path.read_text(encoding="utf-8"))
assert manifest["version"] == 1

platform = arguments.platform or current_platform()
directory = arguments.binary.resolve().parent
required = manifest["platforms"][platform]["files"]
missing = [name for name in required if not (directory / name).is_file()]

if missing:
    raise FileNotFoundError(
        f"missing required {platform} native sidecars beside {arguments.binary}: "
        + ", ".join(missing)
    )

print(f"native sidecars: {platform} {', '.join(required)}")
