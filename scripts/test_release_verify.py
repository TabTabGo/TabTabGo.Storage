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
        ("v8.0.0", "net8.0", "8.0.0", "releases/v8.0.x/v8.0.0"),
        ("v10.0.0", "net10.0", "10.0.0", "releases/v10.0.x/v10.0.0"),
        ("v11.0.0", "net11.0", "11.0.0", "releases/v11.0.x/v11.0.0"),
        ("v11.2.3", "net11.0", "11.2.3", "releases/v11.2.x/v11.2.3"),
    ]

    def test_valid_tags_pass(self):
        for tag, tfm, declared, branch in self.CASES:
            with self.subTest(tag=tag):
                result = rv.check_tag(tag, props(tfm, declared), branches("main", branch))
                self.assertEqual(tag[1:], result.version)

    def test_the_declared_version_must_equal_the_tag_version(self):
        with self.assertRaises(rv.ReleaseError):
            rv.check_tag("v11.0.0", props("net11.0", "11.0.0-rc.1"), branches("releases/v11.0.x/v11.0.0"))
        with self.assertRaises(rv.ReleaseError):
            rv.check_tag("v11.0.1", props("net11.0", "11.0.0"), branches("releases/v11.0.x/v11.0.1"))

    def test_target_framework_major_must_match_the_tag_major(self):
        with self.assertRaises(rv.ReleaseError):
            rv.check_tag("v10.0.0", props("net8.0", "10.0.0"), branches("releases/v10.0.x/v10.0.0"))
        with self.assertRaises(rv.ReleaseError):
            rv.check_tag("v11.0.0", props("net10.0", "11.0.0"), branches("releases/v11.0.x/v11.0.0"))

    def test_the_commit_must_be_on_a_release_branch_of_the_same_major_minor(self):
        for bad in ("releases/v10.0.x/v10.0.0", "releases/v11.1.x/v11.1.0", "feature/x", "main"):
            with self.subTest(branch=bad), self.assertRaises(rv.ReleaseError):
                rv.check_tag("v11.0.0", props("net11.0", "11.0.0"), branches(bad))

    def test_pre_release_and_build_metadata_tags_are_refused_with_the_stable_only_message(self):
        # Every release is stable: no -rc / -beta / -alpha / -preview tag and no +build metadata, even when Directory.Build.props
        # declares the same version and the branch exists.
        for tag in ("v11.0.0-rc.1", "v11.0.0-beta.1", "v11.0.0-alpha.1", "v11.0.0-preview.1", "v11.0.0-rc", "v1.2.3-gamma.1",
                    "v11.0.0-rc.1-x", "v1.2.3-rc.01", "v1.2.3-rc.0", "v11.0.0+x", "v11.0.0+build.5", "v11.0.0-rc.1+x"):
            with self.subTest(tag=tag):
                with self.assertRaises(rv.ReleaseError) as raised:
                    rv.parse_tag(tag)
                self.assertIn("only stable vMAJOR.MINOR.PATCH tags are released", str(raised.exception))
                with self.assertRaises(rv.ReleaseError):
                    rv.check_tag(tag, props("net11.0", tag[1:]), branches("releases/v11.0.x/v11.0.0"))

    def test_malformed_tags_are_refused(self):
        for tag in ("v11.0", "11.0.0", "v1.2.3\nextra", "v1.2.3 ", "v1.2.3-\nrc", ""):
            with self.subTest(tag=repr(tag)), self.assertRaises(rv.ReleaseError):
                rv.parse_tag(tag)


class TagStrictnessTests(unittest.TestCase):
    def test_non_ascii_digits_and_leading_zeros_are_refused(self):
        # \d matches Arabic-Indic digits and the old pattern accepted leading zeros: v010.0.0 is not canonical.
        for tag in ("v\u0661\u0660.0.0", "v010.0.0", "v1.02.3", "v1.2.03", "v00.0.0"):
            with self.subTest(tag=ascii(tag)), self.assertRaises(rv.ReleaseError):
                rv.parse_tag(tag)

    def test_zero_components_are_fine(self):
        for tag in ("v0.0.0", "v10.0.10", "v1.2.3"):
            with self.subTest(tag=tag):
                self.assertEqual(tag[1:], rv.parse_tag(tag).version)


