#!/usr/bin/env python3
"""Build TabTabGo.Storage against a LOCAL TabTabGo.Core before Core 8.0.1 is on nuget.org.

    python3 scripts/local-core.py            # pack Core v8 (-local.<sha>) and write nuget.local.config
    eval "$(python3 scripts/local-core.py --env)"   # sets TabTabGoCoreVersion for this shell
    dotnet restore TabTabGo.Storage.sln --configfile nuget.local.config
    dotnet build TabTabGo.Storage.sln -c Release -warnaserror --no-restore

What it does: runs tabtabgo/scripts/pack-core-local.py --line v8 (Core solution packed sequentially from a git archive
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


def latest_local_version() -> str:
    versions = []
    for f in FEED.glob("TabTabGo.Core.[0-9]*-local.*.nupkg"):
        m = re.match(r"TabTabGo\.Core\.(\d+\.\d+\.\d+-local\.[0-9a-f]+)\.nupkg", f.name)
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
    args = ap.parse_args()
    if args.env:
        print(f"export TabTabGoCoreVersion={latest_local_version()}")
        return 0
    subprocess.run([sys.executable, str(PACK), "--line", "v8"], check=True)
    version = latest_local_version()
    write_config(version)
    print(f"\nWrote {REPO / 'nuget.local.config'}\nNext:\n  export TabTabGoCoreVersion={version}\n"
          "  dotnet restore TabTabGo.Storage.sln --configfile nuget.local.config\n"
          "  dotnet build TabTabGo.Storage.sln -c Release -warnaserror --no-restore\n"
          "  dotnet test TabTabGo.Storage.sln -c Release --no-build")
    return 0


if __name__ == "__main__":
    sys.exit(main())
