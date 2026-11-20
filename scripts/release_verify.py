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

# ASCII digits only and no leading zeros: \d also matches other scripts' digits, and v010.0.0 / rc.01 are not canonical.
NUM = r"(0|[1-9][0-9]*)"
TAG_RE = re.compile(rf"^v{NUM}\.{NUM}\.{NUM}(-(?:rc|preview|alpha|beta)\.{NUM})?$", re.ASCII)


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
    major, minor, patch, suffix, _ = match.groups()
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


# What `dotnet list package --vulnerable|--deprecated` prints when it could not read its data source. The command still exits 0, so
# the output is the verdict and anything unclear is a failure.
AUDIT_FAILURE_MARKERS = (
    "NU1900", "Unable to load the service index", "Failed to retrieve information", "Error occurred while getting",
    "error:", "Response status code does not indicate success", "No such host is known", "Name or service not known",
)


def check_audit(text: str, kind: str) -> None:
    """Fail closed: findings, audit-source errors and output that does not say 'no <kind> packages' for a project all fail."""
    if f"has the following {kind} packages" in text or re.search(r"^\s+>\s", text, re.M):
        raise ReleaseError(f"{kind} packages found")
    for marker in AUDIT_FAILURE_MARKERS:
        if marker in text:
            raise ReleaseError(f"the {kind} audit could not complete ({marker!r} in the output); it is treated as a failure")
    if f"has no {kind} packages" not in text:
        raise ReleaseError(f"the {kind} audit output does not report any project as clean; it is treated as a failure")


def _status(url: str) -> int:
    import urllib.error
    import urllib.request

    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            return None

    opener = urllib.request.build_opener(NoRedirect)
    try:
        with opener.open(urllib.request.Request(url, method="HEAD"), timeout=30) as response:
            return response.status
    except urllib.error.HTTPError as error:
        return error.code


def check_not_published(package_ids, version: str, status=_status) -> None:
    """A re-run of a release must not look green because `dotnet nuget push --skip-duplicate` skipped what is already published."""
    for package_id in package_ids:
        lower = package_id.lower()
        url = f"https://api.nuget.org/v3-flatcontainer/{lower}/{version.lower()}/{lower}.nuspec"
        try:
            code = status(url)
        except Exception as error:  # network failure: unknown, so not safe to publish
            raise ReleaseError(f"could not check whether {package_id} {version} exists on nuget.org: {error}") from error
        if code == 200:
            raise ReleaseError(f"{package_id} {version} is already published on nuget.org; a re-run would silently skip it")
        if code != 404:
            raise ReleaseError(f"unexpected answer {code} for {package_id} {version} on nuget.org")


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
    a = sub.add_parser("audit")
    a.add_argument("--kind", choices=["vulnerable", "deprecated"], required=True)
    a.add_argument("--file", required=True)
    n = sub.add_parser("not-published")
    n.add_argument("--expected", help="space-separated package ids")
    n.add_argument("--dir", help="folder with the packed <id>.<version>.nupkg files (ids are taken from the file names)")
    n.add_argument("--version", required=True)
    args = ap.parse_args(argv)
    try:
        if args.command == "audit":
            check_audit(pathlib.Path(args.file).read_text(errors="replace"), args.kind)
        elif args.command == "not-published":
            ids = args.expected.split() if args.expected else [
                f.name[: -len(f".{args.version}.nupkg")] for f in sorted(pathlib.Path(args.dir).glob(f"*.{args.version}.nupkg"))]
            if not ids:
                raise ReleaseError("no packages to check: pass --expected or a --dir that holds the packed packages")
            check_not_published(ids, args.version)
        elif args.command == "tag":
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