class AuditGateTests(unittest.TestCase):
    CLEAN_V = "The given project `A` has no vulnerable packages given the current sources.\nThe given project `B` has no vulnerable packages given the current sources.\n"
    CLEAN_D = "The given project `A` has no deprecated packages given the current sources.\n"

    def test_clean_output_passes(self):
        rv.check_audit(self.CLEAN_V, "vulnerable")
        rv.check_audit(self.CLEAN_D, "deprecated")

    def test_findings_fail(self):
        with self.assertRaises(rv.ReleaseError):
            rv.check_audit(self.CLEAN_V + "Project `C` has the following vulnerable packages\n   > Foo 1.0.0 High\n", "vulnerable")
        with self.assertRaises(rv.ReleaseError):
            rv.check_audit(self.CLEAN_D + "Project `C` has the following deprecated packages\n   > Foo 1.0.0 Legacy\n", "deprecated")

    def test_an_unreachable_vulnerability_source_fails_closed(self):
        # dotnet list exits 0 and prints a warning when it cannot read the audit data: that must not pass.
        for text in (
            self.CLEAN_V + "warning NU1900: Error occurred while getting package vulnerability data: Unable to load the service index for source https://api.nuget.org/v3/index.json.\n",
            "warning NU1900: Error occurred while getting package vulnerability data\n",
            self.CLEAN_V + "error: Unable to load the service index for source https://api.nuget.org/v3/index.json.\n",
            self.CLEAN_V + "Failed to retrieve information about 'X' from remote source\n",
            "",
            "Build succeeded.\n",
        ):
            with self.subTest(text=text[:50]), self.assertRaises(rv.ReleaseError):
                rv.check_audit(text, "vulnerable")

    def test_the_cli_exit_code_follows_the_verdict(self):
        with tempfile.TemporaryDirectory() as tmp:
            good = pathlib.Path(tmp) / "good.txt"
            bad = pathlib.Path(tmp) / "bad.txt"
            good.write_text(self.CLEAN_V)
            bad.write_text("warning NU1900: Error occurred while getting package vulnerability data\n")
            run = lambda f: subprocess.run([sys.executable, str(SCRIPTS / "release_verify.py"), "audit", "--kind", "vulnerable", "--file", str(f)],
                                           capture_output=True, text=True).returncode
            self.assertEqual(0, run(good))
            self.assertEqual(1, run(bad))


class NotPublishedTests(unittest.TestCase):
    def test_a_version_that_exists_on_nuget_org_blocks_the_release(self):
        with self.assertRaises(rv.ReleaseError) as raised:
            rv.check_not_published(["Pkg.A", "Pkg.B"], "1.2.3", lambda url: 200 if "pkg.b" in url else 404)
        self.assertIn("Pkg.B", str(raised.exception))

    def test_an_unpublished_version_passes(self):
        urls = []
        rv.check_not_published(["Pkg.A"], "1.2.3", lambda url: urls.append(url) or 404)
        self.assertEqual(["https://api.nuget.org/v3-flatcontainer/pkg.a/1.2.3/pkg.a.nuspec"], urls)

    def test_an_unclear_answer_fails_closed(self):
        for answer in (500, 403, 302):
            with self.subTest(answer=answer), self.assertRaises(rv.ReleaseError):
                rv.check_not_published(["Pkg.A"], "1.2.3", lambda url, a=answer: a)

        def boom(url):
            raise OSError("network down")

        with self.assertRaises(rv.ReleaseError):
            rv.check_not_published(["Pkg.A"], "1.2.3", boom)

    def test_the_publish_job_runs_the_check_before_the_push(self):
        text = (REPO / ".github" / "workflows" / "release.yml").read_text()
        check = text.index("release_verify.py not-published")
        self.assertLess(check, text.index("dotnet nuget push"))


