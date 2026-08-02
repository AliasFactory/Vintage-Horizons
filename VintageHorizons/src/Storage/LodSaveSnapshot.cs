using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// A copy of a section, for persistence on another thread.
///
/// The main thread copies each item that the storage thread needs, because the live section
/// continues to change. The mod appends palette entries. It writes Captured in place. And
/// LodSection.SetColumn edits Runs and ColumnStart in place, when the run count of a column
/// does not change.
///
/// The mod also finds the block CODES here. The storage thread must never touch the block
/// registry of the game.
///
/// The copies are a few hundred KB of memcpy. They let the mod move approximately 20 ms of
/// deflate and SQLite work off the render thread.
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
