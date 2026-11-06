"""Inspect actual release files, optionally comparing every source byte to Git."""
import argparse
import json
import re
import zipfile
from pathlib import Path
from package import ALLOWED, KEY, MANIFEST, VERSION, manifest, sha, snapshot

def inspect(directory, commit, repository=None):
    directory = Path(directory).resolve()
    expected = {"service-reliability-lab-v" + VERSION + ".zip", "service-reliability-demo.html", MANIFEST, KEY}
    sums = {}
    for line in (directory / "SHA256SUMS.txt").read_bytes().decode("ascii").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9._-]+)", line)
        if not match or match[2] in sums:
            raise ValueError("Invalid or duplicate checksum entry")
        sums[match[2]] = match[1]
    if set(sums) != expected:
        raise ValueError("Checksum product list differs")
    for name, digest in sums.items():
        path = directory / name
        if path.is_symlink() or not path.is_file() or path.stat().st_size > 2 * 1024 * 1024:
            raise ValueError("Unsafe or oversized release file")
        if sha(path.read_bytes()) != digest:
            raise ValueError("Release checksum mismatch: " + name)
    data = json.loads((directory / MANIFEST).read_bytes())
    if set(data) != {"schema", "version", "sourceCommit", "sourceTree", "scope", "files"}:
        raise ValueError("Unexpected manifest fields")
    if data["schema"] != "service-reliability-source-v1" or data["version"] != VERSION:
        raise ValueError("Unsupported manifest")
    if data["sourceCommit"] != commit or not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Wrong source commit")
    if not re.fullmatch(r"[0-9a-f]{40}", data["sourceTree"]):
        raise ValueError("Invalid source tree")
    entries = data["files"]
    inventory = {e["path"]: e for e in entries}
    if len(inventory) != len(entries) or set(inventory) != ALLOWED:
        raise ValueError("Source inventory differs from explicit allowlist")
    archive = directory / ("service-reliability-lab-v" + VERSION + ".zip")
    with zipfile.ZipFile(archive) as zipped:
        members = zipped.infolist()
        if len(members) != len(ALLOWED) + 1 or {i.filename for i in members} != ALLOWED | {MANIFEST}:
            raise ValueError("Duplicate or unexpected archive members")
        for info in members:
            if (info.compress_type != zipfile.ZIP_STORED or info.create_system != 3 or
                    info.date_time != (2026, 1, 1, 0, 0, 0) or info.external_attr != 0o100644 << 16 or
                    info.file_size > 2 * 1024 * 1024):
                raise ValueError("Noncanonical archive member")
        if zipped.read(MANIFEST) != (directory / MANIFEST).read_bytes():
            raise ValueError("Inner and outer manifests differ")
        files = {name: zipped.read(name) for name in ALLOWED}
    for name, raw in files.items():
        if inventory[name] != {"path": name, "bytes": len(raw), "sha256": sha(raw)}:
            raise ValueError("Source member mismatch: " + name)
    if files["demo/index.html"] != (directory / "service-reliability-demo.html").read_bytes():
        raise ValueError("Standalone HTML differs")
    if files[KEY] != (directory / KEY).read_bytes():
        raise ValueError("Public key differs")
    if repository is not None:
        source, tree = snapshot(Path(repository).resolve(), commit)
        if source != files or data != manifest(source, commit, tree, VERSION):
            raise ValueError("Actual ZIP does not equal the exact Git source")
    return {"status": "PASS", "sourceCommit": commit, "sourceTree": data["sourceTree"],
            "sourceFiles": len(files), "gitByteComparison": repository is not None,
            "archiveSha256": sha(archive.read_bytes()), "manifestSha256": sha((directory / MANIFEST).read_bytes())}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--repository", type=Path)
    args = parser.parse_args()
    print(json.dumps(inspect(args.directory, args.commit, args.repository), sort_keys=True))

if __name__ == "__main__":
    main()
