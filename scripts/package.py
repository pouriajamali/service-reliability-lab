"""Build an exact, clean Git source release; never scan arbitrary workspace files."""
import argparse
import hashlib
import json
import re
import subprocess
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VERSION = "0.1.2-preview.1"
ALLOWED = frozenset({
    ".editorconfig", ".gitattributes", ".gitignore", ".github/workflows/verify.yml",
    ".github/ci/policy.py", ".github/ci/test_policy.py", "docs/CI-POLICY.md",
    "README.md", "CHANGELOG.md", "CONTRIBUTING.md", "LICENSE", "SIGNING-PUBLIC-KEY.pub",
    "docs/DESIGN.md", "demo/index.html", "demo/template.html", "demo/traces.json",
    "src/Reliability/ReadPolicy.cs", "src/Reliability/Reliability.csproj",
    "src/Demo/Program.cs", "src/Demo/Demo.csproj",
    "tests/Verify/Program.cs", "tests/Verify/Verify.csproj",
    "scripts/package.py", "scripts/check_package.py", "scripts/test_package.py",
    "scripts/build_demo.py", "scripts/compare_traces.py",
})
MANIFEST = "BUILD-MANIFEST.json"
KEY = "SIGNING-PUBLIC-KEY.pub"

def sha(data):
    return hashlib.sha256(data).hexdigest()

def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], stderr=subprocess.PIPE)

def clean(root, commit):
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("A full exact commit SHA is required")
    if git(root, "rev-parse", "HEAD").decode().strip() != commit:
        raise ValueError("HEAD does not match the requested commit")
    if git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all"):
        raise ValueError("Dirty or untracked source; preserve it and use a clean checkout")

def snapshot(root, commit):
    clean(root, commit)
    files = {}
    for entry in git(root, "ls-tree", "-rz", "--full-tree", commit).split(b"\0"):
        if not entry:
            continue
        header, path = entry.split(b"\t", 1)
        mode, kind, _ = header.split()
        name = path.decode("utf-8")
        if mode != b"100644" or kind != b"blob" or name not in ALLOWED:
            raise ValueError("Non-allowlisted, generated, unsafe or symlink member: " + name)
        current = root / name
        if current.is_symlink() or any(parent.is_symlink() for parent in current.parents):
            raise ValueError("Symlink source path: " + name)
        files[name] = git(root, "show", commit + ":" + name)
    if set(files) != ALLOWED:
        raise ValueError("Required source allowlist differs from Git tree")
    clean(root, commit)
    return files, git(root, "rev-parse", commit + "^{tree}").decode().strip()

def manifest(files, commit, tree, version):
    return {"schema": "service-reliability-source-v1", "version": version,
            "sourceCommit": commit, "sourceTree": tree,
            "scope": "Synthetic source and fixture replay; no executable binaries or performance claim.",
            "files": [{"path": name, "bytes": len(data), "sha256": sha(data)}
                      for name, data in sorted(files.items())]}

def build(root, output, commit, version=VERSION):
    root, output = Path(root).resolve(), Path(output).resolve()
    if version != VERSION:
        raise ValueError("Unsupported release version")
    files, tree = snapshot(root, commit)
    payload = json.dumps(manifest(files, commit, tree, version), indent=2, ensure_ascii=True).encode() + b"\n"
    output.mkdir(parents=True, exist_ok=False)
    archive = output / ("service-reliability-lab-v" + version + ".zip")
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_STORED) as zipped:
        for name, data in sorted({**files, MANIFEST: payload}.items()):
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.compress_type = zipfile.ZIP_STORED
            info.external_attr = 0o100644 << 16
            zipped.writestr(info, data)
    products = {archive.name: archive.read_bytes(), "service-reliability-demo.html": files["demo/index.html"],
                MANIFEST: payload, KEY: files[KEY]}
    for name, data in products.items():
        if name != archive.name:
            (output / name).write_bytes(data)
    clean(root, commit)
    sums = "".join(sha(data) + "  " + name + "\n" for name, data in sorted(products.items()))
    (output / "SHA256SUMS.txt").write_bytes(sums.encode("ascii"))
    return {"sourceCommit": commit, "sourceTree": tree, "version": version, "sourceFiles": len(files),
            "archive": archive.name, "archiveSha256": sha(products[archive.name]),
            "archiveBytes": len(products[archive.name]), "manifestSha256": sha(payload)}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--output", type=Path, default=ROOT / ("artifacts/release-v" + VERSION))
    args = parser.parse_args()
    print(json.dumps(build(ROOT, args.output, args.commit), sort_keys=True))

if __name__ == "__main__":
    main()
