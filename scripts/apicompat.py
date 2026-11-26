#!/usr/bin/env python3
"""Assembly-mode API compatibility check of the v11 assemblies against the 10.0.0 libraries (the v10 line).

v11 may only ADD API relative to v10 (additive-only rule); every break must be a deliberate row in EXACT below. Package
validation compares the package, not the assembly, so this script takes the baseline package's lib assembly and runs
Microsoft.DotNet.ApiCompat.Tool (pinned in .config/dotnet-tools.json) against the freshly built net11.0 assembly.

    python3 scripts/apicompat.py                            # CI: download 10.0.0 from nuget.org (SHA-256 pinned below)
    python3 scripts/apicompat.py --baseline-dir DIR         # local: use DIR/<package>.10.0.0.nupkg built from the v10 worktree
    python3 scripts/apicompat.py --update [--baseline-dir]  # regenerate compat/*.suppressions.xml; every entry must match an
                                                            # EXACT row or the script fails and lists what is unmapped

Build first: dotnet build TabTabGo.Storage.sln -c Release. Downloaded packages are cached under artifacts/apicompat-baseline/
(git-ignored); a --baseline-dir package is extracted fresh on every run and never cached. The 10.0.0 SHA-256 values are recorded
after 10.0.0 is published (see BASELINE_SHA256): without them the download mode refuses to run, so a missing pin can never pass
silently. Known gap: ApiCompat does not report a public field turned into a property as a distinct break the way source
compilers see it; behaviour changes are not API changes at all; both are covered by the README upgrade table and by tests.
"""
import argparse
import hashlib
import pathlib
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile

REPO = pathlib.Path(__file__).resolve().parent.parent
CACHE = REPO / "artifacts" / "apicompat-baseline"
COMPAT = REPO / "compat"
BASELINE = "10.0.0"

# package id, folder under src, baseline lib TFM
PACKAGES = [
    ("TabTabGo.Storage", "TabTabGo.Storage", "net10.0"),
    ("TabTabGo.Storage.Services", "TabTabGo.Storage.Services", "net10.0"),
    ("TabTabGo.Storage.Data.EF", "TabTabGo.Storage.Data.EF", "net10.0"),
    ("TabTabGo.Storage.Aws", "TabTabGo.Storage.Aws", "net10.0"),
    ("TabTabGo.Storage.Azure", "TabTabGo.Storage.AzureStorage", "net10.0"),
    ("TabTabGo.Storage.FileStorage", "TabTabGo.Storage.FileStorage", "net10.0"),
]
RIGHT_TFM = "net11.0"

# Exact suppression targets -> (spec row, reason). A break that is not listed here is NOT auto-labelled: --update fails and
# lists it, so a new break needs a deliberate row (substring rules would have labelled an unrelated change by accident).
# v10 -> v11 has none yet: the v11 line is additive. Add a row here (with the spec id) before accepting any break.
EXACT = {}

# SHA-256 of the published 10.0.0 packages, so a tampered download or cache cannot change the baseline. Filled in when 10.0.0 is
# published (run the release workflow's pack output through `sha256sum`); empty means "download mode refuses to run".
BASELINE_SHA256 = {}


def tool(*args):
    # CP1002 (reference not found) is tooling noise: the Core 6 / EF 6 dependency assemblies are not on the left side.
    return subprocess.run(["dotnet", "tool", "run", "apicompat", "--", "--noWarn", "CP1002", *args], cwd=REPO, text=True,
                          capture_output=True)


