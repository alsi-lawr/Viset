#!/usr/bin/env python3
from __future__ import annotations

import argparse
import ctypes
import os
import pathlib
import struct
import subprocess
import tomllib


RID_LAYOUTS = {
    "linux-x64": ("viset", "linux", 62),
    "linux-arm64": ("viset", "linux", 183),
    "osx-arm64": ("viset", "macos", 0x0100000C),
    "win-x64": ("viset.exe", "windows", 0x8664),
    "win-arm64": ("viset.exe", "windows", 0xAA64),
}


def executable_machine(path: pathlib.Path, platform: str) -> int:
    payload = path.read_bytes()
    if platform == "linux":
        if payload[:6] != b"\x7fELF\x02\x01":
            raise ValueError(f"{path} is not a 64-bit little-endian ELF executable")
        return struct.unpack_from("<H", payload, 18)[0]
    if platform == "windows":
        if payload[:2] != b"MZ":
            raise ValueError(f"{path} is not a PE executable")
        header = struct.unpack_from("<I", payload, 0x3C)[0]
        if payload[header : header + 4] != b"PE\0\0":
            raise ValueError(f"{path} has an invalid PE header")
        return struct.unpack_from("<H", payload, header + 4)[0]
    if payload[:4] != b"\xcf\xfa\xed\xfe":
        raise ValueError(f"{path} is not a 64-bit little-endian Mach-O executable")
    return struct.unpack_from("<I", payload, 4)[0]


def run(binary: pathlib.Path, *arguments: str) -> str:
    result = subprocess.run(
        [str(binary), *arguments],
        check=True,
        capture_output=True,
        text=True,
        timeout=30,
    )
    return result.stdout


def load_native_sidecars(directory: pathlib.Path, platform: str, names: list[str]) -> None:
    dll_directory = None
    if platform == "windows":
        dll_directory = os.add_dll_directory(str(directory))

    try:
        mode = getattr(ctypes, "RTLD_GLOBAL", 0)
        sharp_yuv = ctypes.CDLL(str(directory / names[0]), mode=mode)
        webp = ctypes.CDLL(str(directory / names[1]), mode=mode)
        webp_mux = ctypes.CDLL(str(directory / names[2]), mode=mode)

        webp.WebPGetEncoderVersion.restype = ctypes.c_int
        webp_mux.WebPGetMuxVersion.restype = ctypes.c_int
        encoder_version = webp.WebPGetEncoderVersion()
        mux_version = webp_mux.WebPGetMuxVersion()
        if encoder_version != 0x010600 or mux_version != 0x010600:
            raise RuntimeError(
                f"unexpected libwebp versions: encoder=0x{encoder_version:06x} "
                f"mux=0x{mux_version:06x}"
            )

        # Retain every handle until both version calls have completed.
        assert sharp_yuv and webp and webp_mux
    finally:
        if dll_directory is not None:
            dll_directory.close()


def verify_macos_install_names(directory: pathlib.Path, names: list[str]) -> None:
    for name in names:
        path = directory / name
        identifiers = subprocess.run(
            ["otool", "-D", str(path)],
            check=True,
            capture_output=True,
            text=True,
            timeout=30,
        ).stdout.splitlines()
        expected_identifier = f"@loader_path/{name}"
        if identifiers[1:] != [expected_identifier]:
            raise RuntimeError(
                f"unexpected macOS install ID in {name}: {identifiers[1:]} "
                f"expected {expected_identifier}"
            )

        dependencies = subprocess.run(
            ["otool", "-L", str(path)],
            check=True,
            capture_output=True,
            text=True,
            timeout=30,
        ).stdout
        if "@rpath/" in dependencies:
            raise RuntimeError(f"non-relocatable macOS dependency in {name}:\n{dependencies}")

        load_commands = subprocess.run(
            ["otool", "-l", str(path)],
            check=True,
            capture_output=True,
            text=True,
            timeout=30,
        ).stdout
        if "cmd LC_RPATH" in load_commands:
            raise RuntimeError(f"unexpected LC_RPATH remains in staged macOS sidecar: {name}")


parser = argparse.ArgumentParser()
parser.add_argument("--rid", required=True, choices=RID_LAYOUTS)
parser.add_argument("--directory", required=True, type=pathlib.Path)
parser.add_argument("--require-portable-linux", action="store_true")
arguments = parser.parse_args()

root = pathlib.Path(__file__).resolve().parents[2]
directory = arguments.directory.resolve()
executable_name, platform, expected_machine = RID_LAYOUTS[arguments.rid]
executable = directory / executable_name

with (root / "acceptance" / "native-sidecars.toml").open("rb") as stream:
    sidecar_manifest = tomllib.load(stream)

webp_sidecars = sidecar_manifest["platforms"][platform]["files"]
expected = {
    executable_name,
    *webp_sidecars,
    "browser-lock.toml",
}
if (directory / "LICENSE").is_file():
    expected.add("LICENSE")

actual = {path.name for path in directory.iterdir() if path.is_file()}
if actual != expected:
    raise RuntimeError(f"staged inventory mismatch: actual={sorted(actual)} expected={sorted(expected)}")

machine = executable_machine(executable, platform)
if machine != expected_machine:
    raise RuntimeError(
        f"{arguments.rid} executable machine is 0x{machine:x}; expected 0x{expected_machine:x}"
    )

if platform == "macos":
    verify_macos_install_names(directory, webp_sidecars)
load_native_sidecars(directory, platform, webp_sidecars)

if run(executable, "--version").strip() != "viset 0.1.0":
    raise RuntimeError("unexpected viset version output")
if "viset capture CAPTURE.lua" not in run(executable, "--help"):
    raise RuntimeError("viset help does not contain the capture command")

if arguments.require_portable_linux:
    if platform != "linux":
        raise ValueError("--require-portable-linux is valid only for Linux RIDs")
    for path in directory.iterdir():
        if path.is_file() and b"/nix/store" in path.read_bytes():
            raise RuntimeError(f"Nix store reference found in portable Linux payload: {path.name}")

    dependencies = subprocess.run(
        ["ldd", str(executable)],
        check=True,
        capture_output=True,
        text=True,
        timeout=30,
    ).stdout
    if "not found" in dependencies or "/nix/store" in dependencies:
        raise RuntimeError("portable Linux dependency inspection failed:\n" + dependencies)

print(f"qualified {arguments.rid}: {directory}")
