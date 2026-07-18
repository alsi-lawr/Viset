#!/usr/bin/env python3
from __future__ import annotations

import pathlib
import re
import subprocess
import sys
from urllib.parse import unquote, urlsplit


LINK = re.compile(r"!?\[[^\]]*\]\(([^)]+)\)")
HTML_LINK = re.compile(r"(?:href|src)=\"([^\"]+)\"")
PUBLIC_TEXT = ("README.md", "CONTRIBUTING.md", "LICENSE", ".editorconfig")


def public_text_files(root: pathlib.Path) -> list[pathlib.Path]:
    files = [root / name for name in PUBLIC_TEXT]
    files.extend(sorted((root / "docs").rglob("*.md")))
    files.extend(sorted((root / "examples").glob("*/README.md")))
    return files


def markdown_files(root: pathlib.Path) -> list[pathlib.Path]:
    files = [root / "README.md", root / "CONTRIBUTING.md"]

    for directory in ("docs", "benchmarks"):
        files.extend(sorted((root / directory).rglob("*.md")))

    return files


def check_text(path: pathlib.Path, root: pathlib.Path) -> list[str]:
    relative = path.relative_to(root)

    if not path.is_file():
        return [f"missing documentation file: {relative}"]

    try:
        text = path.read_text(encoding="utf-8")
    except UnicodeDecodeError as error:
        return [f"{relative}: invalid UTF-8: {error}"]

    errors: list[str] = []

    if text and not text.endswith("\n"):
        errors.append(f"{relative}: missing final newline")

    for line_number, line in enumerate(text.splitlines(), start=1):
        if line.rstrip(" \t") != line:
            errors.append(f"{relative}:{line_number}: trailing whitespace")

        for character in line:
            if ord(character) > 127:
                errors.append(
                    f"{relative}:{line_number}: non-ASCII character U+{ord(character):04X}"
                )

    return errors


def tracked_paths(root: pathlib.Path) -> set[str] | None:
    if not (root / ".git").exists():
        return None

    result = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z"],
        check=True,
        capture_output=True,
    )
    return {
        entry.decode("utf-8")
        for entry in result.stdout.split(b"\0")
        if entry
    }


def check_links(
    path: pathlib.Path,
    root: pathlib.Path,
    tracked: set[str] | None,
) -> list[str]:
    relative = path.relative_to(root)
    text = path.read_text(encoding="utf-8")
    errors: list[str] = []

    destinations = [match.group(1).strip() for match in LINK.finditer(text)]
    destinations.extend(match.group(1).strip() for match in HTML_LINK.finditer(text))

    for destination in destinations:
        parsed = urlsplit(destination)

        if parsed.scheme or destination.startswith("#"):
            continue

        target_text = unquote(parsed.path)
        if not target_text:
            continue

        target = (path.parent / target_text).resolve()

        try:
            target.relative_to(root)
        except ValueError:
            errors.append(f"{relative}: link leaves repository: {destination}")
            continue

        if not target.exists():
            errors.append(f"{relative}: broken local link: {destination}")
        elif tracked is not None:
            target_relative = target.relative_to(root).as_posix()
            prefix = f"{target_relative}/"

            if target_relative not in tracked and not any(
                item.startswith(prefix) for item in tracked
            ):
                errors.append(f"{relative}: local link is not tracked: {destination}")

    return errors


def main() -> int:
    root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else pathlib.Path(__file__).parent.parent)
    root = root.resolve()
    errors: list[str] = []
    tracked = tracked_paths(root)

    for path in public_text_files(root):
        errors.extend(check_text(path, root))

    for path in markdown_files(root):
        errors.extend(check_links(path, root, tracked))

    if errors:
        for error in errors:
            print(error, file=sys.stderr)
        return 1

    print("documentation: UTF-8, ASCII, whitespace, and local links verified")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