def sha256(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


DOWNLOAD_TIMEOUT_SECONDS = 60
DOWNLOAD_ATTEMPTS = 3


def download(url: str, dest: pathlib.Path, opener=urllib.request.urlopen, sleep=time.sleep) -> None:
    """Download url to dest. Each attempt has a 60 s socket timeout (connect and every read); 3 attempts with 2 s / 4 s backoff.

    A stalled transfer used to hang for hours because urlretrieve has no timeout. The file is written to a temporary name and
    renamed only when complete, so a failed attempt never leaves a partial package behind.
    """
    partial = dest.with_name(dest.name + ".part")
    last = None
    for attempt in range(1, DOWNLOAD_ATTEMPTS + 1):
        try:
            with opener(url, timeout=DOWNLOAD_TIMEOUT_SECONDS) as response:
                data = response.read()
            partial.write_bytes(data)
            partial.replace(dest)
            return
        except (OSError, urllib.error.URLError) as error:  # socket.timeout and URLError are OSError subclasses
            last = error
            partial.unlink(missing_ok=True)
            if attempt < DOWNLOAD_ATTEMPTS:
                sleep(2 ** attempt)
    sys.exit(f"Could not download {url} after {DOWNLOAD_ATTEMPTS} attempts "
             f"({DOWNLOAD_TIMEOUT_SECONDS} s timeout each): {last}. Check the network or proxy and rerun.")


def fetch_local(package: str, baseline_dir: pathlib.Path) -> pathlib.Path:
    """Extract DIR/<package>.<BASELINE>.nupkg into a fresh folder (a locally built baseline is never cached or pinned)."""
    nupkg = baseline_dir / f"{package}.{BASELINE}.nupkg"
    if not nupkg.exists():
        sys.exit(f"{nupkg} not found: pack the v10 worktree first (dotnet pack TabTabGo.Storage.sln -c Release -o {baseline_dir})")
    dest = CACHE / "local" / package
    if dest.exists():
        shutil.rmtree(dest)
    dest.mkdir(parents=True)
    with zipfile.ZipFile(nupkg) as z:
        z.extractall(dest)
    return dest


def fetch(package: str, baseline_dir: pathlib.Path | None = None) -> pathlib.Path:
    if baseline_dir is not None:
        return fetch_local(package, baseline_dir)
    if package not in BASELINE_SHA256:
        sys.exit(f"No pinned SHA-256 for {package} {BASELINE}: record the published package hashes in BASELINE_SHA256 after 10.0.0 "
                 "is published, or pass --baseline-dir with packages built from the v10 worktree")
    dest = CACHE / package
    marker = dest / ".done"
    nupkg = CACHE / f"{package}.{BASELINE}.nupkg"
    expected = BASELINE_SHA256[package]
    if marker.exists() and (not nupkg.exists() or sha256(nupkg) != expected):
        sys.exit(f"{nupkg} does not match the pinned SHA-256; delete artifacts/apicompat-baseline and rerun")
    if not marker.exists():
        dest.mkdir(parents=True, exist_ok=True)
        url = f"https://api.nuget.org/v3-flatcontainer/{package.lower()}/{BASELINE}/{package.lower()}.{BASELINE}.nupkg"
        download(url, nupkg)
        if sha256(nupkg) != expected:
            sys.exit(f"{package} {BASELINE}: downloaded package does not match the pinned SHA-256 {expected}")
        with zipfile.ZipFile(nupkg) as z:
            z.extractall(dest)
        for f in dest.rglob("*"):
            f.chmod(0o755 if f.is_dir() else 0o644)
        marker.write_text("ok")
    return dest


def paths(package: str, folder: str, tfm: str, baseline_dir: pathlib.Path | None = None):
    asm = "TabTabGo.Storage.Azure" if package == "TabTabGo.Storage.Azure" else package
    left_dir = fetch(package, baseline_dir) / "lib" / tfm
    right_dir = REPO / "src" / folder / "bin" / "Release" / RIGHT_TFM
    if not (right_dir / f"{asm}.dll").exists():
        sys.exit(f"Build first: {right_dir / (asm + '.dll')} not found (dotnet build TabTabGo.Storage.sln -c Release)")
    return asm, left_dir, right_dir


def annotate(raw: str, package: str):
    """Add a '<!-- SBn: reason -->' comment before every suppression; returns (xml, unmapped targets)."""
    unmapped = []

    def rule_for(target: str):
        return EXACT.get(target)

    def repl(match):
        block = match.group(0)
        target = re.search(r"<Target>([^<]*)</Target>", block).group(1)
        found = rule_for(target)
        if found is None:
            unmapped.append(target)
            return block
        return f"<!-- {found[0]}: {found[1]} -->\n  {block}"

    # Left/Right hold machine-specific absolute paths; matching on DiagnosticId + Target is enough and portable.
    raw = re.sub(r"\s*<(Left|Right)>[^<]*</\1>", "", raw)
    return re.sub(r"<Suppression>.*?</Suppression>", repl, raw, flags=re.S), unmapped


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--update", action="store_true", help="regenerate compat/*.suppressions.xml")
    ap.add_argument("--baseline-dir", type=pathlib.Path, help="folder with locally packed v10 packages (<id>.10.0.0.nupkg)")
    args = ap.parse_args()
    COMPAT.mkdir(exist_ok=True)
    failed = False
    for package, folder, tfm in PACKAGES:
        asm, left, right = paths(package, folder, tfm, args.baseline_dir)
        suppressions = COMPAT / f"{asm}.suppressions.xml"
        common = ["--left-assembly", str(left / f"{asm}.dll"), "--right-assembly", str(right / f"{asm}.dll"),
                  "--left-assembly-references", str(left), "--right-assembly-references", str(right)]
        if args.update:
            raw_path = CACHE / f"{asm}.raw.xml"
            result = tool(*common, "--generate-suppression-file", "--suppression-file", str(raw_path))
            if result.returncode != 0:
                sys.stderr.write(result.stdout + result.stderr)
                failed = True
                continue
            if raw_path.exists():
                xml, unmapped = annotate(raw_path.read_text(), package)
                raw_path.unlink()
                if unmapped:
                    failed = True
                    print(f"{asm}: UNMAPPED breaks (add a RULES row citing the spec 6.4 id, or fix the API):")
                    for target in unmapped:
                        print("   ", target)
                suppressions.write_text(xml)
                print(f"{asm}: {xml.count('<Suppression>')} suppression(s) written")
            else:
                if suppressions.exists():
                    suppressions.unlink()
                print(f"{asm}: no breaks")
            continue

        extra = ["--suppression-file", str(suppressions)] if suppressions.exists() else []
        result = tool(*common, *extra)
        # The tool exits 0 even when it reports breaks, so the output is the verdict.
        output = result.stdout + result.stderr
        broken = result.returncode != 0 or "breaking changes found" in output or re.search(r"^CP\d{4}:", output, re.M)
        sys.stdout.write(output[-2500:] if broken else f"{asm}: ok\n")
        if broken:
            failed = True
        elif suppressions.exists():
            text = suppressions.read_text()
            blocks = text.count("<Suppression>")
            cited = len(re.findall(r"<!--\s*SB[\w-]*:", text))
            if blocks != cited:
                print(f"{asm}: {blocks - cited} suppression(s) do not cite a spec 6.4 row")
                failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
