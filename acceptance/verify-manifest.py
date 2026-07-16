#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import pathlib
import sys
import tomllib

root = pathlib.Path(sys.argv[1]).resolve()
marker = tomllib.loads((root / ".viset").read_text())
manifest = tomllib.loads((root / "manifest.toml").read_text())

assert marker == {"version": 1, "owner": "viset", "manifest": "manifest.toml"}
assert manifest["version"] == 1
assert manifest["owner"] == "viset"
assert manifest["tool"]["name"] == "viset"
assert manifest["browser"]["version"]

files = manifest["files"]
assert [entry["path"] for entry in files] == [
    "screenshots/red.png",
    "screenshots/blue.png",
    "animations/motion.webp",
]

for entry in files:
    path = root / entry["path"]
    assert path.is_file(), path
    assert hashlib.sha256(path.read_bytes()).hexdigest() == entry["sha256"]

assert files[0]["frame_ticks_ms"] == []
assert files[1]["frame_ticks_ms"] == []
assert files[2]["frame_ticks_ms"] == [33, 34, 33, 33]
assert not (root / "SHA256SUMS").exists()
assert hashlib.sha256((root / "screenshots/red.png").read_bytes()).digest() != hashlib.sha256(
    (root / "screenshots/blue.png").read_bytes()
).digest()

webp = (root / "animations/motion.webp").read_bytes()
durations = []
offset = 0
while True:
    chunk = webp.find(b"ANMF", offset)
    if chunk < 0:
        break
    size = int.from_bytes(webp[chunk + 4 : chunk + 8], "little")
    payload = webp[chunk + 8 : chunk + 8 + size]
    durations.append(int.from_bytes(payload[12:15], "little"))
    offset = chunk + 8 + size

assert durations == [33, 34, 33, 33]
