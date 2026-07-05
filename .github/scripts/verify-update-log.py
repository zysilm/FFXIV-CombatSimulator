#!/usr/bin/env python3
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def fail(message: str) -> None:
    print(f"::error::{message}")
    sys.exit(1)


def main() -> None:
    solution_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("CombatSimulator")
    plugin_name = sys.argv[2] if len(sys.argv) > 2 else solution_dir.name

    csproj_path = solution_dir / f"{plugin_name}.csproj"
    manifest_path = solution_dir / f"{plugin_name}.json"
    update_log_path = solution_dir / "UpdateLog" / "update-log.json"

    if not csproj_path.exists():
        fail(f"Missing project file: {csproj_path}")
    if not manifest_path.exists():
        fail(f"Missing plugin manifest: {manifest_path}")
    if not update_log_path.exists():
        fail(f"Missing update log: {update_log_path}")

    project = ET.parse(csproj_path).getroot()
    version_node = project.find(".//Version")
    if version_node is None or not (version_node.text or "").strip():
        fail(f"{csproj_path} must define <Version>.")

    version = version_node.text.strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        fail(f"Plugin version must be four-part for Dalamud, got '{version}'.")

    embedded_resources = [
        item.attrib.get("Include", "").replace("/", "\\")
        for item in project.findall(".//EmbeddedResource")
    ]
    if "UpdateLog\\update-log.json" not in embedded_resources:
        fail("UpdateLog\\update-log.json must be embedded in the plugin assembly.")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    assembly_version = str(manifest.get("AssemblyVersion", "")).strip()
    if assembly_version != version:
        fail(
            f"{manifest_path} AssemblyVersion '{assembly_version}' must match project version '{version}'."
        )

    update_logs = json.loads(update_log_path.read_text(encoding="utf-8"))
    if not isinstance(update_logs, list):
        fail("Update log must be a JSON array.")

    matching_entry = next(
        (entry for entry in update_logs if isinstance(entry, dict) and entry.get("version") == version),
        None,
    )
    if matching_entry is None:
        fail(f"Update log must contain an entry for version {version}.")

    if not str(matching_entry.get("title", "")).strip():
        fail(f"Update log entry for {version} must have a title.")

    changes = matching_entry.get("changes")
    if not isinstance(changes, list) or not any(str(change).strip() for change in changes):
        fail(f"Update log entry for {version} must have at least one non-empty change.")

    print(f"Update log verified for {plugin_name} {version}.")


if __name__ == "__main__":
    main()
