#!/usr/bin/env python3
"""Release verification used by .github/workflows/release.yml (and tested by scripts/test_release_verify.py).

    release_verify.py tag <tag> [--props Directory.Build.props] [--branches TEXT]
    release_verify.py packages --dir out --expected "Id1 Id2" --version V --tfm net10.0

Why a script and not shell: `unzip -l f | grep -q x` under `set -o pipefail` fails at random (SIGPIPE when grep -q exits early),
and the old tag check stripped the pre-release suffix before comparing with <Version>, so a -rc tag could never pass. Here the
tag is compared with the FULL declared version, the releases/vM.m.x branch is derived from the numeric core only, and package
contents are read with zipfile (no pipes).

Exit code 0 = ok; 1 = a check failed (the message is printed as a GitHub ::error::); with `tag`, GitHub step outputs are printed as
key=value lines on stdout after the checks pass.
"""
import argparse
import dataclasses
import pathlib
import re
import sys
import zipfile

TAG_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)(-(?:rc|preview|alpha|beta)\.\d+)?$")


class ReleaseError(Exception):
    """A release check failed."""


@dataclasses.dataclass(frozen=True)
class Tag:
    version: str      # the full version: 11.0.0-rc.1
    core: str         # the numeric part: 11.0.0
    major: int
    minor: int
    prerelease: bool


def parse_tag(tag: str) -> Tag:
    # fullmatch on a single line: `$` would accept a trailing newline
    match = TAG_RE.fullmatch(tag) if "\n" not in tag else None
    if match is None:
        raise ReleaseError("tag does not match vMAJOR.MINOR.PATCH[-rc|preview|alpha|beta.N]")
    major, minor, patch, suffix = match.groups()
    return Tag(version=tag[1:], core=f"{major}.{minor}.{patch}", major=int(major), minor=int(minor), prerelease=suffix is not None)


def _property(props_text: str, name: str) -> str:
    found = re.search(rf"<{name}>([^<]+)<", props_text)
    if found is None:
        raise ReleaseError(f"no <{name}> in Directory.Build.props")
    return found.group(1).strip()


def check_tag(tag: str, props_text: str, branches_text: str) -> Tag:
    parsed = parse_tag(tag)
    tfm = _property(props_text, "TargetFramework")
    if tfm != f"net{parsed.major}.0":
        raise ReleaseError(f"tag major {parsed.major} does not match TargetFramework {tfm}")
    wanted = f"origin/releases/v{parsed.major}.{parsed.minor}.x/"
    if not any(line.strip().lstrip("* ").startswith(wanted) for line in branches_text.splitlines()):
        raise ReleaseError(f"tagged commit is not on a releases/v{parsed.major}.{parsed.minor}.x/* branch")
    declared = _property(props_text, "Version")
    if declared != parsed.version:
        raise ReleaseError(f"tag version {parsed.version} differs from Directory.Build.props Version {declared}")
    return parsed


def check_packages(out_dir: pathlib.Path, expected_ids, version: str, tfm: str) -> None:
    out_dir = pathlib.Path(out_dir)
    expected_ids = list(expected_ids)
    nupkgs = [p for p in out_dir.glob("*.nupkg") if not p.name.endswith(".snupkg")]
    snupkgs = list(out_dir.glob("*.snupkg"))
    if len(nupkgs) != len(expected_ids):
        raise ReleaseError(f"unexpected number of .nupkg: {len(nupkgs)}, expected {len(expected_ids)}")
    if len(snupkgs) != len(expected_ids):
        raise ReleaseError(f"unexpected number of .snupkg: {len(snupkgs)}, expected {len(expected_ids)}")
    for package_id in expected_ids:
        path = out_dir / f"{package_id}.{version}.nupkg"
        if not path.is_file():
            raise ReleaseError(f"missing {path}")
        with zipfile.ZipFile(path) as z:
            names = z.namelist()
            nuspec = z.read(f"{package_id}.nuspec").decode("utf-8") if f"{package_id}.nuspec" in names else ""
        if f"<version>{version}</version>" not in nuspec:
            raise ReleaseError(f"{path} has the wrong version")
        if not any(n.startswith(f"lib/{tfm}/") for n in names):
            raise ReleaseError(f"{path} has no {tfm} lib")
        if "README.md" not in names:
            raise ReleaseError(f"{path} has no README")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)
    t = sub.add_parser("tag")
    t.add_argument("tag")
    t.add_argument("--props", default="Directory.Build.props")
    t.add_argument("--branches", required=True, help="output of `git branch -r --contains <sha>`")
    p = sub.add_parser("packages")
    p.add_argument("--dir", required=True)
    p.add_argument("--expected", required=True, help="space-separated package ids")
    p.add_argument("--version", required=True)
    p.add_argument("--tfm", help="default: <TargetFramework> of --props")
    p.add_argument("--props", default="Directory.Build.props")
    args = ap.parse_args(argv)
    try:
        if args.command == "tag":
            result = check_tag(args.tag, pathlib.Path(args.props).read_text(), args.branches)
            print(f"version={result.version}")
            print(f"prerelease={'true' if result.prerelease else 'false'}")
        else:
            tfm = args.tfm or _property(pathlib.Path(args.props).read_text(), "TargetFramework")
            check_packages(pathlib.Path(args.dir), args.expected.split(), args.version, tfm)
    except ReleaseError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
