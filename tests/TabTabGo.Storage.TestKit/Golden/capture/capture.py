#!/usr/bin/env python3
"""Re-capture the golden EF model of TabTabGo.Storage.Entities.File from the 6.0.8.4 sources.

    python3 tests/TabTabGo.Storage.TestKit/Golden/capture/capture.py [--sha a6c506b] [--write]

Exports `git archive <sha> src` of this repository into a scratch folder, builds capture/ (net6.0, EF Core 6.0.9,
SQLite and SQL Server providers; no connection to SQL Server is opened) and runs it three times. Without --write the
output is compared with the checked-in golden files and the script exits 1 on any difference; with --write the golden
files are replaced. The commit and the exact commands are printed so they can go into the review notes.
Requires the net6 runtime/SDK that is installed with the .NET 6 SDK (see global.json for the repo's own SDK).
"""
import argparse
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
GOLDEN = HERE.parent
REPO = HERE.parents[3]
FILES = {
    "sqlite": GOLDEN / "file-model-6.0.8.4.json",
    "sqlserver": GOLDEN / "file-model-6.0.8.4.sqlserver.json",
    "extra": GOLDEN / "extra-properties-6.0.8.4.json",
}


def run(cmd, cwd=None):
    print("$", " ".join(str(c) for c in cmd), flush=True)
    return subprocess.run(cmd, cwd=cwd, check=True, text=True, capture_output=True)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--sha", default="a6c506b")
    ap.add_argument("--write", action="store_true")
    args = ap.parse_args()
    full = run(["git", "rev-parse", args.sha], cwd=REPO).stdout.strip()
    with tempfile.TemporaryDirectory() as tmp:
        scratch = pathlib.Path(tmp)
        (scratch / "master").mkdir()
        archive = subprocess.Popen(["git", "-C", str(REPO), "archive", full, "src"], stdout=subprocess.PIPE)
        subprocess.run(["tar", "-x", "-C", str(scratch / "master")], stdin=archive.stdout, check=True)
        if archive.wait() != 0:
            sys.exit("git archive failed")
        cap = scratch / "cap"
        shutil.copytree(HERE, cap, ignore=shutil.ignore_patterns("capture.py", "bin", "obj"))
        # Stop MSBuild from inheriting the repository's props / central package management.
        (cap / "Directory.Build.props").write_text("<Project />")
        (cap / "Directory.Packages.props").write_text("<Project />")
        run(["dotnet", "build", "-c", "Release", "-v", "q", "--nologo"], cwd=cap)
        results = {}
        for mode in FILES:
            results[mode] = run(["dotnet", "run", "-c", "Release", "--no-build", "--", mode], cwd=cap).stdout
    print(f"\nsource commit: {full} (6.0.8.4 = master a6c506b); EF Core 6.0.9; net6.0")
    differ = False
    for mode, path in FILES.items():
        text = results[mode]
        if mode != "extra":
            text = json.dumps(json.loads(text), indent=2) + "\n"
        if args.write:
            path.write_text(text)
            print("wrote", path.relative_to(REPO))
        elif not path.exists() or path.read_text().strip() != text.strip():
            differ = True
            print("DIFFERS:", path.relative_to(REPO))
        else:
            print("same:   ", path.relative_to(REPO))
    return 1 if differ else 0


if __name__ == "__main__":
    sys.exit(main())
