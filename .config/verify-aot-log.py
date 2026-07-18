#!/usr/bin/env python3
from __future__ import annotations

import argparse
import pathlib
import re


ALLOWED_WARNINGS = {
    "IL2104": "Assembly 'FSharp.Core' produced trim warnings.",
    "IL3053": "Assembly 'FSharp.Core' produced AOT analysis warnings.",
}
DIAGNOSTIC = re.compile(r"\b(?P<severity>warning|error) (?P<code>[A-Z]+\d+): (?P<message>.*)")


parser = argparse.ArgumentParser()
parser.add_argument("log", type=pathlib.Path)
arguments = parser.parse_args()

failures: list[str] = []

for line in arguments.log.read_text(errors="replace").splitlines():
    diagnostic = DIAGNOSTIC.search(line)
    if diagnostic is None:
        continue

    severity = diagnostic.group("severity")
    code = diagnostic.group("code")
    message = diagnostic.group("message")

    if severity == "error":
        failures.append(line)
        continue

    allowed_message = ALLOWED_WARNINGS.get(code)
    source_is_fsharp_core = "fsharp.core" in line.casefold() and "fsharp.core.dll" in line.casefold()

    if allowed_message is None or not source_is_fsharp_core or not message.startswith(allowed_message):
        failures.append(line)

if failures:
    raise SystemExit("Unexpected Native AOT diagnostics:\n" + "\n".join(failures))
