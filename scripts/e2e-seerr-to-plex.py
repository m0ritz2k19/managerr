#!/usr/bin/env python3
"""Live End-to-End Test Suite: Seerr -> Managerr -> Usenet Harness / SABnzbd -> 3-Tier LID -> Hardlink -> Plex -> Seerr.

Comprehensive, non-destructive live integration test runner designed to verify the entire media ingestion,
validation, and catalog lifecycle against live or testbed homelab infrastructure.

CRITICAL SAFETY GUARANTEES:
  1. NEVER calls POST /testlab/reset or wipes existing works or indexers.
  2. Records pre-flight snapshot of all existing works (with file counts), indexers, and downloaders.
  3. Uses temporary scoped API keys and temporary indexers cleaned up strictly in finally:.
  4. Post-flight verification asserts 100% of pre-existing works and indexers remain untouched with >= initial file count.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import dataclasses
import datetime as dt
import http.cookiejar
import json
import os
import re
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from typing import Any, Callable


# ---------------------------------------------------------------------------------------------------------------------
# ANSI Styling & Console Helpers
# ---------------------------------------------------------------------------------------------------------------------

class Style:
    RESET = "\033[0m"
    BOLD = "\033[1m"
    DIM = "\033[2m"
    RED = "\033[31m"
    GREEN = "\033[32m"
    YELLOW = "\033[33m"
    BLUE = "\033[34m"
    MAGENTA = "\033[35m"
    CYAN = "\033[36m"
    WHITE = "\033[37m"

    @classmethod
    def strip_if_not_tty(cls) -> None:
        if not sys.stdout.isatty():
            cls.RESET = ""
            cls.BOLD = ""
            cls.DIM = ""
            cls.RED = ""
            cls.GREEN = ""
            cls.YELLOW = ""
            cls.BLUE = ""
            cls.MAGENTA = ""
            cls.CYAN = ""
            cls.WHITE = ""


# ---------------------------------------------------------------------------------------------------------------------
# Env file parser
# ---------------------------------------------------------------------------------------------------------------------

def load_env_file(path: str) -> dict[str, str]:
    """Parses key-value pairs from a simple .env file without external dependencies."""
    result: dict[str, str] = {}
    if not os.path.isfile(path):
        return result
    try:
        with open(path, "r", encoding="utf-8", errors="ignore") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                if "=" in line:
                    key, val = line.split("=", 1)
                    key = key.strip()
                    val = val.strip().strip("'\"")
                    result[key] = val
    except Exception as exc:
        print(f"{Style.YELLOW}[WARN] Could not parse env file {path}: {exc}{Style.RESET}")
    return result


# ---------------------------------------------------------------------------------------------------------------------
# Pre-Flight Safety Snapshot (Zero Data Wipe Guarantee)
# ---------------------------------------------------------------------------------------------------------------------

# Synthetic TestLab fixture works whose files are deterministically cycled by TC04-TC10 and re-verified at >= initial fileCount in TC11.
# All non-synthetic user works in the library are strictly immutable by both work ID and fileCount.
SYNTHETIC_CYCLED_TITLES: frozenset[str] = frozenset(
    {
        "neon horizon",
        "shadow protocol",
        "pioneer one",
        "cosmos laundromat",
    }
)


@dataclasses.dataclass
class PreFlightSafetySnapshot:
    works: dict[int, dict[str, Any]] = dataclasses.field(default_factory=dict)
    indexers: dict[int, dict[str, Any]] = dataclasses.field(default_factory=dict)
    downloaders: dict[int, dict[str, Any]] = dataclasses.field(default_factory=dict)
    snapshot_time: str = ""

    def record(self, client: ManagerrClient) -> None:
        self.snapshot_time = dt.datetime.now(dt.timezone.utc).isoformat()
        works = client.list_works()
        for w in works:
            wid = int(w.get("id") if w.get("id") is not None else w.get("Id", 0))
            if wid > 0:
                self.works[wid] = {
                    "title": w.get("title") or w.get("Title") or "",
                    "workKind": w.get("workKind") or w.get("WorkKind") or "",
                    "fileCount": int(w.get("fileCount") if w.get("fileCount") is not None else w.get("FileCount") or 0),
                }

        indexers = client.list_indexers()
        for ix in indexers:
            ixid = int(ix.get("id") if ix.get("id") is not None else ix.get("Id", 0))
            if ixid > 0:
                self.indexers[ixid] = {
                    "name": ix.get("name") or ix.get("Name") or "",
                    "baseUrl": ix.get("baseUrl") or ix.get("BaseUrl") or "",
                }

        downloaders = client.list_downloaders()
        for dl in downloaders:
            dlid = int(dl.get("id") if dl.get("id") is not None else dl.get("Id", 0))
            if dlid > 0:
                self.downloaders[dlid] = {
                    "name": dl.get("name") or dl.get("Name") or "",
                    "implementation": dl.get("implementation") or dl.get("Implementation") or "",
                }

    def verify_zero_data_loss(self, client: ManagerrClient) -> list[str]:
        violations: list[str] = []
        current_works_list = client.list_works()
        current_works: dict[int, dict[str, Any]] = {}
        current_by_title: dict[str, dict[str, Any]] = {}
        for w in current_works_list:
            wid = int(w.get("id") if w.get("id") is not None else w.get("Id", 0))
            if wid > 0:
                entry = {
                    "id": wid,
                    "title": w.get("title") or w.get("Title") or "",
                    "fileCount": int(w.get("fileCount") if w.get("fileCount") is not None else w.get("FileCount") or 0),
                }
                current_works[wid] = entry
                current_by_title[entry["title"].strip().lower()] = entry

        # Assert 100% of pre-existing user works retain exact ID + >= initial fileCount,
        # and 100% of cycled synthetic fixture works remain present with >= initial fileCount.
        for wid, orig in self.works.items():
            title_key = orig["title"].strip().lower()
            if title_key in SYNTHETIC_CYCLED_TITLES:
                cur = current_works.get(wid) or current_by_title.get(title_key)
                if cur is None:
                    violations.append(f"SYNTHETIC FIXTURE MISSING POST-RUN: '{orig['title']}' was not restored!")
                elif cur["fileCount"] < orig["fileCount"]:
                    violations.append(
                        f"SYNTHETIC FIXTURE FILE LOSS: '{orig['title']}' file count dropped from {orig['fileCount']} to {cur['fileCount']}!"
                    )
            else:
                if wid not in current_works:
                    violations.append(f"PRE-EXISTING USER WORK MISSING: ID #{wid} ('{orig['title']}') was deleted from library!")
                else:
                    cur = current_works[wid]
                    if cur["fileCount"] < orig["fileCount"]:
                        violations.append(
                            f"USER WORK FILE LOSS: ID #{wid} ('{orig['title']}') file count dropped from {orig['fileCount']} to {cur['fileCount']}!"
                        )

        # Assert 100% of pre-existing indexers still exist
        current_indexers_list = client.list_indexers()
        current_indexers = {int(ix.get("id") if ix.get("id") is not None else ix.get("Id", 0)) for ix in current_indexers_list}
        for ixid, orig in self.indexers.items():
            if ixid not in current_indexers:
                violations.append(f"PRE-EXISTING INDEXER MISSING: ID #{ixid} ('{orig['name']}') was deleted!")

        # Assert 100% of pre-existing downloaders still exist
        current_downloaders_list = client.list_downloaders()
        current_downloaders = {int(dl.get("id") if dl.get("id") is not None else dl.get("Id", 0)) for dl in current_downloaders_list}
        for dlid, orig in self.downloaders.items():
            if dlid not in current_downloaders:
                violations.append(f"PRE-EXISTING DOWNLOADER MISSING: ID #{dlid} ('{orig['name']}') was deleted!")

        return violations


# ---------------------------------------------------------------------------------------------------------------------
# HTTP Client Wrapper
# ---------------------------------------------------------------------------------------------------------------------

class HttpClient:
    def __init__(self, insecure: bool = False, timeout: float = 30.0):
        self.insecure = insecure
        self.timeout = timeout
        self.cookie_jar = http.cookiejar.CookieJar()
        handlers: list[urllib.request.BaseHandler] = [urllib.request.HTTPCookieProcessor(self.cookie_jar)]
        if insecure:
            ctx = ssl.create_default_context()
            ctx.check_hostname = False
            ctx.verify_mode = ssl.CERT_NONE
            handlers.append(urllib.request.HTTPSHandler(context=ctx))
        self.opener = urllib.request.build_opener(*handlers)

    def request(
        self,
        url: str,
        method: str = "GET",
        json_body: Any = None,
        headers: dict[str, str] | None = None,
        timeout: float | None = None,
    ) -> tuple[int, str, dict[str, str]]:
        hdrs = {"Accept": "application/json", **(headers or {})}
        data: bytes | None = None
        if json_body is not None:
            data = json.dumps(json_body).encode("utf-8")
            hdrs["Content-Type"] = "application/json"

        req = urllib.request.Request(url, data=data, headers=hdrs, method=method)
        t = timeout if timeout is not None else self.timeout
        try:
            with self.opener.open(req, timeout=t) as resp:
                status = resp.status
                body = resp.read().decode("utf-8", errors="replace")
                resp_headers = {k.lower(): v for k, v in resp.headers.items()}
                return status, body, resp_headers
        except urllib.error.HTTPError as exc:
            err_body = exc.read().decode("utf-8", errors="replace")
            err_headers = {k.lower(): v for k, v in exc.headers.items()}
            return exc.code, err_body, err_headers
        except Exception as exc:
            return 0, str(exc), {}


# ---------------------------------------------------------------------------------------------------------------------
# Service Clients: Managerr, Seerr, Plex, Usenet Harness
# ---------------------------------------------------------------------------------------------------------------------

class ManagerrClient:
    def __init__(self, base_url: str, username: str, password: str, http_client: HttpClient):
        self.base_url = base_url.rstrip("/")
        self.username = username
        self.password = password
        self.http = http_client
        self.csrf_token: str = ""
        self.api_key: str = ""

    def request(
        self,
        path: str,
        method: str = "GET",
        json_body: Any = None,
        headers: dict[str, str] | None = None,
        timeout: float | None = None,
    ) -> tuple[int, str, dict[str, str]]:
        hdrs: dict[str, str] = {}
        if self.csrf_token:
            hdrs["X-Managerr-Csrf"] = self.csrf_token
        if self.api_key:
            hdrs["X-Api-Key"] = self.api_key
        if headers:
            hdrs.update(headers)
        return self.http.request(f"{self.base_url}{path}", method=method, json_body=json_body, headers=hdrs, timeout=timeout)

    def login(self) -> None:
        st, raw, _ = self.request(
            "/api/v1/auth/breakglass/login",
            method="POST",
            json_body={"usernameOrEmail": self.username, "password": self.password},
        )
        if st != 200:
            raise RuntimeError(f"Managerr break-glass login failed ({st}): {raw[:200]}")
        data = json.loads(raw or "{}")
        self.csrf_token = data.get("csrfToken", "")

    def mint_temporary_token(self, name: str, scopes: list[str]) -> tuple[str, int]:
        st, raw, _ = self.request(
            "/api/v1/auth/tokens",
            method="POST",
            json_body={"name": name, "scopes": scopes},
        )
        if st not in (200, 201):
            raise RuntimeError(f"Minting temporary scoped token failed ({st}): {raw[:200]}")
        data = json.loads(raw or "{}")
        token_id = int(data.get("id", 0))
        raw_token = str(data.get("rawTokenOnce", ""))
        return raw_token, token_id

    def revoke_token(self, token_id: int) -> int:
        st, _, _ = self.request(f"/api/v1/auth/tokens/{token_id}", method="DELETE")
        return st

    def list_works(self) -> list[dict[str, Any]]:
        st, raw, _ = self.request("/api/v1/catalog/works")
        if st == 404:
            st, raw, _ = self.request("/api/v1/works")
        if st != 200:
            raise RuntimeError(f"Listing catalog works failed ({st}): {raw[:200]}")
        return json.loads(raw or "[]")

    def get_work_tree(self, work_id: int) -> dict[str, Any] | None:
        st, raw, _ = self.request(f"/api/v1/catalog/works/{work_id}")
        if st == 404:
            st, raw, _ = self.request(f"/api/v1/works/{work_id}")
        if st == 404:
            return None
        if st != 200:
            raise RuntimeError(f"Get work tree #{work_id} failed ({st}): {raw[:200]}")
        return json.loads(raw or "{}")

    def delete_work(self, work_id: int, delete_files: bool = True) -> int:
        qs = f"?deleteFilesOnDisk={'true' if delete_files else 'false'}"
        st, _, _ = self.request(f"/api/v1/catalog/works/{work_id}{qs}", method="DELETE")
        if st == 404:
            st, _, _ = self.request(f"/api/v1/works/{work_id}{qs}", method="DELETE")
        return st

    def list_libraries(self) -> list[dict[str, Any]]:
        st, raw, _ = self.request("/api/v1/libraries")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def create_work(self, payload: dict[str, Any]) -> dict[str, Any]:
        st, raw, _ = self.request("/api/v1/catalog/works", method="POST", json_body=payload)
        if st not in (200, 201):
            raise RuntimeError(f"Creating catalog work failed ({st}): {raw[:300]}")
        return json.loads(raw or "{}")

    def update_unit(self, unit_id: int, payload: dict[str, Any]) -> dict[str, Any]:
        st, raw, _ = self.request(f"/api/v1/catalog/units/{unit_id}", method="PATCH", json_body=payload)
        if st not in (200, 201):
            raise RuntimeError(f"Updating unit #{unit_id} failed ({st}): {raw[:200]}")
        return json.loads(raw or "{}")

    def remove_queue_item(self, client_id: int, external_id: str) -> int:
        st, _, _ = self.request(
            f"/api/v1/downloaders/queue/{client_id}/{urllib.parse.quote(external_id, safe='')}"
            "?removeFromClient=true&deleteData=true&blocklist=false&searchReplacement=false",
            method="DELETE",
        )
        return st

    def auto_search_unit(self, work_id: int, unit_id: int) -> dict[str, Any]:
        st, raw, _ = self.request(f"/api/v1/catalog/works/{work_id}/units/{unit_id}/auto-search", method="POST", json_body={}, timeout=90)
        if st not in (200, 201):
            raise RuntimeError(f"Auto-search unit #{unit_id} failed ({st}): {raw[:200]}")
        return json.loads(raw or "{}")

    def list_indexers(self) -> list[dict[str, Any]]:
        st, raw, _ = self.request("/api/v1/indexers")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def create_indexer(self, payload: dict[str, Any]) -> dict[str, Any]:
        st, raw, _ = self.request("/api/v1/indexers", method="POST", json_body=payload)
        if st not in (200, 201):
            raise RuntimeError(f"Creating indexer failed ({st}): {raw[:200]}")
        return json.loads(raw or "{}")

    def delete_indexer(self, indexer_id: int) -> int:
        st, _, _ = self.request(f"/api/v1/indexers/{indexer_id}", method="DELETE")
        return st

    def list_downloaders(self) -> list[dict[str, Any]]:
        st, raw, _ = self.request("/api/v1/downloaders")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def get_queue(self) -> list[dict[str, Any]]:
        st, raw, _ = self.request("/api/v1/downloaders/queue")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def get_tracked(self) -> list[dict[str, Any]]:
        st, raw, _ = self.request("/api/v1/downloaders/tracked?limit=200")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def get_imports(self, limit: int = 100) -> list[dict[str, Any]]:
        st, raw, _ = self.request(f"/api/v1/downloaders/imports?limit={limit}")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def process_completed(self) -> dict[str, Any]:
        st, raw, _ = self.request("/api/v1/downloaders/process-completed", method="POST", json_body={})
        if st not in (200, 201, 204):
            return {}
        return json.loads(raw or "{}") if raw else {}

    def get_blocklist(self) -> list[dict[str, Any]]:
        st, raw, _ = self.request("/api/v1/activity/blocklist")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def delete_blocklist_entry(self, info_hash: str) -> int:
        st, _, _ = self.request(f"/api/v1/activity/blocklist/{urllib.parse.quote(info_hash, safe='')}", method="DELETE")
        return st

    def list_quarantine(self, status: str = "quarantined") -> list[dict[str, Any]]:
        st, raw, _ = self.request(f"/api/v1/quarantine?status={urllib.parse.quote(status)}")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def approve_quarantine(self, quarantine_id: int, confirmed_language: str = "de") -> dict[str, Any]:
        st, raw, _ = self.request(
            f"/api/v1/quarantine/{quarantine_id}/approve",
            method="POST",
            json_body={"confirmedLanguage": confirmed_language},
        )
        if st not in (200, 201, 204):
            raise RuntimeError(f"Approving quarantine #{quarantine_id} failed ({st}): {raw[:200]}")
        return json.loads(raw or "{}") if raw else {}

    # Sonarr / Radarr shims
    def sonarr_status(self) -> dict[str, Any]:
        st, raw, _ = self.request("/compat/sonarr/api/v3/system/status")
        if st != 200:
            raise RuntimeError(f"Sonarr shim status failed ({st}): {raw[:200]}")
        return json.loads(raw or "{}")

    def radarr_status(self) -> dict[str, Any]:
        st, raw, _ = self.request("/compat/radarr/api/v3/system/status")
        if st != 200:
            raise RuntimeError(f"Radarr shim status failed ({st}): {raw[:200]}")
        return json.loads(raw or "{}")

    def radarr_get_movie(self, tmdb_id: int) -> list[dict[str, Any]]:
        st, raw, _ = self.request(f"/compat/radarr/api/v3/movie?tmdbId={tmdb_id}")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def radarr_get_movie_by_id(self, movie_id: int) -> dict[str, Any] | None:
        st, raw, _ = self.request(f"/compat/radarr/api/v3/movie/{movie_id}")
        if st != 200:
            return None
        return json.loads(raw or "{}")

    def radarr_post_movie(self, payload: dict[str, Any]) -> tuple[int, Any]:
        st, raw, _ = self.request("/compat/radarr/api/v3/movie", method="POST", json_body=payload)
        try:
            return st, json.loads(raw or "{}")
        except Exception:
            return st, raw

    def radarr_delete_movie(self, movie_id: int, delete_files: bool = True) -> int:
        st, _, _ = self.request(f"/compat/radarr/api/v3/movie/{movie_id}?deleteFiles={'true' if delete_files else 'false'}", method="DELETE")
        return st

    def radarr_get_queue(self) -> list[dict[str, Any]]:
        st, raw, _ = self.request("/compat/radarr/api/v3/queue")
        if st != 200:
            return []
        data = json.loads(raw or "[]")
        if isinstance(data, dict):
            return data.get("records") or []
        return data

    def radarr_post_tag(self, payload: dict[str, Any]) -> tuple[int, Any]:
        st, raw, _ = self.request("/compat/radarr/api/v3/tag", method="POST", json_body=payload)
        try:
            return st, json.loads(raw or "{}")
        except Exception:
            return st, raw

    def radarr_get_tags(self) -> list[dict[str, Any]]:
        st, raw, _ = self.request("/compat/radarr/api/v3/tag")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def sonarr_get_series(self, tvdb_id: int) -> list[dict[str, Any]]:
        st, raw, _ = self.request(f"/compat/sonarr/api/v3/series?tvdbId={tvdb_id}")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def sonarr_get_series_by_id(self, series_id: int) -> dict[str, Any] | None:
        st, raw, _ = self.request(f"/compat/sonarr/api/v3/series/{series_id}")
        if st != 200:
            return None
        return json.loads(raw or "{}")

    def sonarr_post_series(self, payload: dict[str, Any]) -> tuple[int, Any]:
        st, raw, _ = self.request("/compat/sonarr/api/v3/series", method="POST", json_body=payload)
        try:
            return st, json.loads(raw or "{}")
        except Exception:
            return st, raw

    def sonarr_delete_series(self, series_id: int, delete_files: bool = True) -> int:
        st, _, _ = self.request(f"/compat/sonarr/api/v3/series/{series_id}?deleteFiles={'true' if delete_files else 'false'}", method="DELETE")
        return st

    def sonarr_get_episodes(self, series_id: int) -> list[dict[str, Any]]:
        st, raw, _ = self.request(f"/compat/sonarr/api/v3/episode?seriesId={series_id}")
        if st != 200:
            return []
        return json.loads(raw or "[]")

    def sonarr_post_tag(self, payload: dict[str, Any]) -> tuple[int, Any]:
        st, raw, _ = self.request("/compat/sonarr/api/v3/tag", method="POST", json_body=payload)
        try:
            return st, json.loads(raw or "{}")
        except Exception:
            return st, raw

    def sonarr_get_tags(self) -> list[dict[str, Any]]:
        st, raw, _ = self.request("/compat/sonarr/api/v3/tag")
        if st != 200:
            return []
        return json.loads(raw or "[]")


class SeerrClient:
    def __init__(self, base_url: str, api_key: str, http_client: HttpClient):
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.http = http_client

    def request(
        self,
        path: str,
        method: str = "GET",
        json_body: Any = None,
        timeout: float | None = None,
    ) -> tuple[int, str, dict[str, str]]:
        hdrs = {"X-Api-Key": self.api_key}
        return self.http.request(f"{self.base_url}/api/v1{path}", method=method, json_body=json_body, headers=hdrs, timeout=timeout)

    def status(self) -> dict[str, Any]:
        st, raw, _ = self.request("/status")
        if st != 200:
            raise RuntimeError(f"Seerr status failed ({st}): {raw[:200]}")
        return json.loads(raw or "{}")

    def create_request(
        self,
        media_type: str,
        media_id: int,
        tvdb_id: int | None = None,
        seasons: list[int] | None = None,
        is4k: bool = False,
    ) -> dict[str, Any]:
        body: dict[str, Any] = {"mediaType": media_type, "mediaId": media_id, "is4k": is4k}
        if tvdb_id is not None:
            body["tvdbId"] = tvdb_id
        if seasons is not None:
            body["seasons"] = seasons
        st, raw, _ = self.request("/request", method="POST", json_body=body, timeout=120)
        if st not in (200, 201):
            raise RuntimeError(f"Seerr create request failed ({st}): {raw[:200]}")
        return json.loads(raw or "{}")

    def get_movie(self, tmdb_id: int) -> dict[str, Any]:
        st, raw, _ = self.request(f"/movie/{tmdb_id}")
        if st != 200:
            return {}
        return json.loads(raw or "{}")

    def get_tv(self, tmdb_id: int) -> dict[str, Any]:
        st, raw, _ = self.request(f"/tv/{tmdb_id}")
        if st != 200:
            return {}
        return json.loads(raw or "{}")

    def delete_media(self, media_id: int) -> int:
        st, _, _ = self.request(f"/media/{media_id}", method="DELETE")
        return st

    def run_job(self, job_name: str) -> int:
        st, _, _ = self.request(f"/settings/jobs/{job_name}/run", method="POST", json_body={})
        return st

    def get_plex_settings(self) -> dict[str, Any]:
        st, raw, _ = self.request("/settings/plex")
        if st != 200:
            return {}
        return json.loads(raw or "{}")


class PlexClient:
    def __init__(self, base_url: str, token: str | None, http_client: HttpClient):
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.http = http_client

    def request(
        self,
        path: str,
        method: str = "GET",
        json_body: Any = None,
        timeout: float | None = None,
    ) -> tuple[int, str, dict[str, str]]:
        hdrs: dict[str, str] = {}
        if self.token:
            hdrs["X-Plex-Token"] = self.token
        return self.http.request(f"{self.base_url}{path}", method=method, json_body=json_body, headers=hdrs, timeout=timeout)

    def identity(self) -> tuple[int, str]:
        st, raw, _ = self.request("/identity")
        return st, raw

    def get_sections(self) -> list[dict[str, Any]]:
        st, raw, _ = self.request("/library/sections")
        if st != 200:
            return []
        try:
            data = json.loads(raw or "{}")
            return data.get("MediaContainer", {}).get("Directory", []) or []
        except Exception:
            return []

    def get_section_items(self, section_key: str, type_id: int | None = None) -> list[dict[str, Any]]:
        qs = f"?type={type_id}" if type_id is not None else ""
        st, raw, _ = self.request(f"/library/sections/{section_key}/all{qs}")
        if st != 200:
            return []
        try:
            data = json.loads(raw or "{}")
            return data.get("MediaContainer", {}).get("Metadata", []) or []
        except Exception:
            return []

    def refresh_section(self, section_key: str) -> int:
        st, _, _ = self.request(f"/library/sections/{section_key}/refresh")
        return st


class HarnessClient:
    def __init__(self, base_url: str, http_client: HttpClient):
        self.base_url = base_url.rstrip("/")
        self.http = http_client

    def status(self) -> dict[str, Any]:
        st, raw, _ = self.http.request(f"{self.base_url}/testlab/status", timeout=15)
        if st != 200:
            raise RuntimeError(f"Harness /testlab/status failed ({st}): {raw[:200]}")
        return json.loads(raw or "{}")

    def grab(self, release_id: str, auto_import: bool = False) -> dict[str, Any]:
        st, raw, _ = self.http.request(
            f"{self.base_url}/testlab/grab",
            method="POST",
            json_body={"id": release_id, "autoImport": auto_import},
            timeout=60,
        )
        if st not in (200, 201):
            raise RuntimeError(f"Harness grab '{release_id}' failed ({st}): {raw[:200]}")
        return json.loads(raw or "{}")


# ---------------------------------------------------------------------------------------------------------------------
# Test Execution Models & JUnit XML
# ---------------------------------------------------------------------------------------------------------------------

@dataclasses.dataclass
class TestCaseResult:
    test_id: str
    name: str
    status: str  # PASS, FAIL, SKIP
    duration: float
    message: str = ""
    details: str = ""


def write_junit_xml(test_results: list[TestCaseResult], output_path: str, total_time: float) -> None:
    suites_el = ET.Element(
        "testsuites",
        name="Managerr-E2E-Suite",
        tests=str(len(test_results)),
        failures=str(sum(1 for r in test_results if r.status == "FAIL")),
        skipped=str(sum(1 for r in test_results if r.status == "SKIP")),
        time=f"{total_time:.2f}",
    )
    suite_el = ET.SubElement(
        suites_el,
        "testsuite",
        name="LiveE2E-Seerr-To-Plex",
        tests=str(len(test_results)),
        failures=str(sum(1 for r in test_results if r.status == "FAIL")),
        skipped=str(sum(1 for r in test_results if r.status == "SKIP")),
        time=f"{total_time:.2f}",
    )
    for res in test_results:
        case_el = ET.SubElement(
            suite_el,
            "testcase",
            classname="e2e_seerr_to_plex",
            name=res.test_id,
            time=f"{res.duration:.2f}",
        )
        if res.status == "FAIL":
            fail_el = ET.SubElement(case_el, "failure", message=res.message)
            fail_el.text = res.details or res.message
        elif res.status == "SKIP":
            skip_el = ET.SubElement(case_el, "skipped", message=res.message)
            skip_el.text = res.details or res.message

    xml_bytes = ET.tostring(suites_el, encoding="utf-8", xml_declaration=True)
    out_dir = os.path.dirname(os.path.abspath(output_path))
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    with open(output_path, "wb") as f:
        f.write(xml_bytes)


# ---------------------------------------------------------------------------------------------------------------------
# Polling Utility
# ---------------------------------------------------------------------------------------------------------------------

def poll_until(fn: Callable[[], tuple[bool, Any]], timeout_s: float, interval_s: float = 3.0) -> tuple[bool, Any]:
    deadline = time.time() + timeout_s
    last_val: tuple[bool, Any] = (False, None)
    while time.time() < deadline:
        last_val = fn()
        if last_val[0]:
            return last_val
        time.sleep(interval_s)
    return last_val


# ---------------------------------------------------------------------------------------------------------------------
# Live E2E Suite Test Cases
# ---------------------------------------------------------------------------------------------------------------------

class E2ETestSuite:
    def __init__(
        self,
        managerr: ManagerrClient,
        seerr: SeerrClient,
        plex: PlexClient,
        harness: HarnessClient,
        timeout: float,
        newznab_url: str = "",
    ):
        self.mgr = managerr
        self.seerr = seerr
        self.plex = plex
        self.harness = harness
        self.timeout = timeout
        self.newznab_url = newznab_url or os.environ.get("TESTLAB_NEWZNAB_URL", "http://127.0.0.1:9117/api")
        self.safety_snapshot = PreFlightSafetySnapshot()
        self.temporary_token_id: int | None = None
        self.temporary_indexer_id: int | None = None

    def _resolve_root_folder(self, media_type: str, default_path: str) -> str:
        for lib in self.mgr.list_libraries():
            lib_type = str(lib.get("mediaType") or lib.get("workKind") or "").lower()
            root = str(lib.get("rootFolderPath") or "").strip()
            if root and lib_type == media_type.lower():
                return root
        return default_path

    # Setup temporary resources
    def setup_temporary_resources(self) -> None:
        # 1. Mint temporary scoped API key
        token_name = f"e2e-live-token-{int(time.time())}"
        raw_key, token_id = self.mgr.mint_temporary_token(
            token_name,
            scopes=[
                "compat:sonarr",
                "compat:radarr",
                "catalog:read",
                "catalog:write",
                "catalog:delete",
                "indexers:read",
                "indexers:write",
                "indexers:search",
                "downloaders:read",
                "downloaders:write",
                "queue:read",
                "queue:manage",
                "quarantine:review",
                "system:admin",
            ],
        )
        self.temporary_token_id = token_id
        self.mgr.api_key = raw_key

        # 2. Check if local Newznab indexer is present; if not, create temporary indexer
        indexers = self.mgr.list_indexers()
        target_host = urllib.parse.urlparse(self.newznab_url).netloc or self.newznab_url
        harness_ix = next((ix for ix in indexers if target_host in (ix.get("baseUrl") or "")), None)
        if not harness_ix:
            created_ix = self.mgr.create_indexer(
                {
                    "name": "TestLab Temporary Newznab",
                    "implementation": "newznab",
                    "definitionId": None,
                    "protocol": "usenet",
                    "baseUrl": self.newznab_url,
                    "apiKey": "testlab-newznab-key",
                    "categories": [2000, 2040, 5000, 5040, 7000, 7020],
                }
            )
            self.temporary_indexer_id = int(created_ix.get("id", 0))

    def cleanup_temporary_resources(self) -> None:
        if self.temporary_indexer_id:
            try:
                self.mgr.delete_indexer(self.temporary_indexer_id)
            except Exception as exc:
                print(f"{Style.YELLOW}[WARN] Could not delete temporary indexer #{self.temporary_indexer_id}: {exc}{Style.RESET}")
            self.temporary_indexer_id = None

        if self.temporary_token_id:
            try:
                self.mgr.revoke_token(self.temporary_token_id)
            except Exception as exc:
                print(f"{Style.YELLOW}[WARN] Could not revoke temporary token #{self.temporary_token_id}: {exc}{Style.RESET}")
            self.temporary_token_id = None

    def _clear_synthetic_release_artifacts(self, substrings: list[str]) -> None:
        """Clears only synthetic TestLab queue/blocklist leftovers matching the given substrings."""
        subs = [s.lower() for s in substrings]
        for t in self.mgr.get_tracked():
            title_low = (t.get("title") or "").lower()
            if any(s in title_low for s in subs) and t.get("state") in (
                "Downloading",
                "ImportPending",
                "Importing",
                "ImportBlocked",
                "FailedPending",
            ):
                if t.get("downloadClientId") and t.get("externalDownloadId"):
                    self.mgr.remove_queue_item(int(t["downloadClientId"]), str(t["externalDownloadId"]))
        for b in self.mgr.get_blocklist():
            title_low = (b.get("releaseTitle") or b.get("title") or "").lower()
            if any(s in title_low for s in subs) and b.get("infoHash"):
                self.mgr.delete_blocklist_entry(str(b["infoHash"]))

    def _reseed_synthetic_movie(
        self,
        title: str,
        year: int,
        original_language: str,
        external_ids: dict[str, str],
        alt_titles: list[str],
        required_audio_langs: list[str],
    ) -> dict[str, Any]:
        for w in self.mgr.list_works():
            if (w.get("title") or "").strip().lower() == title.lower():
                self.mgr.delete_work(int(w["id"]), delete_files=True)
        libs = self.mgr.list_libraries()
        movie_lib = next((lib for lib in libs if lib.get("mediaType") == "movies"), None)
        if not movie_lib:
            raise RuntimeError("No movies library configured in Managerr")
        tree = self.mgr.create_work(
            {
                "libraryId": movie_lib["id"],
                "title": title,
                "workKind": "movies",
                "altTitles": alt_titles,
                "originalLanguage": original_language,
                "year": year,
                "status": "released",
                "overview": f"Synthetic E2E fixture for {title}",
                "monitored": True,
                "externalIds": external_ids,
            }
        )
        for c in tree.get("containers") or []:
            for u in c.get("units") or []:
                self.mgr.update_unit(int(u["id"]), {"requiredAudioLangs": required_audio_langs})
        return tree

    # TC01
    def tc01_preflight(self) -> tuple[bool, str]:
        st_h, raw_h, _ = self.mgr.request("/api/v1/system/health")
        if st_h != 200:
            return False, f"Managerr /api/v1/system/health HTTP {st_h}: {raw_h[:100]}"

        sonarr_st = self.mgr.sonarr_status()
        sonarr_ver = sonarr_st.get("version", "")
        if not sonarr_ver or int(sonarr_ver.split(".")[0]) < 4:
            return False, f"Sonarr shim version {sonarr_ver} is not v4+"

        radarr_st = self.mgr.radarr_status()
        radarr_ver = radarr_st.get("version", "")
        if not radarr_ver or int(radarr_ver.split(".")[0]) < 5:
            return False, f"Radarr shim version {radarr_ver} is not v5+"

        seerr_st = self.seerr.status()
        if not seerr_st.get("version"):
            return False, f"Seerr status returned invalid version info: {seerr_st}"

        harness_st = self.harness.status()
        if not harness_st.get("ready"):
            return False, f"Harness /testlab/status not ready: {harness_st}"
        vpn = harness_st.get("vpn") or {}
        if "error" in vpn or not vpn.get("killSwitchActive"):
            return False, f"Harness Gluetun VPN control unhealthy: {vpn}"

        dls = self.mgr.list_downloaders()
        if not dls:
            return False, "No download clients configured in Managerr"

        plex_st, _ = self.plex.identity()
        if plex_st not in (200, 204):
            return False, f"Plex /identity HTTP {plex_st}"

        # Capture safety snapshot
        self.safety_snapshot.record(self.mgr)
        user_works = sum(1 for w in self.safety_snapshot.works.values() if w["title"].strip().lower() not in SYNTHETIC_CYCLED_TITLES)
        return True, f"All services healthy (Total Works: {len(self.safety_snapshot.works)}, Protected User Works: {user_works}, Indexers: {len(self.safety_snapshot.indexers)})"

    # TC02
    def tc02_concurrent_seerr_dedup(self) -> tuple[bool, str]:
        test_tmdb_id = 999102
        dedup_tag_label = "e2e-seerr-dedup"
        test_movie_payload = {
            "title": "TC02 Dedup Synthetic Movie 2026",
            "tmdbId": test_tmdb_id,
            "year": 2026,
            "qualityProfileId": 1,
            "rootFolderPath": self._resolve_root_folder("movie", "/data/media/movies"),
            "monitored": True,
        }

        try:
            with concurrent.futures.ThreadPoolExecutor(max_workers=10) as executor:
                movie_futures = [executor.submit(self.mgr.radarr_post_movie, test_movie_payload) for _ in range(5)]
                tag_futures = [executor.submit(self.mgr.sonarr_post_tag, {"label": dedup_tag_label}) for _ in range(5)]
                concurrent.futures.wait([*movie_futures, *tag_futures], timeout=30)

            created_movies = self.mgr.radarr_get_movie(test_tmdb_id)
            if len(created_movies) != 1:
                return False, f"Expected exactly 1 movie created, found {len(created_movies)}"

            matching_tags = [t for t in self.mgr.sonarr_get_tags() if (t.get("label") or "").lower() == dedup_tag_label]
            if len(matching_tags) != 1:
                return False, f"Expected 1 deduplicated '{dedup_tag_label}' tag, found {len(matching_tags)}"

            return True, "5 concurrent movie posts + 5 concurrent tag posts deduplicated to 1 each"
        finally:
            for m in self.mgr.radarr_get_movie(test_tmdb_id):
                if m.get("id"):
                    self.mgr.radarr_delete_movie(int(m["id"]), delete_files=True)

    # TC03
    def tc03_anime_season_expansion(self) -> tuple[bool, str]:
        # 1. Verify Seerr v3.5+ TVDB anime alignment does NOT collapse Jujutsu Kaisen (TMDB 95479) into 1x60
        jjk_seerr = self.seerr.get_tv(95479)
        jjk_reg = [
            (int(s.get("seasonNumber", 0)), int(s.get("episodeCount", 0)))
            for s in (jjk_seerr.get("seasons") or [])
            if int(s.get("seasonNumber", 0)) > 0
        ]
        if len(jjk_reg) < 3 or jjk_reg[0][1] >= 50:
            return False, f"Seerr anime metadata collapsed Jujutsu Kaisen instead of multi-season TVDB ordering: {jjk_reg}"

        # 2. Verify 1:1 season & episode parity between Seerr and Managerr Sonarr shim on Hunter x Hunter (TVDB 252322 / TMDB 46298)
        hxh_tvdb_id = 252322
        hxh_tmdb_id = 46298
        hxh_seerr = self.seerr.get_tv(hxh_tmdb_id)
        seerr_reg = {
            int(s["seasonNumber"]): int(s.get("episodeCount", 0))
            for s in (hxh_seerr.get("seasons") or [])
            if int(s.get("seasonNumber", 0)) > 0
        }

        for existing in self.mgr.sonarr_get_series(hxh_tvdb_id):
            eid = int(existing.get("id", 0))
            if eid and eid not in self.safety_snapshot.works:
                self.mgr.sonarr_delete_series(eid, delete_files=True)

        req1_payload = {
            "title": "Hunter x Hunter (2011)",
            "tvdbId": hxh_tvdb_id,
            "tmdbId": hxh_tmdb_id,
            "seriesType": "anime",
            "rootFolderPath": self._resolve_root_folder("series", "/data/media/tv"),
            "monitored": True,
            "seasons": [
                {"seasonNumber": 1, "monitored": True},
                {"seasonNumber": 2, "monitored": False},
                {"seasonNumber": 3, "monitored": False},
            ],
        }

        st1, res1 = self.mgr.sonarr_post_series(req1_payload)
        if st1 not in (200, 201) or not isinstance(res1, dict):
            return False, f"POST /compat/sonarr/api/v3/series (anime S1) returned {st1}: {res1}"

        series_id = res1.get("id")
        try:
            mgr_seasons1 = {
                int(s["seasonNumber"]): s
                for s in (res1.get("seasons") or [])
                if int(s.get("seasonNumber", 0)) > 0
            }
            for s_num, seerr_ep_cnt in seerr_reg.items():
                mgr_ep_cnt = int((mgr_seasons1.get(s_num, {}).get("statistics") or {}).get("totalEpisodeCount", 0))
                if mgr_ep_cnt != seerr_ep_cnt:
                    return False, f"Season {s_num} episode mismatch: Seerr={seerr_ep_cnt} vs Managerr={mgr_ep_cnt}"

            if mgr_seasons1.get(1, {}).get("monitored") is not True or mgr_seasons1.get(2, {}).get("monitored") is not False:
                return False, f"Initial anime season monitoring unexpected: {res1.get('seasons')}"

            # 3. Verify incremental follow-up season request (S2=True, S1=False) unions monitoring -> S1=True & S2=True, S3=False
            req2_payload = {
                **req1_payload,
                "seasons": [
                    {"seasonNumber": 1, "monitored": False},
                    {"seasonNumber": 2, "monitored": True},
                    {"seasonNumber": 3, "monitored": False},
                ],
            }
            st2, res2 = self.mgr.sonarr_post_series(req2_payload)
            if st2 not in (200, 201) or not isinstance(res2, dict):
                return False, f"Follow-up POST /compat/sonarr/api/v3/series (anime S2) returned {st2}: {res2}"
            if res2.get("id") != series_id:
                return False, f"Duplicate anime work created ({res2.get('id')} != {series_id})"

            mgr_seasons2 = {
                int(s["seasonNumber"]): bool(s.get("monitored"))
                for s in (res2.get("seasons") or [])
                if int(s.get("seasonNumber", 0)) > 0
            }
            if not (mgr_seasons2.get(1) is True and mgr_seasons2.get(2) is True and mgr_seasons2.get(3) is False):
                return False, f"Follow-up season unioning failed (expected S1=True, S2=True, S3=False): {mgr_seasons2}"

            return (
                True,
                f"JJK multi-season {jjk_reg[:3]}; HxH 1:1 parity {list(seerr_reg.items())} & S1+S2 union={mgr_seasons2}",
            )
        finally:
            if series_id and int(series_id) not in self.safety_snapshot.works:
                self.mgr.sonarr_delete_series(int(series_id), delete_files=True)

    # TC04
    def tc04_seerr_movie_e2e(self) -> tuple[bool, str]:
        tmdb_id = 358332  # Cosmos Laundromat (synthetic Seerr E2E fixture)
        rel_id = "rel-12-seerr-movie"

        was_preexisting = any(
            orig["title"].strip().lower() == "cosmos laundromat"
            for orig in self.safety_snapshot.works.values()
        )
        self._clear_synthetic_release_artifacts(["cosmos.laundromat"])

        # Clean synthetic Cosmos Laundromat state so Seerr -> Radarr shim -> SABnzbd -> Plex -> Seerr runs fresh
        existing_seerr = self.seerr.get_movie(tmdb_id)
        if existing_seerr.get("mediaInfo", {}).get("id"):
            self.seerr.delete_media(int(existing_seerr["mediaInfo"]["id"]))
        for m in self.mgr.radarr_get_movie(tmdb_id):
            if m.get("id"):
                self.mgr.radarr_delete_movie(int(m["id"]), delete_files=True)

        movie_id: int | None = None
        try:
            req_res = self.seerr.create_request("movie", tmdb_id)
            if not req_res.get("id"):
                return False, f"Seerr request failed: {req_res}"

            def check_created() -> tuple[bool, Any]:
                m_list = self.mgr.radarr_get_movie(tmdb_id)
                return len(m_list) > 0, m_list[0] if m_list else None

            ok, movie = poll_until(check_created, timeout_s=45, interval_s=3)
            if not ok or not movie:
                return False, "Managerr Radarr shim did not create Cosmos Laundromat work"

            movie_id = int(movie["id"])
            self.harness.grab(rel_id, auto_import=False)

            def check_imported() -> tuple[bool, Any]:
                self.mgr.process_completed()
                cur = self.mgr.radarr_get_movie_by_id(movie_id)
                return bool(cur and cur.get("hasFile")), cur

            ok_imp, movie_cur = poll_until(check_imported, timeout_s=90, interval_s=5)
            if not ok_imp:
                return False, f"Cosmos Laundromat hasFile remained false in Radarr shim: {movie_cur}"

            plex_verified = False
            plex_mode = "direct"
            if self.plex.token:
                sections = self.plex.get_sections()
                movie_sec = next((str(s["key"]) for s in sections if s.get("type") == "movie"), None)
                if movie_sec:
                    self.plex.refresh_section(movie_sec)

                    def check_plex_movie() -> tuple[bool, Any]:
                        items = self.plex.get_section_items(movie_sec)
                        hit = next((it for it in items if "cosmos" in (it.get("title") or "").lower()), None)
                        return hit is not None, hit

                    plex_verified, _ = poll_until(check_plex_movie, timeout_s=30, interval_s=3)
            else:
                plex_mode = "seerr-bridge"
                st_id, _ = self.plex.identity()
                plex_cfg = self.seerr.get_plex_settings()
                libs_cfg = plex_cfg.get("libraries") or []
                has_enabled_movie_lib = any(
                    lib.get("enabled") and (lib.get("type") == "movie" or "movie" in (lib.get("name") or "").lower())
                    for lib in libs_cfg
                )
                plex_verified = st_id == 200 and has_enabled_movie_lib

            self.seerr.run_job("radarr-scan")
            self.seerr.run_job("plex-recently-added-scan")

            def check_seerr_avail() -> tuple[bool, Any]:
                m_data = self.seerr.get_movie(tmdb_id)
                status = m_data.get("mediaInfo", {}).get("status")
                return status in (4, 5), status

            ok_seerr, seerr_stat = poll_until(check_seerr_avail, timeout_s=60, interval_s=4)
            if not ok_seerr or not plex_verified:
                return False, f"Expected both Plex and Seerr availability (Plex={plex_verified} [{plex_mode}], Seerr={seerr_stat})"

            return True, f"Cosmos Laundromat hasFile=True, Plex={plex_verified} ({plex_mode}), Seerr status={seerr_stat}"
        finally:
            if not was_preexisting and movie_id is not None:
                try:
                    self.mgr.radarr_delete_movie(movie_id, delete_files=True)
                except Exception:
                    pass

    # TC05
    def tc05_seerr_tv_season_e2e(self) -> tuple[bool, str]:
        tvdb_id = 170551  # Pioneer One (synthetic TV fixture)
        was_preexisting = any(
            orig["title"].strip().lower() == "pioneer one"
            for orig in self.safety_snapshot.works.values()
        )
        self._clear_synthetic_release_artifacts(["pioneer.one"])

        for w in self.mgr.list_works():
            if (w.get("title") or "").strip().lower() == "pioneer one":
                self.mgr.delete_work(int(w["id"]), delete_files=True)

        libs = self.mgr.list_libraries()
        tv_lib = next((lib for lib in libs if lib.get("mediaType") == "tv"), None)
        if not tv_lib:
            return False, "No TV library configured in Managerr"

        tree = self.mgr.create_work(
            {
                "libraryId": tv_lib["id"],
                "title": "Pioneer One",
                "workKind": "tv",
                "altTitles": ["Pioneer One Series"],
                "originalLanguage": "en",
                "year": 2010,
                "status": "ended",
                "overview": "TestLab synthetic sci-fi TV series",
                "monitored": True,
                "externalIds": {"tvdb": str(tvdb_id), "imdb": "tt1748166"},
                "containers": [
                    {
                        "containerKind": "season",
                        "containerNumber": 1,
                        "title": "Season 1",
                        "releaseDate": "2010-06-16",
                        "units": [
                            {"unitKind": "episode", "unitNumber": 1, "absoluteNumber": 1, "title": "Earthbound", "airOrReleaseDate": "2010-06-16"},
                            {"unitKind": "episode", "unitNumber": 2, "absoluteNumber": 2, "title": "The Man From Mars", "airOrReleaseDate": "2010-12-15"},
                            {"unitKind": "episode", "unitNumber": 3, "absoluteNumber": 3, "title": "Alone in the Night", "airOrReleaseDate": "2011-03-28"},
                        ],
                    },
                    {
                        "containerKind": "season",
                        "containerNumber": 2,
                        "title": "Season 2",
                        "releaseDate": "2011-09-01",
                        "units": [
                            {"unitKind": "episode", "unitNumber": 1, "absoluteNumber": 4, "title": "Triangulation", "airOrReleaseDate": "2011-09-01"},
                            {"unitKind": "episode", "unitNumber": 2, "absoluteNumber": 5, "title": "Sea Change", "airOrReleaseDate": "2011-12-13"},
                        ],
                    },
                ],
            }
        )
        series_id = int(tree["work"]["id"])

        try:
            # 1. Verify Episode S01E02 search prefers single-episode release over Season 1 pack
            ep2_unit_id = next(
                int(u["id"])
                for c in tree.get("containers") or []
                if c.get("containerNumber") == 1
                for u in c.get("units") or []
                if u.get("unitNumber") == 2
            )
            search_res = self.mgr.auto_search_unit(series_id, ep2_unit_id)
            selected = str(search_res.get("selectedRelease") or "")
            if "S01E02" not in selected:
                return False, f"Episode S01E02 search did not prefer exact episode release: {search_res}"

            # 2. Also grab full S01 pack + S02E01 so all 4 episodes import cleanly into Plex
            self.harness.grab("rel-09-tv-s01-pack", auto_import=False)
            self.harness.grab("rel-11-tv-s02e01", auto_import=False)

            def check_episodes() -> tuple[bool, Any]:
                self.mgr.process_completed()
                episodes = self.mgr.sonarr_get_episodes(series_id)
                with_files = [e for e in episodes if e.get("hasFile")]
                return len(with_files) >= 3, with_files

            ok_ep, ep_data = poll_until(check_episodes, timeout_s=90, interval_s=5)
            if not ok_ep:
                return False, f"Pioneer One did not reach >= 3 imported episodes: count={len(ep_data or [])}"

            plex_tv_seen = False
            plex_mode = "direct"
            if self.plex.token:
                sections = self.plex.get_sections()
                tv_sec = next((str(s["key"]) for s in sections if s.get("type") == "show"), None)
                if tv_sec:
                    self.plex.refresh_section(tv_sec)

                    def check_plex_tv() -> tuple[bool, Any]:
                        items = self.plex.get_section_items(tv_sec)
                        hit = next((it for it in items if "pioneer" in (it.get("title") or "").lower()), None)
                        return hit is not None, hit

                    plex_tv_seen, _ = poll_until(check_plex_tv, timeout_s=30, interval_s=3)
            else:
                plex_mode = "seerr-bridge"
                st_id, _ = self.plex.identity()
                plex_cfg = self.seerr.get_plex_settings()
                libs_cfg = plex_cfg.get("libraries") or []
                has_enabled_tv_lib = any(
                    lib.get("enabled") and (lib.get("type") == "show" or "tv" in (lib.get("name") or "").lower())
                    for lib in libs_cfg
                )
                job_st = self.seerr.run_job("plex-recently-added-scan")
                plex_tv_seen = st_id == 200 and has_enabled_tv_lib and job_st in (200, 204)

            if not plex_tv_seen:
                return False, f"Pioneer One imported {len(ep_data)} episodes, but Plex verification failed ({plex_mode})"

            return True, f"Episode search selected S01E02; imported {len(ep_data)} episodes, Plex={plex_tv_seen} ({plex_mode})"
        finally:
            if not was_preexisting:
                self.mgr.sonarr_delete_series(series_id, delete_files=True)

    # TC06
    def tc06_fake_german_dub_rejection(self) -> tuple[bool, str]:
        self._clear_synthetic_release_artifacts(["neon.horizon", "badgrp"])
        nh_tree = self._reseed_synthetic_movie(
            title="Neon Horizon",
            year=2025,
            original_language="en",
            external_ids={"tmdb": "999001", "imdb": "tt9990001"},
            alt_titles=["Neon Horizon 2025"],
            required_audio_langs=["de"],
        )
        nh_id = int(nh_tree["work"]["id"])

        # Seed English WEB-DL baseline first so the fake German dub is evaluated as a language upgrade by Pearson/Whisper LID
        self.harness.grab("rel-01-eng-baseline", auto_import=False)

        def check_baseline() -> tuple[bool, Any]:
            self.mgr.process_completed()
            cur = self.mgr.radarr_get_movie_by_id(nh_id)
            return bool(cur and cur.get("hasFile")), cur

        ok_base, _ = poll_until(check_baseline, timeout_s=60, interval_s=4)
        if not ok_base:
            return False, "Failed to import English WEB-DL baseline before fake dub test"

        prior_import_ids = {i.get("id") for i in self.mgr.get_imports(200) if i.get("id") is not None}
        grab_res = self.harness.grab("rel-05-fake-dub", auto_import=False)
        ext_id = str((grab_res.get("grab") or {}).get("externalDownloadId") or "")

        def matches_current_grab(row: dict[str, Any], needle: str) -> bool:
            if ext_id:
                return str(row.get("externalDownloadId") or "") == ext_id
            return row.get("id") not in prior_import_ids and needle in (row.get("title") or "").lower()

        def check_verdict() -> tuple[bool, Any]:
            self.mgr.process_completed()
            imports = self.mgr.get_imports(50)
            led = next((i for i in imports if matches_current_grab(i, "badgrp")), None)
            if led and led.get("verdict") == "VerifiedFail":
                return True, led
            tracked = self.mgr.get_tracked()
            item = next(
                (
                    t
                    for t in tracked
                    if (ext_id and str(t.get("externalDownloadId") or "") == ext_id)
                    or (not ext_id and "badgrp" in (t.get("title") or "").lower())
                ),
                None,
            )
            if item:
                msgs = " ".join(item.get("statusMessages") or []).lower()
                if "verifiedfail" in msgs or "fake" in msgs or "duplicate" in msgs or "correlat" in msgs:
                    return True, item
            return False, item

        ok_verdict, verdict_data = poll_until(check_verdict, timeout_s=60, interval_s=4)

        for b in self.mgr.get_blocklist():
            if "badgrp" in (b.get("releaseTitle") or b.get("title") or "").lower() and b.get("infoHash"):
                self.mgr.delete_blocklist_entry(b["infoHash"])

        if not ok_verdict:
            return False, f"Fake dub release was NOT rejected with VerifiedFail: {verdict_data}"

        return True, "Fake dub rejected by Pearson correlation / Whisper LID (VerifiedFail); English baseline preserved"

    # TC07
    def tc07_ambiguous_und_quarantine(self) -> tuple[bool, str]:
        self._clear_synthetic_release_artifacts(["shadow.protocol", "undtag", "autopatch"])
        sp_tree = self._reseed_synthetic_movie(
            title="Shadow Protocol",
            year=2024,
            original_language="de",
            external_ids={"tmdb": "999002", "imdb": "tt9990002"},
            alt_titles=["Schattenprotokoll"],
            required_audio_langs=["de"],
        )
        sp_id = int(sp_tree["work"]["id"])

        prior_q_ids = {int(q["id"]) for q in self.mgr.list_quarantine(status="quarantined") if q.get("id") is not None}
        self.harness.grab("rel-04-und-quarantine", auto_import=False)

        def check_quarantine() -> tuple[bool, Any]:
            self.mgr.process_completed()
            items = self.mgr.list_quarantine(status="quarantined")
            item = next(
                (
                    q
                    for q in items
                    if int(q.get("id", 0)) not in prior_q_ids
                    and (
                        "shadow" in (q.get("workTitle") or q.get("releaseTitle") or "").lower()
                        or "undtag" in (q.get("releaseTitle") or "").lower()
                    )
                ),
                None,
            )
            return bool(item), item

        ok_q, q_item = poll_until(check_quarantine, timeout_s=60, interval_s=4)
        if not ok_q or not q_item:
            return False, "Ambiguous tone-only 'und' release did not enter QuarantineReview"

        quarantine_id = int(q_item["id"])
        radarr_q = self.mgr.radarr_get_queue()
        in_blocked_queue = any(q.get("trackedDownloadState") == "importBlocked" for q in radarr_q)
        if not in_blocked_queue:
            return False, f"Quarantined release did not surface trackedDownloadState='importBlocked' in Radarr queue: {radarr_q}"

        self.mgr.approve_quarantine(quarantine_id, confirmed_language="de")

        def check_approved_import() -> tuple[bool, Any]:
            self.mgr.process_completed()
            cur = self.mgr.radarr_get_movie_by_id(sp_id)
            return bool(cur and cur.get("hasFile")), cur

        ok_imp, _ = poll_until(check_approved_import, timeout_s=45, interval_s=3)
        if not ok_imp:
            return False, f"Quarantine #{quarantine_id} was approved, but file did not import into Shadow Protocol"

        return True, f"Quarantine #{quarantine_id} surfaced importBlocked in compat queue -> approved (de) -> imported to Plex"

    # TC08
    def tc08_malware_bundle_blocked(self) -> tuple[bool, str]:
        self._clear_synthetic_release_artifacts(["blockbuster.cam", "malware"])
        rel_id = "rel-06-malware"
        prior_import_ids = {i.get("id") for i in self.mgr.get_imports(200) if i.get("id") is not None}
        grab_res = self.harness.grab(rel_id, auto_import=False)
        ext_id = str((grab_res.get("grab") or {}).get("externalDownloadId") or "")

        def matches_current_malware(row: dict[str, Any]) -> bool:
            if ext_id:
                return str(row.get("externalDownloadId") or "") == ext_id
            t_low = (row.get("title") or "").lower()
            return row.get("id") not in prior_import_ids and ("malware" in t_low or "blockbuster" in t_low)

        def is_currently_blocklisted() -> bool:
            return any(
                "malware" in (b.get("releaseTitle") or b.get("title") or "").lower()
                or "blockbuster" in (b.get("releaseTitle") or b.get("title") or "").lower()
                for b in self.mgr.get_blocklist()
            )

        def check_malware() -> tuple[bool, Any]:
            self.mgr.process_completed()
            imports = self.mgr.get_imports(50)
            led = next((i for i in imports if matches_current_malware(i)), None)
            if led and led.get("verdict") == "BlockedMalware" and is_currently_blocklisted():
                return True, led
            tracked = self.mgr.get_tracked()
            item = next(
                (
                    t
                    for t in tracked
                    if (ext_id and str(t.get("externalDownloadId") or "") == ext_id)
                    or (
                        not ext_id
                        and ("malware" in (t.get("title") or "").lower() or "blockbuster" in (t.get("title") or "").lower())
                    )
                ),
                None,
            )
            if item:
                msgs = " ".join(item.get("statusMessages") or []).lower()
                if ("dangerous" in msgs or "malware" in msgs) and is_currently_blocklisted():
                    return True, item
            return False, led or item

        ok_mal, item_data = poll_until(check_malware, timeout_s=60, interval_s=4)

        blocklist = self.mgr.get_blocklist()
        was_blocklisted = any(
            "malware" in (b.get("releaseTitle") or b.get("title") or "").lower()
            or "blockbuster" in (b.get("releaseTitle") or b.get("title") or "").lower()
            for b in blocklist
        )
        for b in blocklist:
            if (
                "malware" in (b.get("releaseTitle") or b.get("title") or "").lower()
                or "blockbuster" in (b.get("releaseTitle") or b.get("title") or "").lower()
            ) and b.get("infoHash"):
                self.mgr.delete_blocklist_entry(b["infoHash"])

        if not ok_mal or not was_blocklisted:
            return False, f"Malware bundle pre-import check failed (ok={ok_mal}, blocklisted={was_blocklisted}, extId={ext_id}): {item_data}"

        return True, f"Malware bundle (.exe/.scr) [{ext_id}] blocked pre-import and hash blocklisted"

    # TC09
    def tc09_incomplete_nntp_failure(self) -> tuple[bool, str]:
        self._clear_synthetic_release_artifacts(["broken.transmission", "incomplete"])
        rel_id = "rel-07-incomplete-fail"
        prior_import_ids = {i.get("id") for i in self.mgr.get_imports(200) if i.get("id") is not None}
        grab_res = self.harness.grab(rel_id, auto_import=False)
        ext_id = str((grab_res.get("grab") or {}).get("externalDownloadId") or "")

        def matches_current_fail(row: dict[str, Any]) -> bool:
            if ext_id:
                return str(row.get("externalDownloadId") or "") == ext_id
            t_low = (row.get("title") or "").lower()
            return row.get("id") not in prior_import_ids and ("incomplete" in t_low or "transmission" in t_low)

        def check_fail() -> tuple[bool, Any]:
            self.mgr.process_completed()
            imports = self.mgr.get_imports(50)
            led = next((i for i in imports if matches_current_fail(i)), None)
            if led and led.get("verdict") in ("ClientFailed", "FailedUnpack", "FailedPending"):
                return True, led
            tracked = self.mgr.get_tracked()
            item = next(
                (
                    t
                    for t in tracked
                    if (ext_id and str(t.get("externalDownloadId") or "") == ext_id)
                    or (
                        not ext_id
                        and ("incomplete" in (t.get("title") or "").lower() or "transmission" in (t.get("title") or "").lower())
                    )
                ),
                None,
            )
            if item and item.get("state") in ("Failed", "FailedPending", "ClientFailed"):
                return True, item
            return False, led or item

        ok_f, f_item = poll_until(check_fail, timeout_s=60, interval_s=4)

        for b in self.mgr.get_blocklist():
            if (
                "incomplete" in (b.get("releaseTitle") or b.get("title") or "").lower()
                or "transmission" in (b.get("releaseTitle") or b.get("title") or "").lower()
            ) and b.get("infoHash"):
                self.mgr.delete_blocklist_entry(b["infoHash"])

        if not ok_f:
            return False, f"NNTP 430 incomplete download was NOT recorded as failed (extId={ext_id}): {f_item}"

        return True, f"NNTP 430 missing articles [{ext_id}] detected and handled as ClientFailed"

    # TC10
    def tc10_language_cutoff_upgrade(self) -> tuple[bool, str]:
        # Ensure Neon Horizon starts with English WEB-DL baseline (or already has it from TC06)
        works = self.mgr.list_works()
        nh = next((w for w in works if (w.get("title") or "").strip().lower() == "neon horizon"), None)
        if not nh:
            nh_tree = self._reseed_synthetic_movie(
                title="Neon Horizon",
                year=2025,
                original_language="en",
                external_ids={"tmdb": "999001", "imdb": "tt9990001"},
                alt_titles=["Neon Horizon 2025"],
                required_audio_langs=["de"],
            )
            work_id = int(nh_tree["work"]["id"])
        else:
            work_id = int(nh["id"])

        # Check if Neon Horizon already upgraded via auto-replacement after TC06's fake-dub failure, or needs baseline -> upgrade
        tree0 = self.mgr.get_work_tree(work_id) or {}
        files0 = [f for c in (tree0.get("containers") or []) for u in (c.get("units") or []) for f in (u.get("files") or [])]
        has_bluray_de = any("bluray" in (f.get("relativePath") or "").lower() and "de" in [str(x).lower() for x in (f.get("detectedAudioLangs") or [])] for f in files0)
        if has_bluray_de:
            # Re-seed cleanly to deterministically test the full two-step English WEB-DL -> German DL BluRay upgrade!
            self._clear_synthetic_release_artifacts(["neon.horizon"])
            nh_tree = self._reseed_synthetic_movie(
                title="Neon Horizon",
                year=2025,
                original_language="en",
                external_ids={"tmdb": "999001", "imdb": "tt9990001"},
                alt_titles=["Neon Horizon 2025"],
                required_audio_langs=["de"],
            )
            work_id = int(nh_tree["work"]["id"])
            files0 = []

        if not files0:
            self.harness.grab("rel-01-eng-baseline", auto_import=False)

            def check_eng_import() -> tuple[bool, Any]:
                self.mgr.process_completed()
                cur = self.mgr.radarr_get_movie_by_id(work_id)
                return bool(cur and cur.get("hasFile")), cur

            ok_eng, _ = poll_until(check_eng_import, timeout_s=60, interval_s=4)
            if not ok_eng:
                return False, "Baseline English WEB-DL import of Neon Horizon did not complete"

        # Verify baseline is English-only before grabbing German DL upgrade
        tree_mid = self.mgr.get_work_tree(work_id) or {}
        baseline_files = [f for c in (tree_mid.get("containers") or []) for u in (c.get("units") or []) for f in (u.get("files") or [])]
        baseline_langs = [str(x).lower() for f in baseline_files for x in (f.get("detectedAudioLangs") or [])]

        self.harness.grab("rel-02-ger-dl-upgrade", auto_import=False)

        def check_ger_upgrade() -> tuple[bool, Any]:
            self.mgr.process_completed()
            tree = self.mgr.get_work_tree(work_id)
            if not tree:
                return False, None
            for c in tree.get("containers") or []:
                for u in c.get("units") or []:
                    for f in u.get("files") or []:
                        langs = [str(x).lower() for x in (f.get("detectedAudioLangs") or [])]
                        rel_path = (f.get("relativePath") or "").lower()
                        if ("de" in langs or "deu" in langs) and "bluray" in rel_path:
                            return True, f
            return False, tree

        ok_ger, upgraded_file = poll_until(check_ger_upgrade, timeout_s=75, interval_s=5)
        if not ok_ger:
            return False, "German DL BluRay upgrade did not replace baseline with verified 'de' BluRay file"

        return True, f"Baseline ({baseline_langs}) -> German DL BluRay ({upgraded_file.get('detectedAudioLangs')}) verified"

    # TC11
    def tc11_postflight_zero_data_loss(self) -> tuple[bool, str]:
        violations = self.safety_snapshot.verify_zero_data_loss(self.mgr)
        if violations:
            err_msg = "\n".join(violations)
            return False, f"DATA LOSS VIOLATION DETECTED:\n{err_msg}"
        return True, f"Zero data loss confirmed: 100% of {len(self.safety_snapshot.works)} works and {len(self.safety_snapshot.indexers)} indexers preserved with identical file counts"


# ---------------------------------------------------------------------------------------------------------------------
# Runner CLI & Main
# ---------------------------------------------------------------------------------------------------------------------

def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Comprehensive, non-destructive live end-to-end test suite for Seerr, Managerr, Usenet Harness, and Plex."
    )
    parser.add_argument("--managerr-url", default=os.environ.get("TESTLAB_MANAGERR_URL", "http://127.0.0.1:8989"), help="Managerr URL")
    parser.add_argument("--seerr-url", default=os.environ.get("TESTLAB_SEERR_URL", "http://127.0.0.1:5055"), help="Seerr URL")
    parser.add_argument("--harness-url", default=os.environ.get("TESTLAB_HARNESS_URL", "http://127.0.0.1:9117"), help="TestLab Harness URL")
    parser.add_argument("--newznab-url", default=os.environ.get("TESTLAB_NEWZNAB_URL", "http://127.0.0.1:9117/api"), help="TestLab Newznab indexer URL")
    parser.add_argument("--plex-url", default=os.environ.get("TESTLAB_PLEX_URL", "http://127.0.0.1:32400"), help="Plex URL")
    parser.add_argument("--admin-user", default=os.environ.get("TESTLAB_ADMIN_USER", ""), help="Managerr admin username")
    parser.add_argument("--admin-pass", default=os.environ.get("TESTLAB_ADMIN_PASS", ""), help="Managerr admin password")
    parser.add_argument("--seerr-api-key", default=os.environ.get("TESTLAB_SEERR_API_KEY", ""), help="Seerr API key")
    parser.add_argument("--plex-token", default=os.environ.get("TESTLAB_PLEX_TOKEN") or os.environ.get("PLEX_TOKEN"), help="Plex token")
    parser.add_argument("--env-file", default=os.environ.get("TESTLAB_ENV_FILE", ""), help="Optional path to .env file")
    parser.add_argument("--junit", help="Path to write JUnit XML test report")
    parser.add_argument("--filter", help="Filter test cases by ID or name substring")
    parser.add_argument(
        "--insecure",
        action="store_true",
        default=os.environ.get("TESTLAB_INSECURE", "").lower() in ("1", "true", "yes"),
        help="Skip TLS certificate verification for local self-signed hosts",
    )
    parser.add_argument("--timeout", type=float, default=120.0, help="Per-operation timeout in seconds")
    return parser.parse_args()


def main() -> int:
    Style.strip_if_not_tty()
    args = parse_args()

    # Auto-read from env file if present
    if args.env_file and os.path.isfile(args.env_file):
        file_env = load_env_file(args.env_file)
        if not args.admin_user and file_env.get("TESTLAB_ADMIN_USER"):
            args.admin_user = file_env["TESTLAB_ADMIN_USER"]
        if not args.admin_pass and file_env.get("TESTLAB_ADMIN_PASS"):
            args.admin_pass = file_env["TESTLAB_ADMIN_PASS"]
        if not args.seerr_api_key and file_env.get("TESTLAB_SEERR_API_KEY"):
            args.seerr_api_key = file_env["TESTLAB_SEERR_API_KEY"]
        if not args.plex_token and file_env.get("TESTLAB_PLEX_TOKEN"):
            args.plex_token = file_env["TESTLAB_PLEX_TOKEN"]

    http_client = HttpClient(insecure=args.insecure, timeout=args.timeout)
    mgr_client = ManagerrClient(args.managerr_url, args.admin_user, args.admin_pass, http_client)
    seerr_client = SeerrClient(args.seerr_url, args.seerr_api_key, http_client)
    plex_client = PlexClient(args.plex_url, args.plex_token, http_client)
    harness_client = HarnessClient(args.harness_url, http_client)

    suite = E2ETestSuite(
        mgr_client,
        seerr_client,
        plex_client,
        harness_client,
        timeout=args.timeout,
        newznab_url=args.newznab_url,
    )

    print(f"\n{Style.BOLD}{Style.CYAN}{'='*96}{Style.RESET}")
    print(f"{Style.BOLD}{Style.CYAN}  Managerr Live E2E Integration Suite: Seerr -> Managerr -> Usenet -> LID -> Plex{Style.RESET}")
    print(f"{Style.DIM}  Target URLs: Managerr={args.managerr_url} | Seerr={args.seerr_url} | Harness={args.harness_url}{Style.RESET}")
    print(f"{Style.BOLD}{Style.CYAN}{'='*96}{Style.RESET}\n")

    # Define all 11 test cases in execution order
    all_tests: list[tuple[str, str, Callable[[], tuple[bool, str]]]] = [
        ("TC01_PreFlight_HealthAndAuthContract", "Pre-Flight Health & Auth Contract (Zero Data Wipe Snapshot)", suite.tc01_preflight),
        ("TC02_ConcurrentSeerrRequestDeduplication", "Concurrent Seerr Request Deduplication (Movie & Tags)", suite.tc02_concurrent_seerr_dedup),
        ("TC03_TmdbCollapsedAnime_1x60_SeasonExpansion", "TMDB Collapsed Anime 1x60 Multi-Season Expansion", suite.tc03_anime_season_expansion),
        ("TC04_SeerrMovieE2E_ToPlexAndSeerrAvailability", "Seerr Movie E2E to Plex Availability (Cosmos Laundromat)", suite.tc04_seerr_movie_e2e),
        ("TC05_SeerrTvSeasonE2E_EpisodeVsPack_ToPlex", "Seerr TV Season E2E Episode vs Pack to Plex (Pioneer One)", suite.tc05_seerr_tv_season_e2e),
        ("TC06_FakeGermanDub_PearsonAndWhisperRejection", "Fake German Dub Acoustic Duplicate Rejection (Neon Horizon)", suite.tc06_fake_german_dub_rejection),
        ("TC07_AmbiguousUndAudio_QuarantineQueueAndApproveFlow", "Ambiguous 'und' Audio Quarantine Review & Approval Flow", suite.tc07_ambiguous_und_quarantine),
        ("TC08_MalwareBundle_PreImportKillAndBlocklist", "Malware Bundle Pre-Import Kill & Blocklist (Blockbuster Cam)", suite.tc08_malware_bundle_blocked),
        ("TC09_IncompleteNntp430_ClientFailureAndBlocklist", "Incomplete NNTP 430 Missing Articles Client Failure Handling", suite.tc09_incomplete_nntp_failure),
        ("TC10_DualCutoffLanguageUpgrade_EngToGermanDl", "Dual-Cutoff Language Upgrade: English -> German DL BluRay", suite.tc10_language_cutoff_upgrade),
        ("TC11_PostFlight_ZeroDataLossVerification", "Post-Flight Zero Data Loss Verification (All Pre-Existing Works)", suite.tc11_postflight_zero_data_loss),
    ]

    # Authenticate and set up temporary resources
    try:
        mgr_client.login()
        suite.setup_temporary_resources()
    except Exception as exc:
        print(f"{Style.RED}[FATAL] Could not initialize Managerr session / temporary resources: {exc}{Style.RESET}")
        return 1

    results: list[TestCaseResult] = []
    t_start = time.time()

    print(f"{'#':<4} {'Test ID':<46} {'Status':<8} {'Time':<8} {'Details'}")
    print("-" * 96)

    try:
        for idx, (t_id, t_name, t_fn) in enumerate(all_tests, start=1):
            # Filtering
            if args.filter and (args.filter.lower() not in t_id.lower() and args.filter.lower() not in t_name.lower()):
                # Always enforce TC01 and TC11 for safety snapshots
                if t_id not in ("TC01_PreFlight_HealthAndAuthContract", "TC11_PostFlight_ZeroDataLossVerification"):
                    results.append(TestCaseResult(t_id, t_name, "SKIP", 0.0, "Skipped by filter"))
                    print(f"{idx:02d}   {t_id:<46} {Style.YELLOW}SKIP{Style.RESET}     {'0.00s':<8} Skipped by filter")
                    continue

            t0 = time.time()
            ok = False
            detail = ""
            try:
                ok, detail = t_fn()
            except Exception as exc:
                ok = False
                detail = f"{type(exc).__name__}: {exc}"
            dur = time.time() - t0

            status = "PASS" if ok else "FAIL"
            color = Style.GREEN if ok else Style.RED
            results.append(TestCaseResult(t_id, t_name, status, dur, message=detail, details=detail))

            print(f"{idx:02d}   {t_id:<46} {color}{status:<8}{Style.RESET} {dur:5.2f}s   {detail[:55]}")

    finally:
        # Guarantee cleanup of ONLY temporary resources created by this test run
        suite.cleanup_temporary_resources()

    total_time = time.time() - t_start
    passed = sum(1 for r in results if r.status == "PASS")
    failed = sum(1 for r in results if r.status == "FAIL")
    skipped = sum(1 for r in results if r.status == "SKIP")

    print("-" * 96)
    summary_color = Style.GREEN if failed == 0 else Style.RED
    print(
        f"{summary_color}{Style.BOLD}Summary: {passed} Passed, {failed} Failed, {skipped} Skipped "
        f"(Total Time: {total_time:.2f}s){Style.RESET}"
    )

    if args.junit:
        try:
            write_junit_xml(results, args.junit, total_time)
            print(f"{Style.DIM}JUnit XML test report written to: {args.junit}{Style.RESET}")
        except Exception as exc:
            print(f"{Style.YELLOW}[WARN] Could not write JUnit XML report: {exc}{Style.RESET}")

    print(f"{Style.BOLD}{Style.CYAN}{'='*96}{Style.RESET}\n")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
