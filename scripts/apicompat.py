#!/usr/bin/env python3
"""Assembly-mode API compatibility check of the v8 assemblies against the published 6.0.8.4 libraries.

Package validation (EnablePackageValidation) compares nothing here: every 6.0.8.4 asset is netstandard2.0 or net6.0, so
it only reports PKV006 (target framework dropped). This script downloads the 6.0.8.4 packages from nuget.org, takes
their lib assembly and runs Microsoft.DotNet.ApiCompat.Tool (pinned in .config/dotnet-tools.json) against the freshly
built net8.0 assembly of this repository.

    python3 scripts/apicompat.py            # check: fails on any break that is not in compat/<assembly>.suppressions.xml
    python3 scripts/apicompat.py --update   # regenerate compat/*.suppressions.xml; every entry must match a RULES row
                                            # (spec 6.4 breaking-change id) or the script fails and lists what is unmapped

Build first: dotnet build TabTabGo.Storage.sln -c Release. The downloaded packages are cached under
artifacts/apicompat-baseline/ (git-ignored). Known gaps: ApiCompat does not report a public field turned into a property
as a distinct break the way source compilers see it (spec 6.4 SB14, FileResult), and behaviour changes (SB6-SB9, SB16, SB17,
SB21) are not API changes at all; both are covered by the README upgrade table and by tests.
"""
import argparse
import pathlib
import re
import subprocess
import sys
import urllib.request
import zipfile

REPO = pathlib.Path(__file__).resolve().parent.parent
CACHE = REPO / "artifacts" / "apicompat-baseline"
COMPAT = REPO / "compat"
BASELINE = "6.0.8.4"

# package id, folder under src, baseline lib TFM
PACKAGES = [
    ("TabTabGo.Storage", "TabTabGo.Storage", "netstandard2.0"),
    ("TabTabGo.Storage.Services", "TabTabGo.Storage.Services", "net6.0"),
    ("TabTabGo.Storage.Data.EF", "TabTabGo.Storage.Data.EF", "net6.0"),
    ("TabTabGo.Storage.Aws", "TabTabGo.Storage.Aws", "net6.0"),
    ("TabTabGo.Storage.Azure", "TabTabGo.Storage.AzureStorage", "netstandard2.0"),
    ("TabTabGo.Storage.FileStorage", "TabTabGo.Storage.FileStorage", "net6.0"),
]

# (substring of the suppression Target, spec 6.4 row, one-line reason). First match wins.
RULES = [
    ("FormFile", "SB4", "IFile.FormFile / File.FormFile removed; upload content is a FileUpload (ADR 0004)"),
    ("CompositionRoot", "SB10", "LightInject composition roots removed; use AddTabTabGo...()"),
    ("ImageSizeExtensions", "SB15", "ImageSizeExtensions moved from the global namespace to TabTabGo.Storage.Services"),
    ("PathService", "SB13", "PathService takes IHostEnvironment instead of IHostingEnvironment"),
    ("IPathService", "SB-A1", "IPathService.PathSettings { get; } is a new abstract member (custom implementers break)"),
    ("S3StorageProvider", "SB11", "S3StorageProvider constructors; parameterless ctor and SetConfiguration are obsolete"),
    ("BlobStorageProvider", "SB12", "BlobStorageProvider constructor takes BlobServiceClient + options; legacy ctor obsolete"),
    ("IFileService", "SB22", "IFileService<TFile> gained Upload / Replace / ListAsync (and Delete(query) stays); implementers must add them"),
    ("FileService`1.#ctor", "SB2", "FileService constructor: IUnitOfWork moved from TabTabGo.Core.Infrastructure.Data to TabTabGo.Core.Data (Core 8 API, spec 3.4)"),
    ("FileService", "SB22", "FileService<TFile> constructor / member changes (Update(JsonObject) replaces Update(JObject), PostCreate, GetImage)"),
    ("IStorageProvider", "SB7", "IStorageProvider members gained nullable annotations and default-interface additions; contract change"),
    ("StorageProvider", "SB7", "StorageProvider base members; contract change"),
    ("StorageDbContext", "SB18", "StorageDbContext maps File; OnConfiguring override removed"),
    ("FileResult", "SB14", "FileResult fields became properties and the type is IAsyncDisposable"),
    ("IFile", "SB4", "IFile member set changed (FormFile removed)"),
    ("Entities.File", "SB4", "File.FormFile removed"),
    ("S3Configuration", "SB11", "S3Configuration members (optional credentials, endpoint, timeout)"),
    ("PathSettings", "SB-A1", "PathSettings: FileTypePaths indexer / TryGetPath / PublicBaseUrl"),
    ("ImageService", "SB22", "ImageService constructor (options) and format handling"),
]


def tool(*args):
    # CP1002 (reference not found) is tooling noise: the Core 6 / EF 6 dependency assemblies are not on the left side.
    return subprocess.run(["dotnet", "tool", "run", "apicompat", "--", "--noWarn", "CP1002", *args], cwd=REPO, text=True,
                          capture_output=True)


def fetch(package: str) -> pathlib.Path:
    dest = CACHE / package
    marker = dest / ".done"
    if not marker.exists():
        dest.mkdir(parents=True, exist_ok=True)
        url = f"https://api.nuget.org/v3-flatcontainer/{package.lower()}/{BASELINE}/{package.lower()}.{BASELINE}.nupkg"
        nupkg = CACHE / f"{package}.{BASELINE}.nupkg"
        urllib.request.urlretrieve(url, nupkg)
        with zipfile.ZipFile(nupkg) as z:
            z.extractall(dest)
        for f in dest.rglob("*"):
            f.chmod(0o755 if f.is_dir() else 0o644)
        marker.write_text("ok")
    return dest


def paths(package: str, folder: str, tfm: str):
    asm = "TabTabGo.Storage.Azure" if package == "TabTabGo.Storage.Azure" else package
    left_dir = fetch(package) / "lib" / tfm
    right_dir = REPO / "src" / folder / "bin" / "Release" / "net8.0"
    if not (right_dir / f"{asm}.dll").exists():
        sys.exit(f"Build first: {right_dir / (asm + '.dll')} not found (dotnet build TabTabGo.Storage.sln -c Release)")
    return asm, left_dir, right_dir


def annotate(raw: str, package: str):
    """Add a '<!-- SBn: reason -->' comment before every suppression; returns (xml, unmapped targets)."""
    unmapped = []

    def rule_for(target: str):
        for needle, row, reason in RULES:
            if needle in target:
                return row, reason
        return None

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
    args = ap.parse_args()
    COMPAT.mkdir(exist_ok=True)
    failed = False
    for package, folder, tfm in PACKAGES:
        asm, left, right = paths(package, folder, tfm)
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
