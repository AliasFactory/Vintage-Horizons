#!/usr/bin/env python3
"""Join the benchmark CSV files into one table for a comparison.

    scripts/bench-compare.py [bench-dir]

This script reads each <label>.csv that scripts/bench.sh wrote. For each waypoint it
prints the frame timings of each configuration, side by side. When a 'vanilla' baseline
is present, it also prints the cost against that baseline.

The job of a distance mod is to draw more terrain. Thus the important number is the frame
time that the mod spends on that work. The raw fps of a client that draws nothing past
224 blocks is not the important number.
"""
import csv
import glob
import os
import sys

bench_dir = sys.argv[1] if len(sys.argv) > 1 else ".testdata/bench"

rows = []
for path in sorted(glob.glob(os.path.join(bench_dir, "*.csv"))):
    with open(path, newline="") as f:
        rows.extend(csv.DictReader(f))

if not rows:
    sys.exit(f"no CSVs found in {bench_dir}")

labels, waypoints = [], []
for r in rows:
    if r["label"] not in labels:
        labels.append(r["label"])
    if r["waypoint"] not in waypoints:
        waypoints.append(r["waypoint"])

by_key = {(r["label"], r["waypoint"]): r for r in rows}

# 'vanilla' means no LOD mod at all. It is the lowest cost, and each mod is measured
# against it.
baseline = "vanilla" if "vanilla" in labels else None

hdr = f"{'waypoint':<18} {'configuration':<24} {'fps avg':>8} {'1% low':>8} {'ms avg':>7} {'ms 1%':>7} {'RSS MB':>7}"
if baseline:
    hdr += f" {'vs ' + baseline:>14}"
print(hdr)
print("-" * len(hdr))

for wp in waypoints:
    base = by_key.get((baseline, wp)) if baseline else None
    for label in labels:
        r = by_key.get((label, wp))
        if not r:
            continue
        line = (f"{wp:<18} {label:<24} {float(r['fps_avg']):>8.0f} {float(r['fps_1pct_low']):>8.0f} "
                f"{float(r['frame_ms_avg']):>7.2f} {float(r['frame_ms_1pct_low']):>7.2f} {int(r['rss_mb']):>7}")
        if base and label != baseline:
            # The added frame time is the honest measure of the cost. A ratio of fps values
            # gives too large a number at a high frame rate, where a fraction of a
            # millisecond appears as a large percentage.
            extra_ms = float(r["frame_ms_avg"]) - float(base["frame_ms_avg"])
            extra_mb = int(r["rss_mb"]) - int(base["rss_mb"])
            line += f" {extra_ms:>+7.2f}ms {extra_mb:>+5}MB"
        print(line)
    print()

print("Screenshots for visual comparison, same vantage point per configuration:")
for wp in waypoints:
    shots = sorted(glob.glob(os.path.join(bench_dir, f"*--{wp}.png")))
    if shots:
        print(f"  {wp}: " + "  ".join(os.path.basename(s) for s in shots))