class PackageSetTests(unittest.TestCase):
    IDS = ["Pkg.A", "Pkg.B"]

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.out = pathlib.Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def pack(self, package_id, version="11.0.0", tfm="net11.0", readme=True, symbols=True):
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
        rv.check_packages(self.out, self.IDS, "11.0.0", "net11.0")

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
        (self.out / "Pkg.B.11.0.0.nupkg").unlink()
        with zipfile.ZipFile(self.out / "Pkg.B.11.0.0.nupkg", "w") as z:
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
                 "--version", "11.0.0", "--tfm", "net11.0"], capture_output=True, text=True)
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

    def test_the_audit_steps_use_the_fail_closed_gate(self):
        for name in ("release.yml", "ci.yml"):
            text = (REPO / ".github" / "workflows" / name).read_text()
            self.assertIn("release_verify.py audit --kind vulnerable", text, name)
            self.assertIn("release_verify.py audit --kind deprecated", text, name)

    def test_release_workflow_uses_the_script_for_tag_and_package_checks(self):
        text = (REPO / ".github" / "workflows" / "release.yml").read_text()
        self.assertIn("scripts/release_verify.py tag", text)
        self.assertIn("scripts/release_verify.py packages", text)
        self.assertNotIn('core="${version%%-*}"', text)


    # Release-workflow structure (no YAML library in the standard library: the jobs and steps are split textually).
    @staticmethod
    def _jobs(text):
        jobs, current = {}, None
        for line in text.splitlines():
            match = re.match(r"^  ([a-z][a-z-]*):\s*$", line)
            if match and "jobs:" in text.split(line)[0]:
                current = match.group(1)
                jobs[current] = []
            elif current is not None:
                jobs[current].append(line)
        return {name: "\n".join(lines) for name, lines in jobs.items()}

    @staticmethod
    def _steps(job):
        return re.split(r"(?m)^      - ", job)[1:]

    def test_no_job_downloads_packages_into_the_workspace_before_a_checkout(self):
        # actions/checkout empties the workspace on a fresh runner: a download made before it into the workspace is wiped, the
        # re-inspect then fails after the approval and the tag is burned. Download outside the workspace (runner.temp) or after it.
        text = (REPO / ".github" / "workflows" / "release.yml").read_text()
        for name, job in self._jobs(text).items():
            steps = self._steps(job)
            for index, step in enumerate(steps):
                if "actions/download-artifact@" not in step:
                    continue
                path = re.search(r"(?m)^\s+path:\s*(\S.*)$", step)
                self.assertIsNotNone(path, f"{name}: download step without a path")
                outside = path.group(1).startswith("${{ runner.temp }}")
                checkout_later = any("actions/checkout@" in later for later in steps[index + 1:])
                self.assertTrue(outside or not checkout_later, f"{name}: download into the workspace before a checkout")

    def test_publish_scripts_come_from_the_resolved_sha_and_packages_from_runner_temp(self):
        text = (REPO / ".github" / "workflows" / "release.yml").read_text()
        publish = self._jobs(text)["publish"]
        self.assertIn("ref: ${{ needs.guard.outputs.sha }}", publish)
        self.assertNotIn("refs/tags", publish)
        self.assertIn("path: ${{ runner.temp }}/out", publish)
        self.assertNotIn("out/*.nupkg\" --api-key", publish.replace("$RUNNER_TEMP/out/*.nupkg", ""))

    def test_publish_runs_only_for_a_real_run_and_uses_the_standard_names(self):
        text = (REPO / ".github" / "workflows" / "release.yml").read_text()
        publish = self._jobs(text)["publish"]
        self.assertIn("if: needs.guard.outputs.dry_run == 'false'", publish)
        self.assertIn("environment: nuget-stable", publish)
        self.assertIn("vars.STABLE_PUBLISH_MODE", text)
        self.assertIn("vars.NUGET_USER", publish)
        for stale in ("RELEASE_APPROVAL_MODE", "nuget-release", "secrets.NUGET_USER", "NUGET_API_KEY: ${{ secrets"):
            self.assertNotIn(stale, text)

    def test_the_github_release_step_refuses_a_moved_tag(self):
        text = (REPO / ".github" / "workflows" / "release.yml").read_text()
        release = self._jobs(text)["github-release"]
        self.assertIn("git/ref/tags/$TAG", release)
        self.assertIn("git/tags/", release)       # an annotated tag is dereferenced to its commit
        self.assertIn("needs.guard.outputs.sha", release)
        self.assertIn("no longer points at the published commit", release)


if __name__ == "__main__":
    unittest.main()
