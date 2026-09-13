"""Validate and package the FabrCore 2.0 development skills using Python's stdlib.

Run from any directory. --output may point at a website's wwwroot/downloads folder.
No repository files are modified; generated ZIP contents have deterministic timestamps.
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
import zipfile
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[1]
SKILLS = ROOT / "docs" / "skills"
VERSION = "2.0.0"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "skills")
    args = parser.parse_args()
    bundled = {}
    names = []
    for folder in sorted(SKILLS.iterdir(), key=lambda p: p.name.lower()):
        entry = folder / "SKILL.md"
        if not entry.is_file():
            continue
        content = entry.read_text(encoding="utf-8-sig")
        front = re.match(r"\A---\s*\n(.*?)\n---\s*\n", content, re.S)
        if not front:
            raise ValueError(f"Missing frontmatter: {entry}")
        name_match = re.search(r"^name:\s*([a-z0-9-]+)\s*$", front[1], re.M)
        if not name_match or not re.search(r"^description:\s*\S", front[1], re.M):
            raise ValueError(f"Missing name/description: {entry}")
        name = name_match[1]
        if name in names or len(name) > 64:
            raise ValueError(f"Invalid/duplicate name: {name}")
        if not re.search(r"^  version: 2\.0\.0\s*$", front[1], re.M):
            raise ValueError(f"Unexpected release version: {entry}")
        names.append(name)
        for file in sorted(folder.rglob("*")):
            if not file.is_file():
                continue
            relative = file.relative_to(folder)
            if any(part.startswith(".") or part == "__pycache__" for part in relative.parts):
                raise ValueError(f"Unexpected hidden/cache file in distribution: {file}")
            archive_path = f"{name}/{relative.as_posix()}"
            if file.suffix.lower() == ".md":
                for target in re.findall(r"\]\(([^)]+)\)", file.read_text(encoding="utf-8-sig")):
                    target = unquote(target.split("#", 1)[0])
                    if not target or re.match(r"[a-z]+:", target):
                        continue
                    resolved = (file.parent / target).resolve()
                    if not resolved.is_relative_to(SKILLS.resolve()) or not resolved.exists():
                        raise ValueError(f"Non-portable/broken link in {file}: {target}")
            bundled[archive_path] = file.read_bytes()

    # Detect stale maintained protocol/migration copies before creating an archive.
    administration = (ROOT / "docs/cloud-administration.md").read_text(encoding="utf-8-sig")
    administration = administration.replace("(cloud-server-protocol.md)", "(transport.md)").replace(
        "(migrations/monitoring.sql)", "(../assets/monitoring.sql)")
    transport = (ROOT / "docs/cloud-server-protocol.md").read_text(encoding="utf-8-sig").replace(
        "(cloud-administration.md)", "(administration.md)")
    checks = {
        "fabrcore-connections/references/integration.md": (ROOT / "docs/connections-and-microsoft-integration.md").read_text(encoding="utf-8-sig").replace("(migrations/data-protection.sql)", "(../assets/data-protection.sql)"),
        "fabrcore-connections/assets/data-protection.sql": (ROOT / "docs/migrations/data-protection.sql").read_text(encoding="utf-8-sig"),
        "fabrcore-cloud-administration/references/administration.md": administration,
        "fabrcore-cloud-administration/references/transport.md": transport,
        "fabrcore-cloud-administration/assets/monitoring.sql": (ROOT / "docs/migrations/monitoring.sql").read_text(encoding="utf-8-sig"),
        "fabrcore-releases/scripts/monitoring.sql": (ROOT / "docs/migrations/monitoring.sql").read_text(encoding="utf-8-sig"),
        "fabrcore-releases/scripts/Migrate-Acl.ps1": (ROOT / "scripts/Migrate-Acl.ps1").read_text(encoding="utf-8-sig"),
    }
    for name, expected in checks.items():
        actual = bundled[name].decode("utf-8-sig").replace("\r\n", "\n")
        if actual != expected.replace("\r\n", "\n"):
            raise ValueError(f"Stale bundled reference: {name}")

    manifest = {
        "version": VERSION,
        "skills": names,
        "files": {name: hashlib.sha256(data).hexdigest() for name, data in sorted(bundled.items())},
    }
    bundled["manifest.json"] = (json.dumps(manifest, indent=2) + "\n").encode()
    args.output.mkdir(parents=True, exist_ok=True)
    archive = args.output / f"fabrcore-skills-{VERSION}.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as output:
        for name, data in sorted(bundled.items()):
            info = zipfile.ZipInfo(name, date_time=(2026, 9, 12, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            output.writestr(info, data)
    with zipfile.ZipFile(archive) as check:
        if check.testzip() is not None:
            raise ValueError("Archive integrity check failed")
        for name, digest in manifest["files"].items():
            if hashlib.sha256(check.read(name)).hexdigest() != digest:
                raise ValueError(f"Archive hash mismatch: {name}")
    print(f"Validated {len(names)} skills and {len(manifest['files'])} files: {archive}")


if __name__ == "__main__":
    main()
