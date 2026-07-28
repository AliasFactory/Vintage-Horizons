#!/usr/bin/env python3
"""Assert on what a sandbox run actually logged.

The mod reports its whole internal state through a handful of format strings whose
shape is stable, so a run's logs are a machine-readable record of what the pipeline
did. That is what makes the sandbox tiers assertable rather than just watchable.

Every claim in DESIGN.md and docs/STATUS.md was originally established by reading
these same numbers off a screen by hand, once. This reads them every time.

Usage:
    check-log.py <client-main.log> [options]

Options are assertions; with none, only the universal invariants are checked.
"""

import argparse
import re
import sys

# --- Log line shapes. Quoted from the format strings in the C# source. ---

# LodPipeline.cs: logger.Notification("LOD cache: {0}", dbPath)
CACHE_OPEN = re.compile(r"LOD cache: (\S+)")

# VintageHorizonsModSystem.cs: "Level finalized. LOD capture active
# (render distance: ..., {0} sections from cache{1})."
LEVEL_FINALIZED = re.compile(
    r"Level finalized\. LOD capture active \([^,]+, (\d+) sections from cache")

# The primary counter line. Matched on labels rather than field positions, because
# the storage line below genuinely renders its placeholders out of order and a
# positional match would silently drift if anyone reordered these too.
STATS = re.compile(
    r"(\d+) sections resident \[(?P<levels>[^\]]*)\] "
    r"\((?P<evicted>\d+) RAM-evicted, (?P<from_cache>\d+) from cache\), "
    r"(?P<meshes>\d+) meshes \((?P<mesh_evicted>\d+) evicted\), "
    r"(?P<selected>\d+) selected \[(?P<drawn>[^\]]*)\] minus (?P<culled>\d+) frustum-culled, "
    r"(?P<columns>\d+) columns captured, (?P<pending>\d+) pending, "
    r"worker: (?P<qcap>\d+) captures / (?P<qmesh>\d+) meshes queued / "
    r"(?P<cap_err>\d+)\+(?P<mesh_err>\d+) errors, "
    r"(?P<mip>\d+) awaiting mip, (?P<render_dirty>\d+) render-dirty, (?P<unsaved>\d+) unsaved")

STORAGE = re.compile(
    r"storage thread: (?P<backlog>\d+) write backlog, (?P<written>\d+) written, "
    r"(?P<write_err>\d+) write errors, (?P<read>\d+) read, "
    r"(?P<in_flight>\d+) async loads in flight, (?P<read_err>\d+) read errors")

ASSIST = re.compile(
    r"server assist: (?P<offered>\d+) offered, (?P<remote_only>\d+) remote-only, "
    r"(?P<wanted>\d+) wanted by view, (?P<requested>\d+) requested, "
    r"(?P<received>\d+) received, (?P<installed>\d+) installed, "
    r"(?P<in_flight>\d+) in flight, (?P<declined>\d+) declined")

FILL_IN = re.compile(r"Fill-in: (\d+) meshes after ([\d.]+)s")

MANIFEST = re.compile(r"server key manifest complete . (\d+) keys received")

# Lines that are a failure by their mere presence.
FATAL_LINES = [
    ("First capture error was", "a capture job threw"),
    ("First mesh error was", "a mesh job threw"),
    ("First storage-write error was", "a storage write threw"),
    ("lodterrain shader failed to compile", "the LOD shader did not compile"),
    ("!= TINT_SLOTS", "shader tint slot count disagrees with the C# constant"),
    ("Deleting unreadable cached section", "a cached section could not be read back"),
    ("is not ours", "the cache format version was rejected and the cache was discarded"),
    ("Storage drain timed out", "sections were still unwritten at shutdown"),
    ("Could not open LOD cache", "the cache database could not be opened"),
]


class Report:
    def __init__(self):
        self.passed = 0
        self.failures = []
        self.notes = []

    def ok(self, condition, what, detail=""):
        if condition:
            self.passed += 1
        else:
            self.failures.append(f"{what}" + (f"\n        {detail}" if detail else ""))

    def note(self, text):
        self.notes.append(text)

    def finish(self, label):
        for note in self.notes:
            print(f"      - {note}")
        total = self.passed + len(self.failures)
        if self.failures:
            print(f"  {label}: {len(self.failures)} FAILED of {total}")
            for failure in self.failures:
                print(f"      x {failure}")
            return 1
        print(f"  {label}: {self.passed} ok")
        return 0


def last_match(pattern, lines):
    """The final occurrence, which is the settled state rather than a mid-run sample."""
    found = None
    for line in lines:
        m = pattern.search(line)
        if m:
            found = m
    return found


