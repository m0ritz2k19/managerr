#!/usr/bin/env python3
"""Refresh the *arr compatibility contract used by tests/Managerr.Phase6.Tests/ArrCompat.

Two sources pin what Managerr's /compat/sonarr and /compat/radarr shims must look like:

  specs  The official Sonarr/Radarr v3 OpenAPI documents (src/<App>.Api.V3/openapi.json), copied from a
         checkout of the upstream repositories (the upstream/ submodules). Every compat route must exist
         there and every response is validated against its schema.

  seerr  The calls Seerr's servarr client makes (server/api/servarr/{base,sonarr,radarr}.ts), extracted into
         seerr-contract.json. Hand-written fields ("reads", "notes") survive a refresh; a call that is new in
         Seerr is added with "reads": null, which fails the tests until someone has reviewed it.

Usage:
  scripts/update-arr-compat-contract.py specs --upstream upstream
  scripts/update-arr-compat-contract.py seerr --seerr /path/to/seerr [--check]

--check writes nothing and exits 1 when Seerr's calls differ from the committed contract, so a scheduled job
can flag upstream drift before a user hits it.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "tests" / "Managerr.Phase6.Tests" / "ArrCompat"
CONTRACT = OUT / "seerr-contract.json"
APPS = {"sonarr": "Sonarr", "radarr": "Radarr"}
SEERR_FILES = {"base.ts": ["sonarr", "radarr"], "sonarr.ts": ["sonarr"], "radarr.ts": ["radarr"]}

METHOD_DECL = re.compile(
    r"^\s*(?:public|protected|private)?\s*(?:static\s+)?(?:async\s+)?(\w+)\s*(?:=\s*async\s*)?\([^)]*\)?[^;]*?(?:=>\s*)?\{?\s*$"
)
HTTP_CALL = re.compile(
    r"this\.(?:axios\.(get|post|put|delete)|(getRolling))\s*(?:<[^(]*?>)?\(\s*([`'])([^`']+)\3",
    re.S,
)
COMMAND_CALL = re.compile(r"runCommand\(\s*'(\w+)'\s*,\s*\{([^}]*)\}", re.S)
PARAMS = re.compile(r"params:\s*\{([^}]*)\}", re.S)
KEYWORDS = {"if", "for", "while", "switch", "catch", "return", "function", "constructor"}


def git_head(path: Path) -> str:
    return subprocess.run(["git", "-C", str(path), "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()


def cmd_specs(args: argparse.Namespace) -> int:
    for app, name in APPS.items():
        repo = Path(args.upstream) / name
        src = repo / "src" / f"{name}.Api.V3" / "openapi.json"
        spec = json.loads(src.read_text(encoding="utf-8"))
        spec["info"]["x-managerr-source"] = {
            "repository": f"https://github.com/{name}/{name}",
            "commit": git_head(repo),
            "file": f"src/{name}.Api.V3/openapi.json",
            "license": "GPL-3.0",
        }
        dest = OUT / "upstream" / f"{app}-v3.openapi.json"
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(json.dumps(spec, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"{dest.relative_to(ROOT)}: {len(spec['paths'])} paths at {spec['info']['x-managerr-source']['commit'][:10]}")
    return 0


def keys_of(block: str) -> list[str]:
    keys = []
    for part in block.split(","):
        m = re.match(r"\s*(\w+)\s*(?::|$)", part)
        if m:
            keys.append(m.group(1))
    return keys


def extract_calls(seerr: Path) -> dict[str, dict]:
    calls: dict[str, dict] = {}
    for file, apps in SEERR_FILES.items():
        text = (seerr / "server" / "api" / "servarr" / file).read_text(encoding="utf-8")
        lines = text.splitlines()
        offsets, pos = [], 0
        for line in lines:
            offsets.append(pos)
            pos += len(line) + 1

        def method_at(index: int) -> str:
            line_no = max(i for i, o in enumerate(offsets) if o <= index)
            for i in range(line_no, -1, -1):
                m = METHOD_DECL.match(lines[i])
                if m and m.group(1) not in KEYWORDS:
                    return m.group(1)
            return "?"

        def add(app: str, method: str, path: str, query: list[str], used_by: str, command: str | None = None) -> None:
            key = f"{app} {method} {path}" + (f" {command}" if command else "")
            entry = calls.setdefault(key, {
                "app": app, "method": method, "path": path,
                **({"command": command} if command else {}),
                "query": [], "usedBy": [],
            })
            entry["query"] = sorted(set(entry["query"]) | set(query))
            if used_by not in entry["usedBy"]:
                entry["usedBy"].append(used_by)
                entry["usedBy"].sort()

        for m in HTTP_CALL.finditer(text):
            verb = (m.group(1) or "get").upper()
            path = re.sub(r"\$\{[^}]+\}", "{id}", m.group(4))
            if path == "/command":
                continue  # runCommand's generic POST; each named command is listed below
            # the params object belongs to this call when it appears before the call's closing ");"
            tail = text[m.end(): m.end() + 400]
            close = tail.find(");")
            params = PARAMS.search(tail[: close if close >= 0 else len(tail)])
            query = keys_of(params.group(1)) if params else []
            for app in apps:
                add(app, verb, path, query, f"{file[:-3]}.{method_at(m.start())}")

        for m in COMMAND_CALL.finditer(text):
            if m.group(1) == "commandName":
                continue
            for app in apps:
                add(app, "POST", "/command", [], f"{file[:-3]}.{method_at(m.start())}", command=m.group(1))
    return calls


def contract_key(entry: dict) -> str:
    return f"{entry['app']} {entry['method']} {entry['path']}" + (f" {entry['command']}" if entry.get("command") else "")


def cmd_seerr(args: argparse.Namespace) -> int:
    seerr = Path(args.seerr)
    found = extract_calls(seerr)
    current = json.loads(CONTRACT.read_text(encoding="utf-8")) if CONTRACT.exists() else {"calls": []}
    old = {contract_key(c): c for c in current.get("calls", [])}

    merged = []
    for key in sorted(found):
        entry = found[key]
        prev = old.get(key, {})
        entry["reads"] = prev.get("reads")
        if prev.get("notes"):
            entry["notes"] = prev["notes"]
        merged.append(entry)

    contract = {
        "//": "Generated by scripts/update-arr-compat-contract.py seerr; edit only 'reads' and 'notes'. "
              "'reads' lists the response fields Seerr uses ([] = an array element, null = not reviewed yet).",
        "seerr": {"repository": "https://github.com/seerr-team/seerr", "commit": git_head(seerr), "license": "MIT"},
        "calls": merged,
    }

    added = sorted(set(found) - set(old))
    removed = sorted(set(old) - set(found))
    changed = sorted(k for k in set(found) & set(old)
                     if (found[k]["query"], found[k]["usedBy"]) != (old[k].get("query"), old[k].get("usedBy")))
    for label, keys in (("new in Seerr", added), ("no longer called", removed), ("changed", changed)):
        for k in keys:
            print(f"{label}: {k}")

    if args.check:
        drift = bool(added or removed or changed)
        print("Seerr contract is up to date" if not drift else "Seerr contract drifted; rerun without --check and review")
        return 1 if drift else 0

    CONTRACT.write_text(json.dumps(contract, indent=2) + "\n", encoding="utf-8")
    print(f"{CONTRACT.relative_to(ROOT)}: {len(merged)} calls at Seerr {contract['seerr']['commit'][:10]}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("specs")
    p.add_argument("--upstream", default=str(ROOT / "upstream"))
    p.set_defaults(func=cmd_specs)
    p = sub.add_parser("seerr")
    p.add_argument("--seerr", required=True)
    p.add_argument("--check", action="store_true")
    p.set_defaults(func=cmd_seerr)
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
