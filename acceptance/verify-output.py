#!/usr/bin/env python3
from __future__ import annotations

import argparse
import pathlib


def uint24(payload: bytes) -> int:
    return int.from_bytes(payload, "little")


def animation_frames(payload: bytes) -> tuple[tuple[int, int], list[dict[str, int]]]:
    assert payload[0:4] == b"RIFF"
    assert payload[8:12] == b"WEBP"
    assert int.from_bytes(payload[4:8], "little") + 8 == len(payload)

    canvas = None
    frames = []
    offset = 12

    while offset + 8 <= len(payload):
        identifier = payload[offset : offset + 4]
        size = int.from_bytes(payload[offset + 4 : offset + 8], "little")
        start = offset + 8
        end = start + size
        assert end <= len(payload)
        chunk = payload[start:end]

        if identifier == b"VP8X":
            assert len(chunk) == 10
            canvas = (uint24(chunk[4:7]) + 1, uint24(chunk[7:10]) + 1)
        elif identifier == b"ANMF":
            assert len(chunk) >= 16
            frames.append(
                {
                    "x": uint24(chunk[0:3]) * 2,
                    "y": uint24(chunk[3:6]) * 2,
                    "width": uint24(chunk[6:9]) + 1,
                    "height": uint24(chunk[9:12]) + 1,
                    "duration": uint24(chunk[12:15]),
                    "flags": chunk[15],
                }
            )

        offset = end + size % 2

    assert offset == len(payload)
    assert canvas is not None
    return canvas, frames


parser = argparse.ArgumentParser()
parser.add_argument("root", type=pathlib.Path)
parser.add_argument("expected_paths", nargs="+")
parser.add_argument("--max-animation-duration-ms", type=int)
parser.add_argument("--expected-animation-duration-ms", type=int)
parser.add_argument("--max-animation-frames", type=int)
parser.add_argument("--media-size", action="append", default=[])
arguments = parser.parse_args()

root = arguments.root.resolve()
expected_paths = sorted(arguments.expected_paths)
actual_paths = sorted(
    path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_file()
)
assert actual_paths == expected_paths, (actual_paths, expected_paths)

expected_sizes = {}
for specification in arguments.media_size:
    path, separator, dimensions = specification.partition("=")
    width, size_separator, height = dimensions.partition("x")
    assert separator and size_separator, specification
    expected_sizes[path] = (int(width), int(height))

for relative_path in expected_paths:
    path = root / relative_path
    payload = path.read_bytes()

    if relative_path.endswith(".png"):
        assert payload.startswith(b"\x89PNG\r\n\x1a\n")
        if relative_path in expected_sizes:
            actual_size = (
                int.from_bytes(payload[16:20], "big"),
                int.from_bytes(payload[20:24], "big"),
            )
            assert actual_size == expected_sizes.pop(relative_path)
        continue

    canvas, frames = animation_frames(payload)
    if relative_path in expected_sizes:
        assert canvas == expected_sizes.pop(relative_path)
    assert len(frames) >= 1
    assert all(frame["duration"] > 0 for frame in frames)
    duration = sum(frame["duration"] for frame in frames)
    if arguments.max_animation_duration_ms is not None:
        assert duration <= arguments.max_animation_duration_ms
    if arguments.expected_animation_duration_ms is not None:
        assert duration == arguments.expected_animation_duration_ms
    if arguments.max_animation_frames is not None:
        assert len(frames) <= arguments.max_animation_frames

assert expected_sizes == {}
