#!/usr/bin/env python3
from __future__ import annotations

import argparse
import pathlib
import subprocess
import tempfile


CAPTURE = """--[[
# viset
version = 1
output = "smoke.png"

[devices.desktop]

[devices.desktop.viewport]
width = 320
height = 240
]]

viset.page.navigate("data:text/html,<style>html{background:transparent}body{color:%23126;font:32px sans-serif}</style><h1>Viset</h1>")
viset.page.wait_for("document.readyState === 'complete'", "10s")
viset.snapshot()
"""


parser = argparse.ArgumentParser()
parser.add_argument("binary", type=pathlib.Path)
arguments = parser.parse_args()

with tempfile.TemporaryDirectory(prefix="viset-smoke-") as temporary:
    root = pathlib.Path(temporary)
    script = root / "capture.lua"
    output = root / "output"
    script.write_text(CAPTURE, encoding="utf-8")

    subprocess.run(
        [str(arguments.binary.resolve()), "capture", str(script), "--output", str(output)],
        check=True,
        timeout=90,
    )

    actual = sorted(path.relative_to(output).as_posix() for path in output.rglob("*") if path.is_file())
    if actual != ["smoke.png"]:
        raise RuntimeError(f"unexpected capture smoke inventory: {actual}")

    png = (output / "smoke.png").read_bytes()
    if not png.startswith(b"\x89PNG\r\n\x1a\n"):
        raise RuntimeError("capture smoke did not produce a PNG")
    dimensions = (int.from_bytes(png[16:20], "big"), int.from_bytes(png[20:24], "big"))
    if dimensions != (320, 240):
        raise RuntimeError(f"unexpected smoke dimensions: {dimensions}")

print("capture smoke: 320x240 PNG")
