using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// The safety rule behind sweeping and generation: which columns a bulk pass can load,
/// which it can peek, and which it must not touch. This rule keeps two promises. The
/// sweep must not make the engine generate terrain. Generation must never peek a column
/// that exists, because a peek regenerates from the seed and would describe the terrain
/// as it was before anyone built on it.
/// </summary>
public static class GenerateChecks
{
    public static void Run(Check c)
    {
        KeyInjective(c);
        Neighbourhood(c);
        ClassifyRule(c);
        GeneratorConstants(c);
        AbsentSampling(c);
    }

    static void GeneratorConstants(Check c)
    {
        // A canary, with the reason in the message: raising the pass without the
        // Harmony guard TopoHorizon ships crashes vanilla worldgen.
        c.Eq(Vintagestory.API.Server.EnumWorldGenPass.Terrain, LodPlayerPregen.Pass,
            "peeks stop at Terrain: Vegetation NREs in vanilla worldgen without a Harmony guard we do not ship");

        // These numbers go to the person who typed the command.
        c.Eq(1, LodPlayerPregen.ColumnsFor(0), "radius 0 is one column");
        c.Eq(2401, LodPlayerPregen.ColumnsFor(24), "radius 24 is 2401 columns");
        c.Eq(66049, LodPlayerPregen.ColumnsFor(128), "radius 128 is 66049 columns");
        c.True(LodPlayerPregen.EstimateSeconds(2401, 16) > 0, "a real run estimates a non-zero time");
        c.Eq(0, LodPlayerPregen.EstimateSeconds(1, 16), "one column rounds to zero seconds, not a crash");
    }

    static void AbsentSampling(Check c)
    {
        // The verify sample must contain only absent positions - a present position in
        // the sample would count a legitimate load as a broken promise.
        const int R = 10;
        var map = new LodColumnMap();
        for (int cz = -R; cz <= R; cz++)
        for (int cx = -R; cx <= R; cx++)
        {
            map.Add(cx, cz);
        }

        List<long> sample = map.AbsentSample(0, 0, 14, 64);
        bool onlyAbsent = true, inRadius = true;
        foreach (long key in sample)
        {
            int cx = LodColumnMap.KeyCx(key), cz = LodColumnMap.KeyCz(key);
            onlyAbsent &= !map.Contains(cx, cz);
            inRadius &= Math.Max(Math.Abs(cx), Math.Abs(cz)) <= 14;
        }
        c.True(onlyAbsent, "the sample holds only positions absent from the map");
        c.True(inRadius, "the sample stays inside the requested radius");
        c.Eq(64, sample.Count, "a large absent set fills the sample to its cap exactly");

        // 29x29 window minus the 21x21 square = 400 absents; ask for more than exist.
        List<long> all = map.AbsentSample(0, 0, 14, 1000);
        c.Eq(29 * 29 - 21 * 21, all.Count, "a cap above the absent count returns every absent position");

        c.Eq(0, map.AbsentSample(0, 0, R - LodColumnMap.SafeNeighbourhood, 100).Count,
            "a fully generated area has nothing to sample");

        // Round-trip of the key unpacking the verifier depends on, at negatives too.
        c.Eq(-37, LodColumnMap.KeyCx(LodColumnMap.Key(-37, 91)), "KeyCx round-trips a negative cx");
        c.Eq(91, LodColumnMap.KeyCz(LodColumnMap.Key(-37, 91)), "KeyCz round-trips");
    }

    static void KeyInjective(Check c)
    {
        // The packing looks obviously fine and is exactly the kind of thing that breaks
        // at negative coordinates. World coordinates are non-negative today; the map
        // must not silently rely on that.
        var seen = new HashSet<long>();
        for (int cz = -64; cz <= 64; cz++)
        for (int cx = -64; cx <= 64; cx++)
        {
            seen.Add(LodColumnMap.Key(cx, cz));
        }
        c.Eq(129 * 129, seen.Count, "Key is injective over a -64..64 grid");
    }

