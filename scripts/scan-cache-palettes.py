#!/usr/bin/env python3
"""Scan VintageHorizons LOD caches for sections whose palette colours are missing.

Reports, per cache file, how many sections have zero-colour palette entries and
which block codes they carry, so a report of black or dark distant terrain becomes
a number instead of a guess about shader maths.

Ported from DistantVistas (github.com/leroysquad/DistantVistas, MIT, same lineage),
blob format v4 verified against VintageHorizons/src/Storage/LodStore.cs (2026-09-01).

Blob format v4: [1 byte version][deflate: uint16 paletteCount,
then per entry: 7-bit-length-prefixed UTF8 code, int32 colour, byte flags ...]
"""
import collections
import glob
import sqlite3
import struct
import sys
import zlib


def read_7bit_string(buf, i):
    n = 0
    shift = 0
    while True:
        b = buf[i]
        i += 1
        n |= (b & 0x7F) << shift
        if not (b & 0x80):
            break
        shift += 7
    return buf[i:i + n].decode("utf8"), i + n


def palette_of(blob):
    if not blob or blob[0] != 4:
        return None
    raw = zlib.decompressobj(-zlib.MAX_WBITS).decompress(blob[1:])
    count = struct.unpack_from("<H", raw, 0)[0]
    i = 2
    entries = []
    for _ in range(count):
        code, i = read_7bit_string(raw, i)
        colour = struct.unpack_from("<i", raw, i)[0]
        i += 4
        flags = raw[i]
        i += 1
        entries.append((code, colour & 0xFFFFFFFF, flags))
    return entries


def main():
    if len(sys.argv) != 2:
        print(f"usage: {sys.argv[0]} <cache.db-glob>", file=sys.stderr)
        print('example: scan-cache-palettes.py ".testdata/ModData/vintagehorizons/*.db"',
              file=sys.stderr)
        return 2

    for path in sorted(glob.glob(sys.argv[1])):
        con = sqlite3.connect("file:" + path + "?mode=ro", uri=True)
        total = uncoloured = partial = 0
        zero_colour = collections.Counter()
        near_white = collections.Counter()
        for detail, sx, sz, data in con.execute("SELECT Detail,SX,SZ,Data FROM Section"):
            try:
                pal = palette_of(data)
            except Exception:
                continue
            if not pal:
                continue
            total += 1
            zeros = [e for e in pal if e[1] == 0]
            # unknown.png measures 0xFCFCFC; HD packs can land nearby.
            whites = [e for e in pal
                      if e[1] != 0 and min(e[1] & 0xFF, (e[1] >> 8) & 0xFF,
                                           (e[1] >> 16) & 0xFF) >= 0xF0]
            if zeros and len(zeros) == len(pal):
                uncoloured += 1
                if uncoloured <= 3:
                    print(f"    all-uncoloured section L{detail} ({sx},{sz}): "
                          f"{[e[0] for e in pal][:4]}")
            elif zeros:
                partial += 1
                for e in zeros:
                    zero_colour[e[0]] += 1
            for e in whites:
                near_white[e[0]] += 1
        name = path.split("/")[-1]
        print(f"{name}: {total} parsed, {uncoloured} entirely uncoloured, "
              f"{partial} partly")
        for code, n in zero_colour.most_common(6):
            print(f"    zero-colour entry {code} x{n}")
        for code, n in near_white.most_common(6):
            print(f"    near-white entry {code} x{n}")
        con.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
