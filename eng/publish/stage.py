#!/usr/bin/env python3
from __future__ import annotations

import argparse
import pathlib
import shutil
import subprocess
import tomllib


RID_LAYOUTS = {
    "linux-x64": ("viset", "linux"),
    "linux-arm64": ("viset", "linux"),
    "osx-arm64": ("viset", "macos"),
    "win-x64": ("viset.exe", "windows"),
    "win-arm64": ("viset.exe", "windows"),
}


def remove_macos_rpaths(path: pathlib.Path) -> None:
    output = subprocess.run(
        ["otool", "-l", str(path)],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.splitlines()

    rpaths: list[str] = []
    for index, line in enumerate(output):
        if line.strip() != "cmd LC_RPATH":
            continue
        for candidate in output[index + 1 : index + 5]:
            value = candidate.strip()
            if value.startswith("path "):
                rpaths.append(value.removeprefix("path ").split(" (offset ", 1)[0])
                break

    for rpath in rpaths:
        subprocess.run(
            ["install_name_tool", "-delete_rpath", rpath, str(path)],
            check=True,
        )


def make_macos_sidecars_relocatable(directory: pathlib.Path) -> None:
    for tool in ["otool", "install_name_tool"]:
        if shutil.which(tool) is None:
            raise FileNotFoundError(f"required macOS staging tool is unavailable: {tool}")

    sharp_yuv = directory / "libsharpyuv.dylib"
    webp = directory / "libwebp.dylib"
    webp_mux = directory / "libwebpmux.dylib"

    replacements = {
        "@rpath/libsharpyuv.0.dylib": "@loader_path/libsharpyuv.dylib",
        "@rpath/libwebp.7.dylib": "@loader_path/libwebp.dylib",
        "@rpath/libwebpmux.3.dylib": "@loader_path/libwebpmux.dylib",
    }

    for path in [sharp_yuv, webp, webp_mux]:
        dependency_output = subprocess.run(
            ["otool", "-L", str(path)],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.splitlines()
        dependencies = {
            line.strip().split(" (compatibility ", 1)[0]
            for line in dependency_output[2:]
            if line.strip()
        }

        for source, destination in replacements.items():
            if source in dependencies:
                subprocess.run(
                    ["install_name_tool", "-change", source, destination, str(path)],
                    check=True,
                )

        identifier = f"@loader_path/{path.name}"
        subprocess.run(["install_name_tool", "-id", identifier, str(path)], check=True)
        remove_macos_rpaths(path)


parser = argparse.ArgumentParser()
parser.add_argument("--rid", required=True, choices=RID_LAYOUTS)
parser.add_argument("--publish-directory", required=True, type=pathlib.Path)
parser.add_argument("--destination", required=True, type=pathlib.Path)
arguments = parser.parse_args()

root = pathlib.Path(__file__).resolve().parents[2]
publish_directory = arguments.publish_directory.resolve()
destination = arguments.destination.resolve()
executable, platform = RID_LAYOUTS[arguments.rid]

with (root / "acceptance" / "native-sidecars.toml").open("rb") as stream:
    sidecar_manifest = tomllib.load(stream)

files = [
    (publish_directory / executable, executable),
    *[
        (publish_directory / name, name)
        for name in sidecar_manifest["platforms"][platform]["files"]
    ],
    (root / "browser-lock.toml", "browser-lock.toml"),
]

license_path = root / "LICENSE"
if license_path.is_file():
    files.append((license_path, "LICENSE"))

missing = [str(source) for source, _ in files if not source.is_file()]
if missing:
    raise FileNotFoundError("missing publish inputs: " + ", ".join(missing))

if destination.exists():
    shutil.rmtree(destination)
destination.mkdir(parents=True)

for source, name in files:
    shutil.copy2(source, destination / name)

if platform == "macos":
    make_macos_sidecars_relocatable(destination)

print(f"staged {arguments.rid}: {destination}")
for path in sorted(destination.iterdir()):
    print(path.name)
