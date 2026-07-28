namespace VintageHorizons.Checks;

/// <summary>
/// Builders for the plain data the LOD types operate on. Everything here is constructible
/// without a world, a chunk or an API — that property is what makes the fast tier possible,
/// so these helpers deliberately never reach for a game object.
/// </summary>
public static class Fixtures
{
    public const int Total = LodSection.GridSize * LodSection.GridSize;

    /// <summary>
    /// A section with one palette entry per supplied colour and the given columns filled.
    /// Column runs reference palette ids, exactly as a captured section's do after remap.
    /// </summary>
    public static LodSection Section(params (int Col, ulong[] Runs)[] columns)
    {
        var s = new LodSection();
        foreach ((int col, ulong[] runs) in columns) s.SetColumn(col, runs);
        return s;
    }

    /// <summary>A section whose every column carries one full-height run of palette id 0.</summary>
    public static LodSection SolidSection(int paletteId = 0, int yTop = 8, int yBottom = 0)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: paletteId + 1, color: 0x00806040, flags: 0);
        ulong[] run = { LodSection.PackRun(paletteId, yTop, yBottom) };
        for (int col = 0; col < Total; col++) s.SetColumn(col, run);
        return s;
    }

    /// <summary>
    /// The snapshot LodSaveSnapshot.Of would build, minus the world lookup that turns
    /// palette BlockIds into codes. Codes are supplied directly so no registry is needed.
    /// </summary>
    public static LodSaveSnapshot Snapshot(LodSection section, int level = 0, int sx = 0, int sz = 0,
        bool applyToParent = false, string[]? codes = null)
    {
        int count = section.Palette.Count;
        var colors = new int[count];
        var flags = new byte[count];
        for (int i = 0; i < count; i++)
        {
            colors[i] = section.Palette[i].Color;
            flags[i] = section.Palette[i].Flags;
        }

        return new LodSaveSnapshot
        {
            Level = level,
            SX = sx,
            SZ = sz,
            ApplyToParent = applyToParent,
            PaletteCodes = codes ?? Enumerable.Range(0, count).Select(i => "game:testblock-" + i).ToArray(),
            PaletteColors = colors,
            PaletteFlags = flags,
            Runs = (ulong[])section.Runs.Clone(),
            ColumnStart = (int[])section.ColumnStart.Clone(),
            Captured = (bool[])section.Captured.Clone(),
        };
    }

    /// <summary>Snapshot for meshing. Palette arrays are per-entry, matching SectionSnapshot.Of.</summary>
    public static SectionSnapshot Snap(LodSection s)
    {
        var colors = new int[s.Palette.Count];
        var flags = new byte[s.Palette.Count];
        var slots = new byte[s.Palette.Count];
        for (int i = 0; i < s.Palette.Count; i++)
        {
            colors[i] = s.Palette[i].Color;
            flags[i] = s.Palette[i].Flags;
            slots[i] = s.Palette[i].TintSlot;
        }
        return new SectionSnapshot
        {
            Runs = s.Runs,
            ColumnStart = s.ColumnStart,
            Captured = (bool[])s.Captured.Clone(),
            PaletteColors = colors,
            PaletteFlags = flags,
            PaletteTintSlots = slots,
        };
    }

    public static MeshJob Job(LodSection self, long key = 0, SectionSnapshot?[]? neighbors = null) =>
        new()
        {
            Key = key,
            Self = Snap(self),
            Neighbors = neighbors ?? new SectionSnapshot?[4],
        };
}
