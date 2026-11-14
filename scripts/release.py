#!/usr/bin/env python3
"""Build the plugin for every Jellyfin line in lines.json, zip each build the
way Jellyfin's plugin catalog expects, and write the repository manifest.json.

    python3 scripts/release.py --version 0.1.0 --repo FreeStuffCrazy/jellyfin-plugin-freestuffcrazy \
        --tag v0.1.0 --out artifacts [--previous previous-manifest.json] [--changelog "..."]

Needs the .NET SDKs for every line (8, 9 and 10) and PyYAML. Plugin metadata
comes from build.yaml. We don't use jprm for the build because it cannot pass
-p:JellyfinVersion and rewrites <TargetFramework> itself.

Versions: each line gets a version A.B.C.N, where N is the line's position in
lines.json. Jellyfin installs the highest version whose targetAbi it satisfies,
so newer lines must have higher versions, and lines.json must only ever grow at
the end.
"""
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "src" / "Jellyfin.Plugin.FreeStuffCrazy" / "Jellyfin.Plugin.FreeStuffCrazy.csproj"


def slug(name):
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")


def version_key(version):
    return tuple(int(part) for part in version.split("."))


def publish(line_version, version, out_dir):
    out_dir.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            "dotnet", "publish", str(PROJECT), "-c", "Release", "-o", str(out_dir),
            f"-p:JellyfinVersion={line_version}",
            f"-p:Version={version}", f"-p:AssemblyVersion={version}", f"-p:FileVersion={version}",
        ],
        check=True,
    )


def md5(path):
    digest = hashlib.md5()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True, help="release version, A.B.C")
    parser.add_argument("--repo", default="FreeStuffCrazy/jellyfin-plugin-freestuffcrazy")
    parser.add_argument("--tag", help="release tag; zip URLs point at its assets (default v<version>)")
    parser.add_argument("--out", default="artifacts")
    parser.add_argument("--previous", help="the previous manifest.json, to keep older versions")
    parser.add_argument("--changelog", help="changelog text for these versions (default: build.yaml's)")
    args = parser.parse_args()

    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version):
        sys.exit(f"--version must look like 1.2.3, not {args.version!r}")
    tag = args.tag or f"v{args.version}"

    meta = yaml.safe_load((ROOT / "build.yaml").read_text())
    lines = json.loads((ROOT / "lines.json").read_text())
    if len({line["targetAbi"] for line in lines}) != len(lines):
        sys.exit("lines.json has a repeated targetAbi")
    abis = [version_key(line["targetAbi"]) for line in lines]
    if abis != sorted(abis):
        sys.exit("lines.json must list Jellyfin lines oldest first")

    out = Path(args.out).resolve()
    shutil.rmtree(out, ignore_errors=True)
    out.mkdir(parents=True)
    work = out / "_build"
    changelog = (args.changelog or meta["changelog"]).strip()
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    base = slug(meta["name"])

    versions = []
    for index, line in enumerate(lines):
        version = f"{args.version}.{index}"
        build_dir = work / line["jellyfin"]
        publish(line["jellyfin"], version, build_dir)

        plugin_meta = {
            "category": meta["category"],
            "changelog": changelog,
            "description": meta["description"].strip(),
            "guid": meta["guid"],
            "name": meta["name"],
            "overview": meta["overview"],
            "owner": meta["owner"],
            "targetAbi": line["targetAbi"],
            "timestamp": now,
            "version": version,
        }
        zip_path = out / f"{base}_{version}.zip"
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
            for artifact in meta["artifacts"]:
                source = build_dir / artifact
                if not source.is_file():
                    sys.exit(f"{artifact} was not produced for Jellyfin {line['jellyfin']}")
                archive.write(source, artifact)
            archive.writestr("meta.json", json.dumps(plugin_meta, indent=4, sort_keys=True) + "\n")
        versions.append({
            "version": version,
            "changelog": changelog,
            "targetAbi": line["targetAbi"],
            "sourceUrl": f"https://github.com/{args.repo}/releases/download/{tag}/{zip_path.name}",
            "checksum": md5(zip_path),
            "timestamp": now,
        })
        print(f"{zip_path.name}  Jellyfin {line['jellyfin']} (targetAbi {line['targetAbi']})")
    shutil.rmtree(work)

    entry = {
        "guid": meta["guid"],
        "name": meta["name"],
        "description": meta["description"].strip(),
        "overview": meta["overview"],
        "owner": meta["owner"],
        "category": meta["category"],
        "versions": versions,
    }
    if args.previous and Path(args.previous).is_file():
        try:
            previous = json.loads(Path(args.previous).read_text())
        except json.JSONDecodeError:
            sys.exit(f"{args.previous} is not valid JSON; refusing to drop the older versions it should hold")
        for old in previous:
            if old.get("guid") == meta["guid"]:
                new_versions = {item["version"] for item in versions}
                entry["versions"] += [item for item in old.get("versions", []) if item["version"] not in new_versions]
    entry["versions"].sort(key=lambda item: version_key(item["version"]), reverse=True)

    (out / "manifest.json").write_text(json.dumps([entry], indent=2) + "\n")
    print(f"manifest.json  {len(entry['versions'])} version(s)")


if __name__ == "__main__":
    main()