    static void Neighbourhood(Check c)
    {
        c.Eq(4, LodColumnMap.SafeNeighbourhood,
            "the neighbourhood width is the measured 4 (no check 1460, r1 714, r2 509, r4 0 columns generated)");

        int r = LodColumnMap.SafeNeighbourhood;

        // A full 9x9 is complete, including at negative coordinates.
        foreach ((int ox, int oz) in new[] { (10, 10), (-100, -100), (0, 0) })
        {
            var map = new LodColumnMap();
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                map.Add(ox + dx, oz + dz);
            }
            c.True(map.NeighbourhoodComplete(ox, oz), $"a full 9x9 around ({ox},{oz}) is complete");
        }

        // Any single missing cell breaks it. All 81 positions, not a sample: a corner
        // that the loop misses is exactly the bug this exists to catch.
        int broken = 0;
        for (int missZ = -r; missZ <= r; missZ++)
        for (int missX = -r; missX <= r; missX++)
        {
            var map = new LodColumnMap();
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx == missX && dz == missZ) continue;
                map.Add(10 + dx, 10 + dz);
            }
            if (!map.NeighbourhoodComplete(10, 10)) broken++;
        }
        c.Eq(81, broken, "each of the 81 cells is load-bearing: removing any one breaks completeness");
    }

    static void ClassifyRule(Check c)
    {
        int r = LodColumnMap.SafeNeighbourhood;

        // An empty map peeks everywhere. No frontier rule applies to a peek, and that
        // asymmetry is the difference between generation and the sweep.
        var empty = new LodColumnMap();
        bool allPeek = true;
        foreach ((int cx, int cz) in new[] { (0, 0), (500, 500), (-3, 7), (100000, -100000) })
        {
            allPeek &= empty.Classify(cx, cz) == EnumColumnAction.Peek;
        }
        c.True(allPeek, "an empty map classifies every position Peek, neighbours or none");

        // A filled square of Chebyshev radius 10 around the origin. The expected action
        // at every position follows from the distance alone, so assert the whole window
        // against the formula and then the three counts against each other.
        const int R = 10;
        var disc = new LodColumnMap();
        for (int cz = -R; cz <= R; cz++)
        for (int cx = -R; cx <= R; cx++)
        {
            disc.Add(cx, cz);
        }

        int load = 0, frontier = 0, peek = 0, mismatches = 0, peekedExisting = 0;
        const int W = 20;
        for (int cz = -W; cz <= W; cz++)
        for (int cx = -W; cx <= W; cx++)
        {
            int cheb = Math.Max(Math.Abs(cx), Math.Abs(cz));
            EnumColumnAction expected =
                cheb <= R - r ? EnumColumnAction.Load
                : cheb <= R   ? EnumColumnAction.SkipFrontier
                :               EnumColumnAction.Peek;

            EnumColumnAction got = disc.Classify(cx, cz);
            if (got != expected) mismatches++;
            if (got == EnumColumnAction.Peek && disc.Contains(cx, cz)) peekedExisting++;

            switch (got)
            {
                case EnumColumnAction.Load: load++; break;
                case EnumColumnAction.SkipFrontier: frontier++; break;
                case EnumColumnAction.Peek: peek++; break;
            }
        }

        c.Eq(0, mismatches, "every position in the window classifies by Chebyshev distance alone");
        c.Eq((2 * (R - r) + 1) * (2 * (R - r) + 1), load, "Load covers the interior square");
        c.Eq((2 * R + 1) * (2 * R + 1) - (2 * (R - r) + 1) * (2 * (R - r) + 1), frontier,
            "SkipFrontier covers the outer rings of the existing square");
        c.Eq((2 * W + 1) * (2 * W + 1) - (2 * R + 1) * (2 * R + 1), peek,
            "Peek covers exactly the positions that do not exist");

        // The non-destructive assertion. A peek of an existing column would cache a
        // horizon from before the player built there.
        c.Eq(0, peekedExisting, "Classify never returns Peek for a column that exists");
    }
}
