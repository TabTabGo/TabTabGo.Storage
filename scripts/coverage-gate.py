#!/usr/bin/env python3
"""Fail when merged Cobertura coverage of src/ assemblies is below the thresholds (spec FR-9).

Usage: coverage-gate.py <results-dir> [--line 85] [--branch 75]
Reads every coverage.cobertura.xml under the directory; test and TestKit assemblies are ignored. Lines and branches
are merged per (file, line) across reports so the same source covered by several test projects counts once.
"""
import argparse
import pathlib
import sys
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument("results")
parser.add_argument("--line", type=float, default=85.0)
parser.add_argument("--branch", type=float, default=75.0)
args = parser.parse_args()

lines = {}      # (file, number) -> hits
branches = {}   # (file, number) -> (covered, total) maximum seen
for report in pathlib.Path(args.results).rglob("coverage.cobertura.xml"):
    for cls in ET.parse(report).getroot().iter("class"):
        filename = cls.get("filename", "").replace("\\", "/")
        if "/src/" not in "/" + filename and not filename.startswith("src/"):
            continue
        if "Tests" in filename or "TestKit" in filename:
            continue
        for line in cls.iter("line"):
            key = (filename, line.get("number"))
            lines[key] = max(lines.get(key, 0), int(line.get("hits", "0")))
            cond = line.get("condition-coverage")
            if cond and "(" in cond:
                covered, total = cond.split("(")[1].rstrip(")").split("/")
                old = branches.get(key, (0, 0))
                branches[key] = (max(old[0], int(covered)), max(old[1], int(total)))

if not lines:
    sys.exit("coverage-gate: no coverage data found")
line_rate = 100.0 * sum(1 for h in lines.values() if h > 0) / len(lines)
total_branches = sum(t for _, t in branches.values())
branch_rate = 100.0 * sum(c for c, _ in branches.values()) / total_branches if total_branches else 100.0
print(f"line coverage   {line_rate:6.2f}%  (gate {args.line}%)")
print(f"branch coverage {branch_rate:6.2f}%  (gate {args.branch}%)")
if line_rate < args.line or branch_rate < args.branch:
    sys.exit("coverage-gate: below threshold")
