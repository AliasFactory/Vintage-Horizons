using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// A section frozen for off-thread persistence.
///
/// Everything the storage thread needs is copied on the main thread, because the
/// live section keeps mutating: palette entries are appended, Captured is written
/// in place, and LodSection.SetColumn edits Runs/ColumnStart in place when a
/// column's run count is unchanged. Block CODES are resolved here too - the
/// storage thread must never touch the game's block registry.
///
/// The copies are a few hundred KB of memcpy, against the ~20ms of deflate and
/// SQLite work they let us move off the render thread.
/// </summary>
public sealed class LodSaveSnapshot
{
    public int Level;
    public int SX;
    public int SZ;
    public bool ApplyToParent;

    public string[] PaletteCodes = Array.Empty<string>();
    public int[] PaletteColors = Array.Empty<int>();
    public byte[] PaletteFlags = Array.Empty<byte>();

    public ulong[] Runs = Array.Empty<ulong>();
    public int[] ColumnStart = Array.Empty<int>();
    public bool[] Captured = Array.Empty<bool>();

    public static LodSaveSnapshot Of(int level, int sx, int sz, LodSection section, IWorldAccessor world, bool applyToParent)
    {
        int count = section.Palette.Count;
        var codes = new string[count];
        var colors = new int[count];
        var flags = new byte[count];

        for (int i = 0; i < count; i++)
        {
            LodPaletteEntry e = section.Palette[i];
            Block? block = e.BlockId > 0 ? world.Blocks[e.BlockId] : null;
            codes[i] = block?.Code?.ToShortString() ?? "";
            colors[i] = e.Color;
            flags[i] = e.Flags;
        }

        return new LodSaveSnapshot
        {
            Level = level,
            SX = sx,
            SZ = sz,
            ApplyToParent = applyToParent,
            PaletteCodes = codes,
            PaletteColors = colors,
            PaletteFlags = flags,
            Runs = (ulong[])section.Runs.Clone(),
            ColumnStart = (int[])section.ColumnStart.Clone(),
            Captured = (bool[])section.Captured.Clone(),
        };
    }

    public int RunCount(int col) => ColumnStart[col + 1] - ColumnStart[col];
}
