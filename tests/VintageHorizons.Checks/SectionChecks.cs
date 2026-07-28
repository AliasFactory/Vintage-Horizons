namespace VintageHorizons.Checks;

/// <summary>
/// The RLE column store. Runs live in one flat array with per-column prefix offsets, so
/// every mutation is index arithmetic over a shared buffer — a class of code where an
/// off-by-one does not crash, it just hands the mesher another column's terrain.
/// </summary>
public static class SectionChecks
{
    public static void Run(Check c)
    {
        RunPacking(c);
        ColumnIndexing(c);
        SetColumnPaths(c);
        ReplaceColumnsPaths(c);
        FlagRemoval(c);
        PaletteReuse(c);
    }

    static void RunPacking(Check c)
    {
        foreach ((int id, int top, int bottom) in new[] { (0, 1, 0), (5, 100, 40), (63, 16383, 0), (1000, 255, 254) })
        {
            ulong run = LodSection.PackRun(id, top, bottom);
            c.Eq(id, LodSection.RunPaletteId(run), $"palette id round-trips ({id},{top},{bottom})");
            c.Eq(top, LodSection.RunYTop(run), $"yTop round-trips ({id},{top},{bottom})");
            c.Eq(bottom, LodSection.RunYBottom(run), $"yBottom round-trips ({id},{top},{bottom})");
        }

        // The field comment says paletteId(16), but RunPaletteId shifts without masking, so
        // the field actually runs to the top of the ulong. That is load-bearing rather than
        // sloppy: LodWorker.Capture packs raw BLOCK ids here and the main thread remaps them
        // to palette ids on apply, and block ids exceed 16 bits in a modded game. Asserting
        // the comment instead of the code would break exactly those installs.
        const int bigBlockId = 70000;
        ulong wide = LodSection.PackRun(bigBlockId, 200, 100);
        c.Eq(bigBlockId, LodSection.RunPaletteId(wide), "run ids wider than 16 bits survive (modded block ids)");
        c.Eq(200, LodSection.RunYTop(wide), "a wide id does not bleed into yTop");
        c.Eq(100, LodSection.RunYBottom(wide), "a wide id does not bleed into yBottom");

        // y fields are 14 bits and wrap by masking; worlds are far shallower than 16384.
        ulong maxY = LodSection.PackRun(1, 0x3FFF, 0x3FFF);
        c.Eq(0x3FFF, LodSection.RunYTop(maxY), "yTop holds its full 14-bit range");
        c.Eq(0x3FFF, LodSection.RunYBottom(maxY), "yBottom holds its full 14-bit range");
    }

    static void ColumnIndexing(Check c)
    {
        c.Eq(0, LodSection.ColumnIndex(0, 0), "column 0,0 is index 0");
        c.Eq(1, LodSection.ColumnIndex(1, 0), "x is the fast axis");
        c.Eq(LodSection.GridSize, LodSection.ColumnIndex(0, 1), "z strides by GridSize");

        var seen = new HashSet<int>();
        for (int cz = 0; cz < LodSection.GridSize; cz++)
        {
            for (int cx = 0; cx < LodSection.GridSize; cx++) seen.Add(LodSection.ColumnIndex(cx, cz));
        }
        c.Eq(Fixtures.Total, seen.Count, "the grid maps onto exactly GridSize^2 distinct indices");
        c.Eq(Fixtures.Total - 1, seen.Max(), "indices stay inside the column arrays");
    }

    static void SetColumnPaths(Check c)
    {
        var s = new LodSection();
        ulong[] two = { LodSection.PackRun(0, 10, 5), LodSection.PackRun(1, 5, 0) };

        c.True(s.SetColumn(3, two), "first write to a column reports a change");
        c.Eq(1, s.CapturedColumns, "first write marks the column captured");
        c.SeqEq(two, s.ColumnRuns(3).ToArray(), "the column reads back what was written");

        // An uncaptured column and a captured-but-empty one are different states: the
        // renderer treats "nothing here" as coverage and "not looked yet" as a reason to
        // keep the coarse parent on screen.
        c.False(s.Captured[4], "an untouched column stays uncaptured");
        c.Eq(0, s.ColumnRuns(4).Length, "an untouched column has no runs");

        c.False(s.SetColumn(3, two), "rewriting identical content reports no change");
        c.Eq(1, s.CapturedColumns, "a no-op write does not double-count the column");

        // Same length: the in-place fast path, which skips the array rebuild entirely.
        ulong[] sameLength = { LodSection.PackRun(0, 12, 6), LodSection.PackRun(1, 6, 0) };
        int[] startsBefore = (int[])s.ColumnStart.Clone();
        c.True(s.SetColumn(3, sameLength), "an equal-length change reports a change");
        c.SeqEq(sameLength, s.ColumnRuns(3).ToArray(), "the in-place path writes the new runs");
        c.SeqEq(startsBefore, s.ColumnStart, "the in-place path leaves offsets untouched");

        // Growing and shrinking must shift every later column's offset, or neighbouring
        // columns start reading from the middle of someone else's runs.
        s.SetColumn(10, two);
        ulong[] three = { LodSection.PackRun(0, 30, 20), LodSection.PackRun(1, 20, 10), LodSection.PackRun(0, 10, 0) };
        c.True(s.SetColumn(3, three), "growing a column reports a change");
        c.SeqEq(three, s.ColumnRuns(3).ToArray(), "the grown column reads back correctly");
        c.SeqEq(two, s.ColumnRuns(10).ToArray(), "a later column survives an earlier column growing");

        ulong[] one = { LodSection.PackRun(1, 8, 0) };
        c.True(s.SetColumn(3, one), "shrinking a column reports a change");
        c.SeqEq(one, s.ColumnRuns(3).ToArray(), "the shrunk column reads back correctly");
        c.SeqEq(two, s.ColumnRuns(10).ToArray(), "a later column survives an earlier column shrinking");

        c.Eq(s.Runs.Length, s.ColumnStart[Fixtures.Total], "the final prefix offset equals the run count");
        c.True(IsMonotonic(s.ColumnStart), "prefix offsets stay non-decreasing");
    }

