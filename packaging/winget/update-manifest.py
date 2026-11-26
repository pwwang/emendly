#!/usr/bin/env python3
"""Generate WinGet 1.6.0 multi-file manifests for a new Emendly release."""

from __future__ import annotations

import argparse
import datetime
import hashlib
import re
import sys
from pathlib import Path

PACKAGE_ID = "pwwang.Emendly"
MANIFEST_VERSION = "1.6.0"
REPO_URL = "https://github.com/pwwang/emendly"

SEMVER_RE = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")
SHA256_RE = re.compile(r"^[0-9A-Fa-f]{64}$")
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")


def compute_sha256(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            hasher.update(chunk)
    return hasher.hexdigest().upper()


def render_version_manifest(version: str) -> str:
    return (
        f"# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.{MANIFEST_VERSION}.schema.json\n"
        "\n"
        f"PackageIdentifier: {PACKAGE_ID}\n"
        f"PackageVersion: {version}\n"
        "DefaultLocale: en-US\n"
        "ManifestType: version\n"
        f"ManifestVersion: {MANIFEST_VERSION}\n"
    )


def render_installer_manifest(version: str, sha256: str, release_date: str) -> str:
    installer_url = f"{REPO_URL}/releases/download/v{version}/Emendly-v{version}-win-x64-setup.exe"
    return (
        f"# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.{MANIFEST_VERSION}.schema.json\n"
        "\n"
        f"PackageIdentifier: {PACKAGE_ID}\n"
        f"PackageVersion: {version}\n"
        "InstallerLocale: en-US\n"
        "InstallerType: inno\n"
        "Scope: user\n"
        "InstallModes:\n"
        "  - interactive\n"
        "  - silent\n"
        "  - silentWithProgress\n"
        "InstallerSwitches:\n"
        "  Silent: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART\n"
        "  SilentWithProgress: /SILENT /SUPPRESSMSGBOXES /NORESTART\n"
        "UpgradeBehavior: install\n"
        f"ReleaseDate: {release_date}\n"
        "Installers:\n"
        "  - Architecture: x64\n"
        f"    InstallerUrl: {installer_url}\n"
        f"    InstallerSha256: {sha256}\n"
        "ManifestType: installer\n"
        f"ManifestVersion: {MANIFEST_VERSION}\n"
    )


def render_locale_manifest(version: str) -> str:
    return (
        f"# yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.{MANIFEST_VERSION}.schema.json\n"
        "\n"
        f"PackageIdentifier: {PACKAGE_ID}\n"
        f"PackageVersion: {version}\n"
        "PackageLocale: en-US\n"
        "Publisher: pwwang\n"
        "PublisherUrl: https://emendly.dev\n"
        f"PublisherSupportUrl: {REPO_URL}/issues\n"
        "PrivacyUrl: https://emendly.dev/privacy.html\n"
        "Author: pwwang\n"
        "PackageName: Emendly\n"
        "PackageUrl: https://emendly.dev\n"
        "License: MIT\n"
        f"LicenseUrl: {REPO_URL}/blob/main/LICENSE\n"
        "ShortDescription: System-wide AI writing assistant for Windows\n"
        "Description: >-\n"
        "  Emendly is a lightweight, on-demand, system-wide writing assistant for Windows.\n"
        "  Press a global hotkey in any text field to proofread, rewrite, compose, translate,\n"
        "  or find synonyms using your own OpenAI-compatible API endpoint.\n"
        "Moniker: emendly\n"
        "Tags:\n"
        "  - ai\n"
        "  - writing\n"
        "  - proofread\n"
        "  - rewrite\n"
        "  - translate\n"
        "  - productivity\n"
        "  - desktop\n"
        f"ReleaseNotesUrl: {REPO_URL}/releases/tag/v{version}\n"
        "ManifestType: defaultLocale\n"
        f"ManifestVersion: {MANIFEST_VERSION}\n"
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate WinGet 1.6.0 manifests for an Emendly release."
    )
    parser.add_argument(
        "--version",
        required=True,
        help="Release version (for example 0.2.0 or v0.2.0).",
    )
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument(
        "--installer",
        type=Path,
        help="Path to Emendly-v<version>-win-x64-setup.exe to hash.",
    )
    source.add_argument(
        "--sha256",
        help="Precomputed 64-character SHA-256 hex digest of the setup installer.",
    )
    parser.add_argument(
        "--release-date",
        default=datetime.date.today().isoformat(),
        help="Release date in YYYY-MM-DD format (default: today).",
    )
    parser.add_argument(
        "--output-root",
        type=Path,
        default=Path(__file__).resolve().parent / "manifests",
        help="Root directory for WinGet manifests (default: packaging/winget/manifests).",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print generated YAML documents to stdout without writing files.",
    )
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)

    version = args.version.strip().lstrip("vV")
    if not SEMVER_RE.match(version):
        print(f"error: invalid semantic version '{args.version}'", file=sys.stderr)
        return 2

    if not DATE_RE.match(args.release_date):
        print(f"error: --release-date must be YYYY-MM-DD, got '{args.release_date}'", file=sys.stderr)
        return 2

    if args.installer is not None:
        if not args.installer.is_file():
            print(f"error: installer file not found: {args.installer}", file=sys.stderr)
            return 2
        sha256 = compute_sha256(args.installer)
    else:
        raw_sha = args.sha256.strip()
        if not SHA256_RE.match(raw_sha):
            print("error: --sha256 must be a 64-character hex digest", file=sys.stderr)
            return 2
        sha256 = raw_sha.upper()

    target_dir = args.output_root / "p" / "pwwang" / "Emendly" / version
    files = [
        (target_dir / f"{PACKAGE_ID}.yaml", render_version_manifest(version)),
        (target_dir / f"{PACKAGE_ID}.installer.yaml", render_installer_manifest(version, sha256, args.release_date)),
        (target_dir / f"{PACKAGE_ID}.locale.en-US.yaml", render_locale_manifest(version)),
    ]

    if args.dry_run:
        for index, (path, content) in enumerate(files):
            if index > 0:
                print("---")
            print(f"# {path}")
            print(content, end="")
        return 0

    target_dir.mkdir(parents=True, exist_ok=True)
    for path, content in files:
        path.write_text(content, encoding="utf-8", newline="\n")
        print(f"wrote {path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
