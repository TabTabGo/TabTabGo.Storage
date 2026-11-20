"""Tests for the release verification logic (scripts/release_verify.py) and for the workflows that call it.

Run: python3 -m unittest scripts/test_release_verify.py
"""
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest
import zipfile

SCRIPTS = pathlib.Path(__file__).resolve().parent
REPO = SCRIPTS.parent
sys.path.insert(0, str(SCRIPTS))
import release_verify as rv  # noqa: E402


def props(tfm: str, version: str) -> str:
    return f"<Project><PropertyGroup><TargetFramework>{tfm}</TargetFramework><Version>{version}</Version></PropertyGroup></Project>"


def branches(*names: str) -> str:
    return "\n".join(f"  origin/{n}" for n in names)


class TagTests(unittest.TestCase):
    CASES = [
        ("v8.0.0", "net8.0", "8.0.0", "releases/v8.0.x/v8.0.0", "8.0.0", False),
        ("v10.0.0", "net10.0", "10.0.0", "releases/v10.0.x/v10.0.0", "10.0.0", False),
        ("v11.0.0-rc.1", "net11.0", "11.0.0-rc.1", "releases/v11.0.x/v11.0.0-rc.1", "11.0.0-rc.1", True),
        ("v11.0.0", "net11.0", "11.0.0", "releases/v11.0.x/v11.0.0", "11.0.0", False),
        ("v11.2.3-beta.4", "net11.0", "11.2.3-beta.4", "releases/v11.2.x/v11.2.3", "11.2.3-beta.4", True),
    ]

    def test_valid_tags_pass(self):
        for tag, tfm, declared, branch, version, prerelease in self.CASES:
            with self.subTest(tag=tag):
                result = rv.check_tag(tag, props(tfm, declared), branches("main", branch))
                self.assertEqual(version, result.version)
                self.assertEqual(prerelease, result.prerelease)

    def test_the_declared_version_is_compared_with_the_full_tag_version(self):
        # Regression: the suffix used to be stripped from the tag, so neither -rc.1 nor the stable tag could match a -rc.1 Version.
        with self.assertRaises(rv.ReleaseError):
            rv.check_tag("v11.0.0", props("net11.0", "11.0.0-rc.1"), branches("releases/v11.0.x/v11.0.0"))
        with self.assertRaises(rv.ReleaseError):
            rv.check_tag("v11.0.0-rc.1", props("net11.0", "11.0.0"), branches("releases/v11.0.x/v11.0.0-rc.1"))
        with self.assertRaises(rv.ReleaseError):
            rv.check_tag("v11.0.0-rc.2", props("net11.0", "11.0.0-rc.1"), branches("releases/v11.0.x/v11.0.0-rc.1"))

    def test_target_framework_major_must_match_the_tag_major(self):
        with self.assertRaises(rv.ReleaseError):
            rv.check_tag("v10.0.0", props("net8.0", "10.0.0"), branches("releases/v10.0.x/v10.0.0"))
        with self.assertRaises(rv.ReleaseError):
            rv.check_tag("v11.0.0-rc.1", props("net10.0", "11.0.0-rc.1"), branches("releases/v11.0.x/v11.0.0-rc.1"))

    def test_the_commit_must_be_on_a_release_branch_of_the_same_major_minor(self):
        for bad in ("releases/v10.0.x/v10.0.0", "releases/v11.1.x/v11.1.0", "feature/x", "main"):
            with self.subTest(branch=bad), self.assertRaises(rv.ReleaseError):
                rv.check_tag("v11.0.0-rc.1", props("net11.0", "11.0.0-rc.1"), branches(bad))
        # the branch is derived from the core part: the suffix never takes part
        rv.check_tag("v11.0.0-rc.1", props("net11.0", "11.0.0-rc.1"), branches("releases/v11.0.x/v11.0.0"))

    def test_malformed_tags_are_refused(self):
        for tag in ("v11.0", "11.0.0", "v1.2.3-rc", "v1.2.3-gamma.1", "v1.2.3\nextra", "v1.2.3 ", "v1.2.3-rc.1-x", ""):
            with self.subTest(tag=repr(tag)), self.assertRaises(rv.ReleaseError):
                rv.parse_tag(tag)


