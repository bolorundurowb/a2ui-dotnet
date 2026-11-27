"""Compare the .NET Express corpus report with a pinned Python peer."""

import json
from pathlib import Path
import sys

from a2ui.core.basic_catalog import BasicCatalog
from a2ui.inference_formats import to_message_dicts, to_message_models
from a2ui.inference_formats.express import ExpressCompiler, ExpressDecompiler


def equal_json(left, right):
    if isinstance(left, bool) or isinstance(right, bool):
        return left is right
    if isinstance(left, dict) and isinstance(right, dict):
        return left.keys() == right.keys() and all(equal_json(left[key], right[key]) for key in left)
    if isinstance(left, list) and isinstance(right, list):
        return len(left) == len(right) and all(equal_json(a, b) for a, b in zip(left, right))
    return left == right


def main():
    if len(sys.argv) != 3:
        raise SystemExit("Usage: Test-ExpressPeer.py <pinned-assets> <dotnet-report.json>")

    corpus = Path(sys.argv[1]) / "catalogs" / "basic" / "v1" / "examples"
    dotnet = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
    files = sorted(corpus.glob("*.json"))
    if not files or set(dotnet) != {file.name for file in files}:
        raise SystemExit("The .NET report must contain every pinned example.")

    catalog = BasicCatalog("1.0")
    compiler = ExpressCompiler([catalog])
    decompiler = ExpressDecompiler([catalog])
    for file in files:
        source = json.loads(file.read_text(encoding="utf-8"))["messages"]
        try:
            express = decompiler.decompile(to_message_models(source))
            messages = to_message_dicts(compiler.compile(express))
        except Exception as error:
            raise SystemExit(f"{file.name}: {error}") from error
        if not equal_json(dotnet[file.name], {"Express": express, "Messages": messages}):
            raise SystemExit(f"{file.name}: the Express text or ordered messages differ.")

    print(f"{len(files)} examples matched the Python peer in Express text and ordered messages with no diagnostics.")


if __name__ == "__main__":
    main()
