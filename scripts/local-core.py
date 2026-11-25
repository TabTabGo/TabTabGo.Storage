#!/usr/bin/env python3
"""Build TabTabGo.Storage against a LOCAL TabTabGo.Core of the same line (default: this repository's line).

    python3 scripts/local-core.py            # pack this line's Core (-local.<sha>) and write nuget.local.config
    eval "$(python3 scripts/local-core.py --env)"   # sets TabTabGoCoreVersion for this shell
    dotnet restore TabTabGo.Storage.sln --configfile nuget.local.config
    dotnet build TabTabGo.Storage.sln -c Release -warnaserror --no-restore

What it does: runs tabtabgo/scripts/pack-core-local.py --line <line> (Core solution packed sequentially from a git archive
of the clean Core worktree, version <core>-local.<sha>, into tabtabgo/.local-nuget), then writes the git-ignored
nuget.local.config (nuget.org + that folder, with source mapping so only TabTabGo.Core packages come from it).
Nothing outside this repository, tabtabgo/.local-nuget and the Core archive scratch folder is touched, and nothing
here is ever committed: release.yml refuses a -local Core.
"""
import argparse
import pathlib
import re
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
TABTABGO = REPO.parent
FEED = TABTABGO / ".local-nuget"
PACK = TABTABGO / "scripts" / "pack-core-local.py"


# Names pack-core-local.py --line expects, and the other names a Core worktree of a line has been created under.
WORKTREE_NAMES = {"v8": "TabTabGo.Core-v8", "v10": "TabTabGo.Core-net10", "v11": "TabTabGo.Core-v11"}
WORKTREE_ALIASES = {
    "v8": ["TabTabGo.Core-v8"],
    "v10": ["TabTabGo.Core-net10", "TabTabGo.Core-v10", "TabTabGo.Core-rel10"],
    "v11": ["TabTabGo.Core-v11", "TabTabGo.Core-net11", "TabTabGo.Core-rel11"],
}


def core_worktree(line: str) -> pathlib.Path:
    """The Core worktree of a line, found with `git worktree list` of the Core repository (not a hard-coded folder name)."""
    listing = subprocess.run(["git", "-C", str(TABTABGO / "TabTabGo.Core"), "worktree", "list", "--porcelain"],
                             capture_output=True, text=True)
    paths = [pathlib.Path(line_[len("worktree "):]) for line_ in listing.stdout.splitlines() if line_.startswith("worktree ")]
    paths += sorted(TABTABGO.glob("TabTabGo.Core-*"))
    for alias in WORKTREE_ALIASES[line]:
        for path in paths:
            if path.name == alias and path.exists():
                return path
    sys.exit(f"No Core worktree for {line} (looked for {', '.join(WORKTREE_ALIASES[line])} in `git worktree list` and {TABTABGO}); "
             "pass --core <path>.")


def default_line() -> str:
    """The line of this repository, read from the target framework (net8.0 -> v8, net10.0 -> v10, net11.0 -> v11)."""
    props = (REPO / "Directory.Build.props").read_text()
    major = re.search(r"<TargetFramework>net(\d+)\.0</TargetFramework>", props).group(1)
    return f"v{major}"


def latest_local_version(line: str) -> str:
    major = line.removeprefix("v")
    versions = []
    for f in FEED.glob(f"TabTabGo.Core.{major}.*-local.*.nupkg"):
        m = re.match(r"TabTabGo\.Core\.(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+?)?-local\.[0-9a-f]+)\.nupkg", f.name)
        if m:
            versions.append((f.stat().st_mtime, m.group(1)))
    if not versions:
        sys.exit(f"No TabTabGo.Core -local package in {FEED}; run without --env first.")
    return max(versions)[1]


def write_config(version: str) -> None:
    config = f"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="ttg-core-local" value="{FEED}" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="ttg-core-local">
      <package pattern="TabTabGo.Core" />
      <package pattern="TabTabGo.Core.*" />
      <package pattern="TabTabGo.Data.EF" />
      <package pattern="TabTabGo.IO.Utilities" />
      <package pattern="TTG.Data" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"""
    (REPO / "nuget.local.config").write_text(config)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--env", action="store_true", help="print the export line for the newest packed version and exit")
    ap.add_argument("--core", type=pathlib.Path, help="Core worktree to pack (default: found with `git worktree list`)")
    ap.add_argument("--line", choices=["v8", "v10", "v11"], default=default_line(), help="Core line to pack (default: this repository's)")
    args = ap.parse_args()
    line = args.line
    if args.env:
        print(f"export TabTabGoCoreVersion={latest_local_version(line)}")
        return 0
    worktree = args.core or core_worktree(line)
    if worktree.name == WORKTREE_NAMES.get(line):
        subprocess.run([sys.executable, str(PACK), "--line", line], check=True)
    else:
        # The line's Core worktree has a different name than pack-core-local.py's --line mode expects: use its explicit mode (a git
        # archive of the clean worktree, version <core>-local.<sha>, into the same feed).
        subprocess.run([sys.executable, str(PACK), "--core", str(worktree), "--feed", str(FEED), "--explicit"], check=True)
    version = latest_local_version(line)
    write_config(version)
    print(f"\nWrote {REPO / 'nuget.local.config'}\nNext:\n  export TabTabGoCoreVersion={version}\n"
          "  dotnet restore TabTabGo.Storage.sln --configfile nuget.local.config\n"
          "  dotnet build TabTabGo.Storage.sln -c Release -warnaserror --no-restore\n"
          "  dotnet test TabTabGo.Storage.sln -c Release --no-build")
    return 0


if __name__ == "__main__":
    sys.exit(main())