class PackageSetTests(unittest.TestCase):
    IDS = ["Pkg.A", "Pkg.B"]

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.out = pathlib.Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def pack(self, package_id, version="11.0.0-rc.1", tfm="net11.0", readme=True, symbols=True):
        with zipfile.ZipFile(self.out / f"{package_id}.{version}.nupkg", "w") as z:
            z.writestr(f"{package_id}.nuspec", f"<package><metadata><version>{version}</version></metadata></package>")
            z.writestr(f"lib/{tfm}/{package_id}.dll", b"x")
            if readme:
                z.writestr("README.md", "r")
        if symbols:
            (self.out / f"{package_id}.{version}.snupkg").write_bytes(b"s")

    def full_set(self, **kwargs):
        for i in self.IDS:
            self.pack(i, **kwargs)

    def check(self):
        rv.check_packages(self.out, self.IDS, "11.0.0-rc.1", "net11.0")

    def test_complete_set_passes(self):
        self.full_set()
        self.check()

    def test_each_defect_is_reported(self):
        for name, kwargs in (("wrong tfm", {"tfm": "net8.0"}), ("no readme", {"readme": False}), ("no symbols", {"symbols": False})):
            with self.subTest(name):
                self.tmp.cleanup()
                self.tmp = tempfile.TemporaryDirectory()
                self.out = pathlib.Path(self.tmp.name)
                self.pack("Pkg.A")
                self.pack("Pkg.B", **kwargs)
                with self.assertRaises(rv.ReleaseError):
                    self.check()

    def test_missing_package_and_wrong_version_are_reported(self):
        self.pack("Pkg.A")
        with self.assertRaises(rv.ReleaseError):
            self.check()
        self.pack("Pkg.B")
        (self.out / "Pkg.B.11.0.0-rc.1.nupkg").unlink()
        with zipfile.ZipFile(self.out / "Pkg.B.11.0.0-rc.1.nupkg", "w") as z:
            z.writestr("Pkg.B.nuspec", "<version>9.9.9</version>")
            z.writestr("lib/net11.0/x.dll", b"x")
            z.writestr("README.md", "r")
        with self.assertRaises(rv.ReleaseError):
            self.check()

    def test_the_check_is_stable_over_300_runs_of_the_real_command(self):
        # Regression: `unzip -l | grep -q` under pipefail failed 31-42 times in 300 runs (SIGPIPE when grep -q exits early).
        self.full_set()
        failures = 0
        for _ in range(300):
            result = subprocess.run(
                [sys.executable, str(SCRIPTS / "release_verify.py"), "packages", "--dir", str(self.out), "--expected", " ".join(self.IDS),
                 "--version", "11.0.0-rc.1", "--tfm", "net11.0"], capture_output=True, text=True)
            failures += result.returncode != 0
        self.assertEqual(0, failures)


class WorkflowHygieneTests(unittest.TestCase):
    def test_no_pipeline_ends_in_grep_q(self):
        # `x | grep -q` under `set -o pipefail` fails randomly with SIGPIPE; capture first or use a here-string / [[ =~ ]].
        offenders = []
        for path in sorted((REPO / ".github" / "workflows").glob("*.yml")) + sorted(SCRIPTS.glob("*.sh")):
            for number, line in enumerate(path.read_text().splitlines(), 1):
                code = line.split("#", 1)[0]
                if re.search(r"\|\s*grep\s+(-[a-zA-Z]*q|-[a-zA-Z]*\s+-[a-zA-Z]*q)", code) or re.search(r"\|\s*grep\s+-[a-zA-Z]*q", code):
                    offenders.append(f"{path.name}:{number}: {line.strip()}")
        self.assertEqual([], offenders)

    def test_release_workflow_uses_the_script_for_tag_and_package_checks(self):
        text = (REPO / ".github" / "workflows" / "release.yml").read_text()
        self.assertIn("scripts/release_verify.py tag", text)
        self.assertIn("scripts/release_verify.py packages", text)
        self.assertNotIn('core="${version%%-*}"', text)


if __name__ == "__main__":
    unittest.main()
