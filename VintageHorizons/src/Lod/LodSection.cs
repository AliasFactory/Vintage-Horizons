using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// One entry in a section's palette: a block resolved to its LOD appearance.
/// Color is RGBA-packed (R in the low byte), resolved on the main thread when the
/// palette entry is first registered (Block.GetColor touches client-only state).
/// </summary>
public struct LodPaletteEntry
{
    public int BlockId;

    /// <summary>Untinted base color; seasonal/climate tint is applied live in the shader.</summary>
    public int Color;

    public byte Flags;

    public const byte FlagWater = 1;
    public const byte FlagTintGrass = 2;   // block uses a climate color map
    public const byte FlagTintFoliage = 4; // block uses a seasonal color map
}

/// <summary>
/// The M4 leaf data model: a section holds 64×64 vertical RLE columns over a local
/// palette. At level L each column covers (ColumnStepBlocks &lt;&lt; L) blocks, so a
/// section spans SectionBlocks &lt;&lt; L. Runs are packed ulongs, stored top-down,
/// contiguous per column, addressed by a prefix-offset table — compact, fast to
/// serialize, and cheap to mip (concepts per DESIGN.md §4, informed by DH/Voxy).
/// </summary>
public class LodSection
{
    public const int GridSize = 64;                 // columns per section edge
    public const int ColumnStepBlocks = 1;          // blocks per column at level 0 — full DH-parity resolution
    public const int SectionBlocks = GridSize * ColumnStepBlocks; // 64 at level 0

    /// <summary>Run packing: paletteId(16) | yTop(14) | yBottom(14). Run spans [yBottom, yTop).</summary>
    public static ulong PackRun(int paletteId, int yTop, int yBottom) =>
        ((ulong)(uint)paletteId << 28) | ((ulong)(uint)(yTop & 0x3FFF) << 14) | (uint)(yBottom & 0x3FFF);

    public static int RunPaletteId(ulong run) => (int)(run >> 28);
    public static int RunYTop(ulong run) => (int)((run >> 14) & 0x3FFF);
    public static int RunYBottom(ulong run) => (int)(run & 0x3FFF);

    /// <summary>Prefix offsets into Runs; column c owns Runs[ColumnStart[c] .. ColumnStart[c+1]).</summary>
    public int[] ColumnStart = new int[GridSize * GridSize + 1];

    public ulong[] Runs = Array.Empty<ulong>();

    public readonly List<LodPaletteEntry> Palette = new();

    /// <summary>Columns that have been captured at least once (empty column ≠ uncaptured column).</summary>
    public readonly bool[] Captured = new bool[GridSize * GridSize];

    public int CapturedColumns;

    public bool IsEmpty => Runs.Length == 0;

    public int RunCount(int col) => ColumnStart[col + 1] - ColumnStart[col];

    /// <summary>Enumerate a column's runs: callback(paletteId, yTop, yBottom).</summary>
    public Span<ulong> ColumnRuns(int col) =>
        Runs.AsSpan(ColumnStart[col], ColumnStart[col + 1] - ColumnStart[col]);

    public int FindOrAddPaletteEntry(int blockId, int color, byte flags)
    {
        for (int i = 0; i < Palette.Count; i++)
        {
            if (Palette[i].BlockId == blockId) return i;
        }
        Palette.Add(new LodPaletteEntry { BlockId = blockId, Color = color, Flags = flags });
        return Palette.Count - 1;
    }

    /// <summary>
    /// Replace one column's runs. Run values must already reference this section's
    /// palette. Returns true if the column content actually changed.
    /// </summary>
    public bool SetColumn(int col, ReadOnlySpan<ulong> newRuns)
    {
        Span<ulong> oldRuns = ColumnRuns(col);
        bool same = Captured[col] && oldRuns.Length == newRuns.Length;
        if (same)
        {
            for (int i = 0; i < newRuns.Length; i++)
            {
                if (oldRuns[i] != newRuns[i]) { same = false; break; }
            }
        }
        if (same) return false;

        if (!Captured[col])
        {
            Captured[col] = true;
            CapturedColumns++;
        }

        int oldLen = oldRuns.Length;
        int delta = newRuns.Length - oldLen;

        if (delta == 0)
        {
            newRuns.CopyTo(Runs.AsSpan(ColumnStart[col]));
            return true;
        }

        var next = new ulong[Runs.Length + delta];
        int start = ColumnStart[col];
        Runs.AsSpan(0, start).CopyTo(next);
        newRuns.CopyTo(next.AsSpan(start));
        Runs.AsSpan(start + oldLen).CopyTo(next.AsSpan(start + newRuns.Length));
        Runs = next;

        for (int c = col + 1; c < ColumnStart.Length; c++) ColumnStart[c] += delta;
        return true;
    }

    /// <summary>
    /// Replace many columns in one pass (one array rebuild total, not one per column) —
    /// the capture path applies a whole chunk column's worth of LOD columns at once.
    /// Entries in newRunsByCol may be null to leave that column untouched.
    /// Returns true if any column content changed.
    /// </summary>
    public bool ReplaceColumns(ulong[]?[] newRunsByCol)
    {
        int total = GridSize * GridSize;
        bool changed = false;
        int newLength = 0;

        for (int col = 0; col < total; col++)
        {
            ulong[]? repl = newRunsByCol[col];
            if (repl == null)
            {
                newLength += RunCount(col);
                continue;
            }

            Span<ulong> oldRuns = ColumnRuns(col);
            bool same = Captured[col] && oldRuns.Length == repl.Length;
            if (same)
            {
                for (int i = 0; i < repl.Length; i++)
                {
                    if (oldRuns[i] != repl[i]) { same = false; break; }
                }
            }

            if (same)
            {
                newRunsByCol[col] = null; // no-op, keep existing storage
                newLength += oldRuns.Length;
            }
            else
            {
                changed = true;
                if (!Captured[col])
                {
                    Captured[col] = true;
                    CapturedColumns++;
                }
                newLength += repl.Length;
            }
        }

        if (!changed) return false;

        var nextRuns = new ulong[newLength];
        var nextStart = new int[total + 1];
        int offset = 0;

        for (int col = 0; col < total; col++)
        {
            nextStart[col] = offset;
            ulong[]? repl = newRunsByCol[col];
            if (repl != null)
            {
                repl.CopyTo(nextRuns, offset);
                offset += repl.Length;
            }
            else
            {
                Span<ulong> keep = ColumnRuns(col);
                keep.CopyTo(nextRuns.AsSpan(offset));
                offset += keep.Length;
            }
        }
        nextStart[total] = offset;

        Runs = nextRuns;
        ColumnStart = nextStart;
        return true;
    }

    public static int ColumnIndex(int cx, int cz) => cz * GridSize + cx;
}
