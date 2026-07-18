#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import pathlib


SYSTEMS = {"x86_64-linux", "aarch64-linux", "aarch64-darwin"}
OUTPUTS = {
    "apps": {"default", "viset"},
    "checks": {"cli", "documentation", "package"},
    "devShells": {"default"},
    "packages": {"default", "viset"},
}


parser = argparse.ArgumentParser()
parser.add_argument("--inventory", type=pathlib.Path, required=True)
parser.add_argument("--system", choices=sorted(SYSTEMS), required=True)
arguments = parser.parse_args()

inventory = json.loads(arguments.inventory.read_text(encoding="utf-8"))

for output, names in OUTPUTS.items():
    systems = inventory.get(output)
    if not isinstance(systems, dict) or set(systems) != SYSTEMS:
        raise RuntimeError(f"unexpected {output} systems: {systems}")

    for system, values in systems.items():
        if not isinstance(values, dict) or set(values) != names:
            raise RuntimeError(f"unexpected {output}.{system} outputs: {values}")

        for name, descriptor in values.items():
            if system == arguments.system:
                expected_type = "app" if output == "apps" else "derivation"
                if descriptor.get("type") != expected_type:
                    raise RuntimeError(
                        f"unexpected native {output}.{system}.{name} descriptor: {descriptor}"
                    )
            elif descriptor:
                print(f"evaluated non-native output: {output}.{system}.{name}")
            else:
                print(f"omitted non-native output: {output}.{system}.{name}")

formatters = inventory.get("formatter")
if not isinstance(formatters, dict) or set(formatters) != SYSTEMS:
    raise RuntimeError(f"unexpected formatter systems: {formatters}")

for system, descriptor in formatters.items():
    if system == arguments.system:
        if descriptor.get("type") != "derivation":
            raise RuntimeError(f"unexpected native formatter.{system} descriptor: {descriptor}")
    elif descriptor:
        print(f"evaluated non-native output: formatter.{system}")
    else:
        print(f"omitted non-native output: formatter.{system}")

print(f"flake inventory: {arguments.system} native outputs present")
