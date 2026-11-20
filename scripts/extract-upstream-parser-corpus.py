#!/usr/bin/env python3
"""
Extracts release-name test cases from the upstream *arr parser fixtures into Managerr's parser corpus.

  scripts/extract-upstream-parser-corpus.py [--upstream upstream] [--out tests/Managerr.Phase2.Tests/ParserCorpus]

Reads src/NzbDrone.Core.Test/ParserTests/*.cs of upstream/{Sonarr,Radarr,Lidarr} (the git submodules; a
sparse checkout of just that folder is enough) and writes one upstream-<repo>.jsonl per repository. Only
fixtures whose expectations map onto ParsedReleaseInfo are taken; see MAPPINGS. Every case gets an id
derived from its fixture, method and arguments, so ids survive upstream line moves and the known-gap
baseline (known-gaps.json) keeps matching after a re-sync.

The upstream fixtures are GPL-3.0, like Managerr.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any, Callable

FIXTURE_DIR = "src/NzbDrone.Core.Test/ParserTests"

# ---------------------------------------------------------------------------------------------------------
# Minimal C# attribute-argument reader: string/verbatim literals, numbers, null/bool, `new[] { ... }`,
# enum member access (kept as text) and named arguments (Description = ..., dropped).
# ---------------------------------------------------------------------------------------------------------


class Reader:
    def __init__(self, text: str, pos: int):
        self.t = text
        self.i = pos

    def ws(self) -> None:
        while self.i < len(self.t):
            if self.t[self.i].isspace():
                self.i += 1
            elif self.t.startswith("//", self.i):
                self.i = self.t.index("\n", self.i)
            elif self.t.startswith("/*", self.i):
                self.i = self.t.index("*/", self.i) + 2
            else:
                break

    def peek(self, s: str) -> bool:
        self.ws()
        return self.t.startswith(s, self.i)

    def expect(self, s: str) -> None:
        self.ws()
        if not self.t.startswith(s, self.i):
            raise ValueError(f"expected {s!r} at {self.t[self.i:self.i + 40]!r}")
        self.i += len(s)

    def string(self) -> str:
        if self.t[self.i] == "@":
            self.i += 2
            out = []
            while True:
                c = self.t[self.i]
                if c == '"':
                    if self.t[self.i + 1] == '"':
                        out.append('"')
                        self.i += 2
                        continue
                    self.i += 1
                    return "".join(out)
                out.append(c)
                self.i += 1
        self.i += 1
        out = []
        while True:
            c = self.t[self.i]
            if c == "\\":
                n = self.t[self.i + 1]
                if n == "u":
                    out.append(chr(int(self.t[self.i + 2:self.i + 6], 16)))
                    self.i += 6
                    continue
                out.append({"n": "\n", "t": "\t", "r": "\r", "0": "\0"}.get(n, n))
                self.i += 2
                continue
            if c == '"':
                self.i += 1
                return "".join(out)
            out.append(c)
            self.i += 1

    def value(self) -> Any:
        self.ws()
        t = self.t
        if t.startswith('"', self.i) or t.startswith('@"', self.i):
            s = self.string()
            # "a" + "b" concatenation
            while self.peek("+"):
                self.expect("+")
                self.ws()
                s += self.string()
            return s
        if t.startswith("new", self.i):
            self.i = t.index("{", self.i) + 1
            items = []
            while not self.peek("}"):
                items.append(self.value())
                if self.peek(","):
                    self.expect(",")
            self.expect("}")
            return items
        m = re.compile(r"-?\d+(?:\.\d+)?[dDfFmM]?").match(t, self.i)
        if m:
            self.i = m.end()
            raw = m.group(0).rstrip("dDfFmM")
            return float(raw) if "." in raw else int(raw)
        m = re.compile(r"[A-Za-z_][\w.]*").match(t, self.i)
        if m:
            self.i = m.end()
            word = m.group(0)
            return {"null": None, "true": True, "false": False}.get(word, word)
        raise ValueError(f"cannot read value at {t[self.i:self.i + 40]!r}")

    def args(self) -> list[Any]:
        self.expect("(")
        out = []
        while not self.peek(")"):
            if re.compile(r"\s*[A-Za-z_]\w*\s*=(?!=)").match(self.t, self.i):
                # Named argument (Description = "...", ExpectedResult = ...): not test data
                self.i = self.t.index("=", self.i) + 1
                self.value()
            else:
                out.append(self.value())
            if self.peek(","):
                self.expect(",")
        self.expect(")")
        return out


TESTCASE = re.compile(r"^[ \t]*\[TestCase\s*(?=\()", re.M)
METHOD = re.compile(r"public\s+void\s+(\w+)\s*\(")


def read_fixture(path: Path) -> list[tuple[str, int, list[Any]]]:
    """(method, line, args) for every active [TestCase(...)] in the file."""
    text = path.read_text(encoding="utf-8-sig")
    cases = []
    for m in TESTCASE.finditer(text):
        r = Reader(text, m.end())
        try:
            args = r.args()
        except (ValueError, IndexError) as ex:
            print(f"warn: {path.name}:{text.count(chr(10), 0, m.start()) + 1}: {ex}", file=sys.stderr)
            continue
        method = METHOD.search(text, r.i)
        if method:
            cases.append((method.group(1), text.count("\n", 0, m.start()) + 1, args))
    return cases


# ---------------------------------------------------------------------------------------------------------
# Upstream expectation -> corpus expectation
# ---------------------------------------------------------------------------------------------------------

Mapper = Callable[[list[Any]], "tuple[str, dict[str, Any]] | None"]

QUALITY_METHOD = re.compile(r"should_parse_(hdtv|webdl|webrip|bluray|remux)(480p|576p|720p|1080p|2160p)(_remux)?_quality")
QUALITY_SOURCE = {"hdtv": "HDTV", "webdl": "WEB-DL", "webrip": "WEBRip", "bluray": "BluRay", "remux": "Remux"}
QUALITY_RESOLUTION = {"480p": "480p", "576p": "480p", "720p": "720p", "1080p": "1080p", "2160p": "2160p"}

# Languages ParsedReleaseInfo.DetectedLanguages can name
LANGUAGE_METHODS = {
    "should_parse_language_english": ["en"],
    "should_parse_language_french": ["fr"],
    "should_parse_language_french_english": ["fr", "en"],
    "should_parse_language_spanish": ["es"],
    "should_parse_language_spanish_latino": ["es"],
    "should_parse_language_german": ["de"],
    "should_parse_language_italian": ["it"],
    "should_parse_language_japanese": ["ja"],
}

# Editions ParsedReleaseInfo.MovieEdition can name
EDITIONS = {
    "directors cut": "Director's Cut", "director's cut": "Director's Cut",
    "imax": "IMAX", "imax enhanced": "IMAX",
    "extended": "Extended", "extended edition": "Extended", "extended cut": "Extended",
    "criterion": "Criterion", "criterion collection": "Criterion",
    "unrated": "Unrated", "remastered": "Remastered",
}


def quality(method: str) -> Mapper | None:
    m = QUALITY_METHOD.fullmatch(method)
    if not m:
        return None
    source = "Remux" if m.group(3) else QUALITY_SOURCE[m.group(1)]
    return lambda a: (a[0], {"source": source, "resolution": QUALITY_RESOLUTION[m.group(2)]})


def language(method: str) -> Mapper | None:
    langs = LANGUAGE_METHODS.get(method)
    return (lambda a: (a[0], {"languagesInclude": langs})) if langs else None


def group(a: list[Any]):
    return a[0], {"group": a[1] if a[1] not in ("", None) else None}


def edition(a: list[Any]):
    mapped = EDITIONS.get(a[1].lower().strip())
    return (a[0], {"edition": mapped}) if mapped else None


YEAR_TOKEN = re.compile(r"\b(19\d{2}|20\d{2})\b")


def nonzero(v: Any) -> bool:
    return isinstance(v, int) and v > 0


def nonzero_year(v: Any) -> int | None:
    return v if isinstance(v, int) and 1900 <= v <= 2099 else None


def sonarr_episode(a: list[Any], multi: bool = False) -> tuple[str, dict[str, Any]]:
    title = strip_series_year(a[0], a[1])
    season = a[2]
    if isinstance(season, int) and 0 <= season <= 100:
        return a[0], {"title": title, "season": season, "episodes": a[3] if multi else [a[3]]}
    return a[0], {"title": title}


def sonarr_season_pack(a: list[Any]) -> tuple[str, dict[str, Any]]:
    title = strip_series_year(a[0], a[1])
    season = a[2]
    if isinstance(season, int) and 0 <= season <= 100:
        return a[0], {"title": title, "season": season, "seasonPack": True}
    return a[0], {"title": title}


def strip_series_year(release: str, title: str | None) -> str | None:
    """Managerr extracts the series year into ParsedReleaseInfo.Year and strips it from CleanTitle."""
    if not title:
        return title
    m = YEAR_TOKEN.search(release.replace("_", "."))
    if not m:
        return title
    yr = m.group(1)
    for suffix in (f" ({yr})", f" [{yr}]", f" - {yr}", f" {yr}"):
        if title.endswith(suffix) and len(title) > len(suffix):
            stripped = title[: -len(suffix)].rstrip(" -.([")
            if len(stripped) >= 2:
                return stripped
    return title


MAPPINGS: dict[str, dict[str, tuple[str, Mapper]]] = {
    "Sonarr": {
        "SingleEpisodeParserFixture.should_parse_single_episode": ("tv", lambda a: sonarr_episode(a, multi=False)),
        "SingleEpisodeParserFixture.should_parse_split_episode": ("tv", lambda a: sonarr_episode(a, multi=False)),
        "MultiEpisodeParserFixture.should_parse_multiple_episodes": ("tv", lambda a: sonarr_episode(a, multi=True)),
        "SeasonParserFixture.should_parse_full_season_release": ("tv", sonarr_season_pack),
        "AbsoluteEpisodeNumberParserFixture.should_parse_absolute_numbers": ("anime", lambda a: (a[0], {"title": strip_series_year(a[0], a[1]), "absolute": [a[2]]})),
        "AbsoluteEpisodeNumberParserFixture.should_parse_multi_episode_absolute_numbers": ("anime", lambda a: (a[0], {"title": strip_series_year(a[0], a[1]), "absolute": list(range(a[2], a[3] + 1))})),
        "AbsoluteEpisodeNumberParserFixture.should_parse_anime_season_packs": ("anime", sonarr_season_pack),
        "ReleaseGroupParserFixture.should_parse_release_group": ("tv", group),
        "ReleaseGroupParserFixture.should_parse_exception_release_group": ("tv", group),
        "ReleaseGroupParserFixture.should_not_include_language_in_release_group": ("tv", group),
        "ReleaseGroupParserFixture.should_not_include_repost_in_release_group": ("tv", group),
        "ReleaseGroupParserFixture.should_parse_anime_release_groups": ("anime", group),
        "QualityParserFixture.*": ("tv", quality),
        "LanguageParserFixture.*": ("tv", language),
    },
    "Radarr": {
        "ParserFixture.should_parse_movie_title": ("movies", lambda a: (a[0], {"title": a[1]})),
        "ParserFixture.should_parse_german_movie": ("movies", lambda a: (a[0], {"title": a[1], "year": nonzero_year(a[3])})),
        "ParserFixture.should_parse_anime_movie_title": ("movies", lambda a: (a[0], {"title": a[1], "group": a[2], "year": nonzero_year(a[3])})),
        "ParserFixture.should_parse_anime_movie_title_without_year": ("movies", lambda a: (a[0], {"title": a[1], "group": a[2]})),
        "ParserFixture.should_parse_movie_year": ("movies", lambda a: (a[0], {"year": nonzero_year(a[1])})),
        "ReleaseGroupParserFixture.should_parse_release_group": ("movies", group),
        "ReleaseGroupParserFixture.should_parse_exception_release_group": ("movies", group),
        "ReleaseGroupParserFixture.should_not_include_language_in_release_group": ("movies", group),
        "ReleaseGroupParserFixture.should_not_include_bad_suffix_in_release_group": ("movies", group),
        "ReleaseGroupParserFixture.should_parse_anime_release_groups": ("movies", group),
        "EditionParserFixture.should_parse_edition": ("movies", edition),
        "EditionParserFixture.should_not_parse_edition": ("movies", lambda a: (a[0], {"edition": None})),
        "QualityParserFixture.*": ("movies", quality),
        "LanguageParserFixture.*": ("movies", language),
    },
    "Lidarr": {
        "ParserFixture.should_parse_artist_name_and_album_title": (
            "music", lambda a: None if len(a) > 3 and a[3] else (a[0], {"author": a[1], "title": a[2]})),
        "ReleaseGroupParserFixture.should_parse_release_group": ("music", group),
        "ReleaseGroupParserFixture.should_not_include_language_in_release_group": ("music", group),
        "ReleaseGroupParserFixture.should_not_include_repost_in_release_group": ("music", group),
    },
}

# Inputs every upstream app treats as unparseable: they feed the invariant tests (never throw), not expectations
CRAP_FIXTURE = "CrapParserFixture"


def resolve(repo: str, fixture: str, method: str) -> tuple[str, Mapper] | None:
    table = MAPPINGS[repo]
    if f"{fixture}.{method}" in table:
        return table[f"{fixture}.{method}"]
    wildcard = table.get(f"{fixture}.*")
    if wildcard:
        mapper = wildcard[1](method)
        return (wildcard[0], mapper) if mapper else None
    return None


def case_id(repo: str, fixture: str, method: str, args: list[Any]) -> str:
    digest = hashlib.sha1(json.dumps([method, args], ensure_ascii=False).encode()).hexdigest()[:10]
    return f"{repo.lower()}/{fixture.removesuffix('Fixture')}/{digest}"


def extract(repo_root: Path, repo: str) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    seen: set[str] = set()
    for path in sorted((repo_root / FIXTURE_DIR).glob("*.cs")):
        fixture = path.stem
        for method, line, args in read_fixture(path):
            if fixture == CRAP_FIXTURE:
                if args and isinstance(args[0], str):
                    entry = {"id": case_id(repo, fixture, method, args), "release": args[0], "hint": None, "expect": {},
                             "origin": f"{repo}/{path.name}:{line}"}
                else:
                    continue
            else:
                rule = resolve(repo, fixture, method)
                if not rule or not args or not isinstance(args[0], str):
                    continue
                hint, mapper = rule
                mapped = mapper(args)
                if not mapped:
                    continue
                release, expect = mapped
                entry = {"id": case_id(repo, fixture, method, args), "release": release, "hint": hint, "expect": expect,
                         "origin": f"{repo}/{path.name}:{line} {method}"}
            if entry["id"] in seen:
                continue
            seen.add(entry["id"])
            out.append(entry)
    return out


def normalize_existing_jsonl(target: Path) -> int:
    lines = target.read_text(encoding="utf-8").splitlines()
    out: list[str] = []
    count = 0
    for line in lines:
        if not line.strip() or line.lstrip().startswith("//"):
            out.append(line)
            continue
        entry = json.loads(line)
        expect = entry.get("expect") or {}
        release = entry.get("release", "")
        if "title" in expect and entry.get("hint") in ("tv", "anime"):
            expect["title"] = strip_series_year(release, expect["title"])
        if "year" in expect:
            expect["year"] = nonzero_year(expect["year"])
        if "season" in expect and isinstance(expect["season"], int) and not (0 <= expect["season"] <= 100):
            expect.pop("season", None)
            expect.pop("episodes", None)
            expect.pop("seasonPack", None)
        out.append(json.dumps(entry, ensure_ascii=False))
        count += 1
    target.write_text("\n".join(out) + "\n", encoding="utf-8")
    return count


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--upstream", default="upstream", type=Path, help="folder holding Sonarr/, Radarr/, Lidarr/")
    ap.add_argument("--out", default="tests/Managerr.Phase2.Tests/ParserCorpus", type=Path)
    opts = ap.parse_args()

    for repo in MAPPINGS:
        root = opts.upstream / repo
        target = opts.out / f"upstream-{repo.lower()}.jsonl"
        if not (root / FIXTURE_DIR).is_dir():
            if target.is_file():
                count = normalize_existing_jsonl(target)
                print(f"{repo}: normalized {count} existing cases -> {target}")
                continue
            print(f"error: {root / FIXTURE_DIR} missing (git submodule update --init {root})", file=sys.stderr)
            return 1
        try:
            sha = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
        except (OSError, subprocess.CalledProcessError):
            sha = "unknown"
        entries = extract(root, repo)
        with target.open("w", encoding="utf-8", newline="\n") as f:
            f.write(f"// Generated by scripts/extract-upstream-parser-corpus.py from {repo}@{sha} {FIXTURE_DIR} (GPL-3.0). Do not edit by hand.\n")
            for e in entries:
                f.write(json.dumps(e, ensure_ascii=False) + "\n")
        print(f"{repo}: {len(entries)} cases -> {target}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
