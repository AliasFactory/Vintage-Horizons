namespace VintageHorizons.Checks;

/// <summary>
/// The builders for the plain data that the LOD types use.
///
/// A caller can construct each item here without a world, a chunk or an API. That property is
/// what makes the fast tier possible. Thus these helpers never use a game object.
/// </summary>
public static class Fixtures
{
    public const int Total = LodSection.GridSize * LodSection.GridSize;

    /// <summary>
    /// A section with one palette entry for each color that the caller gives, and with the
    /// given columns filled. A run in a column points at a palette id, exactly as a run in a
    /// captured section does after the remap.
    /// </summary>
    public static LodSection Section(params (int Col, ulong[] Runs)[] columns)
    {
        var s = new LodSection();
        foreach ((int col, ulong[] runs) in columns) s.SetColumn(col, runs);
        return s;
    }

    /// <summary>A section in which each column holds one run of palette id 0, at the full
    /// height.</summary>
    public static LodSection SolidSection(int paletteId = 0, int yTop = 8, int yBottom = 0)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: paletteId + 1, color: 0x00806040, flags: 0);
        ulong[] run = { LodSection.PackRun(paletteId, yTop, yBottom) };
        for (int col = 0; col < Total; col++) s.SetColumn(col, run);
        return s;
    }

    /// <summary>
    /// The snapshot that LodSaveSnapshot.Of builds, without the world lookup that turns a
    /// palette BlockId into a code. The caller gives the codes directly, thus this needs no
    /// registry.
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

    /// <summary>A snapshot for the meshing. There is one palette array for each entry, which
    /// matches SectionSnapshot.Of.</summary>
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
