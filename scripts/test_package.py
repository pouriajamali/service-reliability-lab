"""Bounded packaging contract checks; fixtures contain only public or synthetic data."""
import os
import shutil
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path
from package import ALLOWED, ROOT, VERSION, build, sha
from check_package import inspect

class PackageContracts(unittest.TestCase):
    def setUp(self):
        self.base = Path(tempfile.mkdtemp(prefix="package-contract-", dir=ROOT / "artifacts"))
        self.repo = self.base / "source"
        self.repo.mkdir()
        for name in ALLOWED:
            target = self.repo / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        self.git("init", "-q", "-b", "fixture")
        self.git("config", "user.name", "Synthetic package fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "commit.gpgsign", "false")
        self.git("config", "core.autocrlf", "false")
        self.git("config", "core.symlinks", "false")
        self.git("add", ".")
        self.git("commit", "-qm", "Synthetic fixture")
        self.commit = self.git("rev-parse", "HEAD").decode().strip()

    def git(self, *args, data=None):
        env = dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM="1")
        return subprocess.check_output(["git", "-C", str(self.repo), *args], input=data,
                                       stderr=subprocess.PIPE, env=env)

    def output(self, name="output"):
        return self.base / name

    def reject_before_output(self):
        with self.assertRaises(ValueError):
            build(self.repo, self.output(), self.commit)
        self.assertFalse(self.output().exists())

    def refresh_zip_checksum(self):
        directory = self.output()
        path = directory / ("service-reliability-lab-v" + VERSION + ".zip")
        sums = directory / "SHA256SUMS.txt"
        lines = []
        for line in sums.read_text(encoding="ascii").splitlines():
            digest, name = line.split("  ")
            lines.append((sha(path.read_bytes()) if name == path.name else digest) + "  " + name)
        sums.write_bytes(("\n".join(lines) + "\n").encode("ascii"))

    def test_clean_exact_git_bytes_and_determinism(self):
        a = build(self.repo, self.output("first"), self.commit)
        b = build(self.repo, self.output("second"), self.commit)
        self.assertEqual(a, b)
        self.assertEqual((self.output("first") / a["archive"]).read_bytes(),
                         (self.output("second") / b["archive"]).read_bytes())
        self.assertTrue(inspect(self.output("first"), self.commit, self.repo)["gitByteComparison"])

    def test_untracked_private_file_rejected(self):
        (self.repo / ".env").write_text("EXAMPLE=SYNTHETIC-NOT-A-SECRET\n", encoding="ascii")
        self.reject_before_output()

    def test_ci_policy_members_are_exact_git_bytes(self):
        additions = {".github/ci/policy.py", ".github/ci/test_policy.py", "docs/CI-POLICY.md"}
        missing = [name for name in additions if not (self.repo / name).exists()]
        for name in missing:
            target = self.repo / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        if missing:
            self.git("add", "--", *missing)
            self.git("commit", "-qm", "Synthetic exact CI policy additions")
            self.commit = self.git("rev-parse", "HEAD").decode().strip()
        proof = build(self.repo, self.output(), self.commit)
        with zipfile.ZipFile(self.output() / proof["archive"]) as zipped:
            self.assertTrue(additions.issubset(zipped.namelist()))
            for name in additions:
                self.assertEqual(zipped.read(name), self.git("show", self.commit + ":" + name))
        self.assertTrue(inspect(self.output(), self.commit, self.repo)["gitByteComparison"])

    def test_missing_required_ci_policy_member_rejected(self):
        self.git("rm", "--", "docs/CI-POLICY.md")
        self.git("commit", "-qm", "Synthetic missing required policy document")
        self.commit = self.git("rev-parse", "HEAD").decode().strip()
        self.reject_before_output()

    def test_unknown_ci_policy_neighbor_rejected(self):
        self.add_disallowed_member(".github/ci/not-reviewed.py")

    def test_dirty_tracked_source_rejected(self):
        with (self.repo / "README.md").open("ab") as stream:
            stream.write(b"\nSynthetic dirty source\n")
        self.reject_before_output()

    def add_disallowed_member(self, name):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"SYNTHETIC-AUXILIARY-NOT-A-SECRET\n")
        self.git("add", "--force", name)
        self.git("commit", "-qm", "Synthetic non-allowlisted file")
        self.commit = self.git("rev-parse", "HEAD").decode().strip()
        self.reject_before_output()

    def test_tracked_private_member_rejected(self):
        self.add_disallowed_member(".env")

    def test_tracked_generated_member_rejected(self):
        self.add_disallowed_member("src/Reliability/bin/unexpected.dll")

    def test_tracked_symlink_mode_rejected(self):
        target = b"outside-synthetic-target"
        (self.repo / "README.md").write_bytes(target)
        blob = self.git("hash-object", "-w", "--stdin", data=target).decode().strip()
        self.git("update-index", "--cacheinfo", "120000," + blob + ",README.md")
        self.git("commit", "-qm", "Synthetic symlink index mode")
        self.commit = self.git("rev-parse", "HEAD").decode().strip()
        # core.symlinks=false represents the committed link as a regular file on Windows.
        self.assertEqual(self.git("status", "--porcelain"), b"")
        self.reject_before_output()

    def test_ignored_build_outputs_are_excluded(self):
        path = self.repo / "artifacts/private-synthetic.txt"
        path.parent.mkdir()
        path.write_bytes(b"SYNTHETIC-ONLY")
        proof = build(self.repo, self.output(), self.commit)
        with zipfile.ZipFile(self.output() / proof["archive"]) as zipped:
            self.assertNotIn("artifacts/private-synthetic.txt", zipped.namelist())
        inspect(self.output(), self.commit, self.repo)

    def test_wrong_commit_rejected(self):
        self.commit = "0" * 40
        self.reject_before_output()

    def test_unexpected_zip_member_rejected_even_with_updated_checksum(self):
        proof = build(self.repo, self.output(), self.commit)
        with zipfile.ZipFile(self.output() / proof["archive"], "a") as zipped:
            zipped.writestr(".env", "SYNTHETIC-NOT-A-SECRET")
        self.refresh_zip_checksum()
        with self.assertRaises(ValueError):
            inspect(self.output(), self.commit, self.repo)

    def test_standalone_html_tamper_rejected(self):
        build(self.repo, self.output(), self.commit)
        (self.output() / "service-reliability-demo.html").write_bytes(b"SYNTHETIC-TAMPER")
        with self.assertRaises(ValueError):
            inspect(self.output(), self.commit, self.repo)

    def test_existing_output_preserved(self):
        build(self.repo, self.output(), self.commit)
        before = (self.output() / "SHA256SUMS.txt").read_bytes()
        with self.assertRaises(FileExistsError):
            build(self.repo, self.output(), self.commit)
        self.assertEqual((self.output() / "SHA256SUMS.txt").read_bytes(), before)

if __name__ == "__main__":
    (ROOT / "artifacts").mkdir(exist_ok=True)
    unittest.main(verbosity=2)
