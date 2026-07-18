#!/usr/bin/env python3

"""Measure repeated executions of a command with a monotonic wall clock."""

from __future__ import annotations

import argparse
import math
import shlex
import subprocess
import time


def percentile(samples: list[float], value: float) -> float:
    ordered = sorted(samples)
    index = max(0, math.ceil(value * len(ordered)) - 1)
    return ordered[index]


def run(command: list[str]) -> None:
    subprocess.run(
        command,
        check=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--samples", type=int, default=100)
    parser.add_argument("--warmup", type=int, default=5)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    arguments = parser.parse_args()

    command = arguments.command
    if command and command[0] == "--":
        command = command[1:]

    if arguments.samples <= 0 or arguments.warmup < 0 or not command:
        parser.error("use positive --samples, non-negative --warmup, and a command")

    for _ in range(arguments.warmup):
        run(command)

    durations: list[float] = []
    for _ in range(arguments.samples):
        started = time.perf_counter_ns()
        run(command)
        durations.append((time.perf_counter_ns() - started) / 1_000_000)

    print(f"command={shlex.join(command)}")
    print(
        " ".join(
            (
                f"samples={len(durations)}",
                f"mean_ms={sum(durations) / len(durations):.3f}",
                f"p50_ms={percentile(durations, 0.50):.3f}",
                f"p95_ms={percentile(durations, 0.95):.3f}",
                f"max_ms={max(durations):.3f}",
            )
        )
    )


if __name__ == "__main__":
    main()
