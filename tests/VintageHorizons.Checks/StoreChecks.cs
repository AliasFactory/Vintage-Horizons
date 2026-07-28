namespace VintageHorizons.Checks;

/// <summary>
/// The on-disk blob format, round-tripped with no database.
///
/// LodStore extends the game's SQLite base class, but Serialize is static and the base
/// constructor only stores its logger — so the format can be exercised without opening a
/// file, and DeserializeForeign explicitly accepts a null world to defer block-id lookup
/// to the main thread. That same door is what the network path uses for foreign sections.
/// </summary>
public static class StoreChecks
{
    public static void Run(Check c)
    {
        RoundTrip(c);
        DeferredPalette(c);
        Rejection(c);
    }

    static void RoundTrip(Check c)
    {
        var section = new LodSection();
        int stone = section.FindOrAddPaletteEntry(blockId: 11, color: 0x00445566, flags: 0);
        int water = section.FindOrAddPaletteEntry(blockId: 22, color: 0x00112233,
            flags: LodPaletteEntry.FlagWater, tintSlot: 9);

        section.SetColumn(0, new[] { LodSection.PackRun(stone, 30, 0) });
        section.SetColumn(1, new[] { LodSection.PackRun(water, 40, 30), LodSection.PackRun(stone, 30, 0) });
        section.SetColumn(Fixtures.Total - 1, new[] { LodSection.PackRun(stone, 12, 0) });

        LodSection back = Restore(c, section);
        if (back == null!) return;

        c.SeqEq(section.Runs, back.Runs, "runs survive the round trip");
        c.SeqEq(section.ColumnStart, back.ColumnStart, "column offsets survive the round trip");
        c.SeqEq(section.Captured, back.Captured, "the captured bitmask survives the round trip");
        c.Eq(section.CapturedColumns, back.CapturedColumns, "the captured count is rebuilt");
        c.Eq(section.Palette.Count, back.Palette.Count, "the palette keeps its length");

        for (int i = 0; i < section.Palette.Count; i++)
        {
            c.Eq(section.Palette[i].Color, back.Palette[i].Color, $"palette[{i}] colour survives");
            c.Eq(section.Palette[i].Flags, back.Palette[i].Flags, $"palette[{i}] flags survive");
        }

        // The last column is the one an off-by-one in the 4096 run counts or the 512-byte
        // captured bitmask would lose, and losing it is invisible until terrain has a seam.
        c.True(back.Captured[Fixtures.Total - 1], "the final column survives the bitmask");
        c.SeqEq(section.ColumnRuns(Fixtures.Total - 1).ToArray(),
            back.ColumnRuns(Fixtures.Total - 1).ToArray(), "the final column's runs survive");
    }

    /// <summary>
    /// Two fields deliberately do NOT round-trip, and asserting full equality would lock in
    /// the wrong thing:
    ///   - TintSlot is never written, so an existing cache picks up corrected per-species
    ///     tints without re-capturing, and stays right when a game update remaps them.
    ///   - BlockId cannot be resolved off the main thread, so a null world defers the codes
    ///     into PendingPaletteCodes for the main thread to resolve on install.
    /// </summary>
    static void DeferredPalette(Check c)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 11, color: 0x00445566, flags: 0, tintSlot: 5);
        section.SetColumn(0, new[] { LodSection.PackRun(0, 10, 0) });

        string[] codes = { "game:rock-granite" };
        LodSection back = Restore(c, section, codes);
        if (back == null!) return;

        c.True(back.PendingPaletteCodes != null, "a null world defers palette codes for the main thread");
        c.SeqEq(codes, back.PendingPaletteCodes!, "the deferred codes are the ones written");
        c.Eq(0, back.Palette[0].BlockId, "block ids stay unresolved until the main thread runs");
        c.Eq((byte)0, back.Palette[0].TintSlot, "tint slots are re-derived, never persisted");
        c.Eq(0x00445566, back.Palette[0].Color, "colour is persisted and comes back");
    }

    /// <summary>
    /// Bad input must come back null, never throw. The storage thread deserializes rows off
    /// the main thread and the network path deserializes whatever a server sent; an
    /// exception on either takes down more than the one bad section.
    /// </summary>
    static void Rejection(Check c)
    {
        var store = new LodStore(null!);
        byte[] good = LodStore.Serialize(Fixtures.Snapshot(Fixtures.SolidSection()));

        c.NoThrow(() => store.DeserializeForeign(Array.Empty<byte>(), null), "an empty blob does not throw");
        c.Eq(null, store.DeserializeForeign(Array.Empty<byte>(), null), "an empty blob returns null");

        c.Eq(null, store.DeserializeForeign(new byte[] { 4 }, null), "a one-byte blob returns null");

        byte[] wrongVersion = (byte[])good.Clone();
        wrongVersion[0] = 99;
        c.Eq(null, store.DeserializeForeign(wrongVersion, null), "a blob from a future format returns null");

        // Truncation is the realistic corruption: a partial write, or a section cut short
        // in transit.
        byte[] truncated = good[..(good.Length / 2)];
        c.NoThrow(() => store.DeserializeForeign(truncated, null), "a truncated blob does not throw");
        c.Eq(null, store.DeserializeForeign(truncated, null), "a truncated blob returns null");

        byte[] garbage = (byte[])good.Clone();
        for (int i = 1; i < garbage.Length; i += 3) garbage[i] ^= 0xA5;
        c.NoThrow(() => store.DeserializeForeign(garbage, null), "a corrupted blob does not throw");

        c.True(store.DeserializeForeign(good, null) != null, "a good blob still deserializes");
    }

    static LodSection Restore(Check c, LodSection section, string[]? codes = null)
    {
        byte[] blob = LodStore.Serialize(Fixtures.Snapshot(section, codes: codes));
        LodSection? back = new LodStore(null!).DeserializeForeign(blob, null);
        c.True(back != null, "the blob deserializes");
        return back!;
    }
}
