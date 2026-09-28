#!/usr/bin/env python3
"""Add one release's ABI-specific ZIPs to the Jellyfin catalog manifest."""

import argparse
import hashlib
import json
import re
import zipfile
from datetime import datetime
from pathlib import Path


ABIS = (
    ("12", "12.0.0.0"),
    ("10.11", "10.11.0.0"),
    ("10.10", "10.10.7.0"),
)
RELEASE_BASE = "https://github.com/Husky0c/jellyfin-bilibili/releases/download"
ICON_URL = "https://raw.githubusercontent.com/Husky0c/jellyfin-bilibili/main/assets/icon.png"


def build_versions(version: str, assets: Path, changelog: str, timestamp: str, meta: dict) -> list[dict]:
    result = []
    for abi, target_abi in ABIS:
        name = f"BiliArchive_{version}_Jellyfin-{abi}-anycpu.zip"
        archive = assets / name
        data = archive.read_bytes()
        expected_sha = (assets / f"{name}.sha256").read_text(encoding="ascii").strip()
        actual_sha = hashlib.sha256(data).hexdigest()
        if expected_sha != f"{actual_sha}  {name}":
            raise ValueError(f"SHA-256 sidecar does not match {name}")
        with zipfile.ZipFile(archive) as package:
            package_meta = json.loads(package.read("meta.json"))
        if any(package_meta.get(key) != value for key, value in (
            ("guid", meta["guid"]), ("version", version), ("targetAbi", target_abi)
        )):
            raise ValueError(f"Package metadata does not match {name}")
        result.append({
            "version": version,
            "changelog": changelog,
            "targetAbi": target_abi,
            "sourceUrl": f"{RELEASE_BASE}/v{version}/{name}",
            "checksum": hashlib.md5(data, usedforsecurity=False).hexdigest(),
            "timestamp": timestamp,
        })
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--timestamp", required=True)
    parser.add_argument("--manifest", type=Path, default=Path("manifest.json"))
    parser.add_argument("--meta", type=Path, default=Path("meta.json"))
    parser.add_argument("--notes", type=Path, default=Path("RELEASE_NOTES.md"))
    args = parser.parse_args()

    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", args.version):
        raise ValueError("Version must have four numeric components")
    datetime.fromisoformat(args.timestamp.replace("Z", "+00:00"))
    meta = json.loads(args.meta.read_text(encoding="utf-8"))
    if meta["version"] != args.version:
        raise ValueError("Version does not match meta.json")
    notes = args.notes.read_text(encoding="utf-8").strip()
    changelog = notes.split("\n\n", 1)[0]
    if not changelog:
        raise ValueError("Release notes are empty")

    if args.manifest.exists():
        catalog = json.loads(args.manifest.read_text(encoding="utf-8"))
        if len(catalog) != 1 or catalog[0].get("guid") != meta["guid"]:
            raise ValueError("Existing manifest does not describe this plugin")
        entry = catalog[0]
    else:
        entry = {"versions": []}
        catalog = [entry]
    entry.update({
        "category": meta["category"],
        "guid": meta["guid"],
        "name": meta["name"],
        "description": meta["description"],
        "imageUrl": ICON_URL,
        "owner": meta["owner"],
        "overview": "自动归档 Bilibili 收藏夹视频",
    })
    versions = build_versions(args.version, args.assets, changelog, args.timestamp, meta)
    versions.extend(v for v in entry["versions"] if v["version"] != args.version)
    versions.sort(key=lambda v: (tuple(map(int, v["version"].split("."))),
                                 tuple(map(int, v["targetAbi"].split(".")))), reverse=True)
    entry["versions"] = versions
    args.manifest.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
