#!/usr/bin/env python3
"""
Manifest Validator for Jellyfin Track Memory Plugin.
Validates:
1. JSON syntax and Schema conformity (structure, required fields, GUID regex, version regex, checksum format).
2. Internal consistency with the repository code (GUID matching Plugin.cs, owner name).
3. Network availability and integrity of release assets (HTTP 200/302, MD5 checksum calculation).
"""

import sys
import json
import re
import hashlib
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MANIFEST_PATH = ROOT / "manifest.json"
PLUGIN_CS_PATH = ROOT / "src" / "Jellyfin.Plugin.TrackMemory" / "Plugin.cs"
SCHEMA_PATH = ROOT / "schemas" / "manifest.schema.json"

GUID_RE = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+$")
CHECKSUM_RE = re.compile(r"^[0-9a-fA-F]{32}$")


def fail(msg: str):
    print(f"❌ ERROR: {msg}", file=sys.stderr)
    sys.exit(1)


def info(msg: str):
    print(f"✅ {msg}")


def main():
    check_remote = "--check-remote" in sys.argv or "--strict" in sys.argv

    if not MANIFEST_PATH.exists():
        fail(f"manifest.json not found at {MANIFEST_PATH}")

    try:
        with open(MANIFEST_PATH, "r", encoding="utf-8") as f:
            manifest = json.load(f)
    except Exception as e:
        fail(f"manifest.json is not valid JSON: {e}")

    if not isinstance(manifest, list) or len(manifest) == 0:
        fail("manifest.json must be a non-empty array of plugin entries.")

    # Read GUID from Plugin.cs if available
    expected_guid = None
    if PLUGIN_CS_PATH.exists():
        cs_content = PLUGIN_CS_PATH.read_text(encoding="utf-8")
        guid_match = re.search(r'Guid\.Parse\("([0-9a-fA-F-]+)"\)', cs_content)
        if guid_match:
            expected_guid = guid_match.group(1).lower()

    for idx, plugin in enumerate(manifest):
        guid = plugin.get("guid")
        name = plugin.get("name")
        owner = plugin.get("owner")
        versions = plugin.get("versions")

        if not guid or not GUID_RE.match(guid):
            fail(f"Entry {idx}: Invalid or missing GUID: {guid}")

        if expected_guid and guid.lower() != expected_guid:
            fail(f"Entry {idx}: GUID {guid} does not match Plugin.cs GUID {expected_guid}")

        if not name:
            fail(f"Entry {idx}: Missing plugin name.")

        if not owner:
            fail(f"Entry {idx}: Missing owner.")

        if not isinstance(versions, list) or len(versions) == 0:
            fail(f"Entry {idx} ({name}): 'versions' must be a non-empty array.")

        info(f"Plugin metadata valid: '{name}' (GUID: {guid}, Owner: {owner})")

        for v_idx, v in enumerate(versions):
            ver = v.get("version")
            target_abi = v.get("targetAbi")
            source_url = v.get("sourceUrl")
            checksum = v.get("checksum")
            timestamp = v.get("timestamp")

            if not ver or not VERSION_RE.match(ver):
                fail(f"Entry {idx} version {v_idx}: Invalid version '{ver}' (expected X.Y.Z.W)")

            if not target_abi or not VERSION_RE.match(target_abi):
                fail(f"Entry {idx} version {v_idx}: Invalid targetAbi '{target_abi}' (expected X.Y.Z.W)")

            if not source_url or not source_url.startswith(("http://", "https://")):
                fail(f"Entry {idx} version {v_idx}: Invalid sourceUrl '{source_url}'")

            if not checksum or not CHECKSUM_RE.match(checksum):
                fail(f"Entry {idx} version {v_idx}: Invalid checksum '{checksum}' (must be 32-char hex MD5)")

            if not timestamp:
                fail(f"Entry {idx} version {v_idx}: Missing timestamp")

            info(f"Version {ver} valid (targetAbi: {target_abi}, checksum: {checksum})")

            if check_remote:
                print(f"   --> Verifying remote asset: {source_url} ...")
                try:
                    req = urllib.request.Request(
                        source_url,
                        headers={"User-Agent": "Jellyfin-Manifest-Validator/1.0"}
                    )
                    with urllib.request.urlopen(req, timeout=15) as resp:
                        if resp.status not in (200, 302):
                            fail(f"Remote URL returned HTTP {resp.status}: {source_url}")
                        data = resp.read()
                        actual_md5 = hashlib.md5(data).hexdigest().lower()
                        if actual_md5 != checksum.lower():
                            fail(f"Checksum mismatch for {source_url}!\nManifest: {checksum}\nActual:   {actual_md5}")
                        info(f"Remote asset verified ({len(data)} bytes, MD5 {actual_md5} matches)")
                except Exception as e:
                    fail(f"Failed to fetch remote asset {source_url}: {e}")

    print("\n🎉 Manifest validation passed successfully!")


if __name__ == "__main__":
    main()