    static void ReplaceColumnsPaths(Check c)
    {
        var s = new LodSection();
        ulong[] a = { LodSection.PackRun(0, 10, 0) };
        ulong[] b = { LodSection.PackRun(1, 20, 10), LodSection.PackRun(0, 10, 0) };

        var batch = new ulong[]?[Fixtures.Total];
        batch[0] = a;
        batch[5] = b;
        batch[Fixtures.Total - 1] = a;

        c.True(s.ReplaceColumns(batch), "a batch with new content reports a change");
        c.Eq(3, s.CapturedColumns, "the batch captured three columns");
        c.SeqEq(a, s.ColumnRuns(0).ToArray(), "batch column 0 reads back");
        c.SeqEq(b, s.ColumnRuns(5).ToArray(), "batch column 5 reads back");
        c.SeqEq(a, s.ColumnRuns(Fixtures.Total - 1).ToArray(), "the last column reads back");
        c.Eq(s.Runs.Length, s.ColumnStart[Fixtures.Total], "prefix offsets close over the run array");

        // Null entries mean "leave this column alone", which is how a chunk column applies
        // only its own 16x16 patch of a 64x64 section.
        var partial = new ulong[]?[Fixtures.Total];
        partial[5] = a;
        c.True(s.ReplaceColumns(partial), "a partial batch reports a change");
        c.SeqEq(a, s.ColumnRuns(5).ToArray(), "the replaced column changed");
        c.SeqEq(a, s.ColumnRuns(0).ToArray(), "an untouched column kept its runs");

        // Unchanged content short-circuits, and does so by nulling the caller's entry in
        // place. Callers reuse that array across sections, so this is observable behaviour,
        // not an implementation detail.
        var identical = new ulong[]?[Fixtures.Total];
        identical[0] = (ulong[])a.Clone();
        c.False(s.ReplaceColumns(identical), "a batch of identical content reports no change");
        c.Eq(null, identical[0], "an unchanged column is nulled out in the caller's batch");
    }

    static void FlagRemoval(Check c)
    {
        var s = new LodSection();
        int keep = s.FindOrAddPaletteEntry(blockId: 1, color: 0x00FFFFFF, flags: 0);
        int drop = s.FindOrAddPaletteEntry(blockId: 2, color: 0x00FF00FF, flags: LodPaletteEntry.FlagSkip);

        ulong keepRun = LodSection.PackRun(keep, 10, 0);
        ulong dropRun = LodSection.PackRun(drop, 20, 10);

        s.SetColumn(0, new[] { dropRun, keepRun });
        s.SetColumn(1, new[] { dropRun });
        s.SetColumn(2, new[] { keepRun });

        s.RemoveRunsWithFlag(LodPaletteEntry.FlagSkip);

        c.SeqEq(new[] { keepRun }, s.ColumnRuns(0).ToArray(), "a flagged run is dropped from a mixed column");
        c.Eq(0, s.ColumnRuns(1).Length, "a wholly flagged column empties");
        c.SeqEq(new[] { keepRun }, s.ColumnRuns(2).ToArray(), "an unflagged column is untouched");
        c.Eq(s.Runs.Length, s.ColumnStart[Fixtures.Total], "offsets close over the rebuilt run array");
        c.Eq(2, s.Runs.Length, "the run array is resized down to what survived");
        c.True(IsMonotonic(s.ColumnStart), "prefix offsets stay non-decreasing after removal");

        // Columns stay captured: the terrain was looked at, it just holds nothing now.
        // Clearing this would make the renderer keep a coarse parent drawn over it forever.
        c.True(s.Captured[1], "an emptied column stays captured");

        // No flagged entry means no work and, critically, no array churn.
        var untouched = new LodSection();
        untouched.FindOrAddPaletteEntry(blockId: 1, color: 0, flags: 0);
        untouched.SetColumn(0, new[] { LodSection.PackRun(0, 5, 0) });
        ulong[] before = untouched.Runs;
        untouched.RemoveRunsWithFlag(LodPaletteEntry.FlagSkip);
        c.True(ReferenceEquals(before, untouched.Runs), "a section with nothing flagged is left alone entirely");
    }

    static void PaletteReuse(Check c)
    {
        var s = new LodSection();
        int first = s.FindOrAddPaletteEntry(blockId: 7, color: 0x00112233, flags: 0);
        int again = s.FindOrAddPaletteEntry(blockId: 7, color: 0x00445566, flags: LodPaletteEntry.FlagWater);
        int other = s.FindOrAddPaletteEntry(blockId: 8, color: 0x00112233, flags: 0);

        c.Eq(first, again, "the same block id reuses its palette slot");
        c.Eq(2, s.Palette.Count, "reuse does not grow the palette");
        c.True(first != other, "a different block id gets its own slot");

        // Identity is the block id alone, so a re-add keeps the original colour and flags.
        // Capture relies on that: it re-adds entries constantly and must not thrash them.
        c.Eq(0x00112233, s.Palette[first].Color, "reuse keeps the original colour");
        c.Eq((byte)0, s.Palette[first].Flags, "reuse keeps the original flags");
    }

    static bool IsMonotonic(int[] starts)
    {
        for (int i = 1; i < starts.Length; i++)
        {
            if (starts[i] < starts[i - 1]) return false;
        }
        return true;
    }
}