def main():
    p = argparse.ArgumentParser()
    p.add_argument("log")
    p.add_argument("--label", default="smoke")
    p.add_argument("--expect-capture", action="store_true",
                   help="sections, meshes and columns must all be non-zero")
    p.add_argument("--expect-cache-loaded", action="store_true",
                   help="the run must have loaded sections from an existing cache")
    p.add_argument("--expect-assist", choices=["connected", "absent", "off"],
                   help="what the client should have concluded about the server")
    p.add_argument("--expect-fetched", action="store_true",
                   help="sections must have been installed from the server")
    p.add_argument("--expect-declined", action="store_true",
                   help="the server must have refused at least one section")
    p.add_argument("--expect-no-fetch", action="store_true",
                   help="no section may have been installed from the server")
    p.add_argument("--expect-idle", action="store_true",
                   help="the mod must have deferred to another LOD mod")
    args = p.parse_args()

    try:
        with open(args.log, encoding="utf-8", errors="replace") as f:
            lines = f.readlines()
    except OSError as e:
        print(f"  {args.label}: CANNOT READ LOG\n      x {e}")
        return 1

    r = Report()
    r.ok(len(lines) > 0, "the log is not empty")

    # --- Universal: nothing may have gone wrong. ---
    for needle, meaning in FATAL_LINES:
        hits = [l for l in lines if needle in l]
        r.ok(not hits, f"no '{needle}' ({meaning})",
             hits[0].strip() if hits else "")

    if args.expect_idle:
        idle = [l for l in lines if "is staying idle to avoid drawing over it" in l]
        r.ok(bool(idle), "the mod deferred to the competing LOD mod")
        r.ok(not any(CACHE_OPEN.search(l) for l in lines),
             "an idle mod opens no cache")
        return r.finish(args.label)

    # --- Exactly one cache per process. ---
    # Two lines naming one file is not cosmetic: it was the singleplayer bug where the
    # client and the in-process server each opened the same database, duplicating the
    # cache, the work and the memory. This assertion is that bug's regression test.
    caches = [CACHE_OPEN.search(l).group(1) for l in lines if CACHE_OPEN.search(l)]
    r.ok(len(caches) == 1, "exactly one LOD cache is opened",
         f"opened {len(caches)}: {caches}")

    finalized = last_match(LEVEL_FINALIZED, lines)
    r.ok(finalized is not None, "the level finalized and capture started")

    if args.expect_cache_loaded and finalized:
        loaded = int(finalized.group(1))
        r.ok(loaded > 0, "sections were loaded from the existing cache",
             f"loaded {loaded}")
        r.note(f"{loaded} sections restored from cache")

    stats = last_match(STATS, lines)
    r.ok(stats is not None, "the mod reported its statistics")

    if stats:
        g = stats.groupdict()
        r.ok(int(g["cap_err"]) == 0, "no capture errors", f"{g['cap_err']} errors")
        r.ok(int(g["mesh_err"]) == 0, "no mesh errors", f"{g['mesh_err']} errors")

        if args.expect_capture:
            r.ok(int(stats.group(1)) > 0, "sections are resident", stats.group(0))
            r.ok(int(g["meshes"]) > 0, "meshes were built", f"{g['meshes']} meshes")
            r.ok(int(g["columns"]) > 0, "columns were captured", f"{g['columns']} columns")

        r.note(f"{stats.group(1)} sections [{g['levels']}], {g['meshes']} meshes, "
               f"{g['columns']} columns, {g['selected']} selected")

    storage = last_match(STORAGE, lines)
    if storage:
        s = storage.groupdict()
        r.ok(int(s["write_err"]) == 0, "no storage write errors", f"{s['write_err']} errors")
        r.ok(int(s["read_err"]) == 0, "no storage read errors", f"{s['read_err']} errors")
        r.note(f"storage: {s['written']} written, {s['read']} read")

    # --- Server assist ---
    if args.expect_assist:
        status_lines = [l for l in lines if "VintageHorizons: " in l and "assist" in l]
        joined = " ".join(status_lines)
        if args.expect_assist == "connected":
            r.ok("server assist connected to server" in joined,
                 "the client connected to the server assist", joined[:300])
        elif args.expect_assist == "absent":
            r.ok(not any("server assist connected" in l for l in lines),
                 "the client found no server assist, as expected")
        elif args.expect_assist == "off":
            r.ok("but the assist is off" in joined,
                 "the client reported the server assist as off", joined[:300])

    assist = last_match(ASSIST, lines)
    if assist:
        a = assist.groupdict()
        r.note(f"assist: {a['offered']} offered, {a['installed']} installed, "
               f"{a['declined']} declined")

        if args.expect_fetched:
            r.ok(int(a["installed"]) > 0, "sections were installed from the server",
                 assist.group(0))
        if args.expect_declined:
            r.ok(int(a["declined"]) > 0, "the server declined sections outside its radius",
                 assist.group(0))
        if args.expect_no_fetch:
            r.ok(int(a["installed"]) == 0, "nothing was fetched from the server",
                 assist.group(0))
    else:
        if args.expect_fetched:
            r.ok(False, "sections were installed from the server", "no assist stats line found")
        if args.expect_declined:
            r.ok(False, "the server declined sections", "no assist stats line found")

    manifest = last_match(MANIFEST, lines)
    if manifest:
        r.note(f"manifest: {manifest.group(1)} keys received")

    # --- Throughput, reported and not gated. ---
    # These are load-dependent: the mesh-pool A/B showed one run capturing four times
    # the columns of another in the same window. Worth watching as a trend, wrong to
    # fail a build on.
    fills = [(int(m.group(1)), float(m.group(2)))
             for m in (FILL_IN.search(l) for l in lines) if m]
    if fills:
        r.note("fill-in: " + ", ".join(f"{n} meshes @ {t:.1f}s" for n, t in sorted(fills)))

    return r.finish(args.label)


if __name__ == "__main__":
    sys.exit(main())
