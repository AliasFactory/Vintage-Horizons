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

    /// <summary>
    /// Which live tint applies (see LodTintRegistry). Derived from the block, never
    /// persisted: an existing cache gets correct per-species tints without re-capturing,
    /// and the mapping stays right if a game update moves a block to a different map.
    /// </summary>
    public byte TintSlot;

    public const byte FlagWater = 1;
    // Bits 2 and 4 are free: they held tint classes, now superseded by TintSlot.

    /// <summary>
    /// Not terrain at all (fire, meta markers): dropped at capture so it never becomes
    /// geometry. Thin ground cover is NOT skipped - see FlagThin.
    /// </summary>
    public const byte FlagSkip = 8;

    /// <summary>
    /// Thin decorative geometry (flowers) that vanilla draws as crossed quads. As a
    /// solid LOD cube it reads as a grey blob; drawn see-through it reads as a plant
    /// and the ground shows through it.
    /// </summary>
    public const byte FlagThin = 16;
}

/// <summary>
/// The M4 leaf data model: a section holds 64×64 vertical RLE columns over a local
/// palette. At level L each column covers (ColumnStepBlocks &lt;&lt; L) blocks, so a
/// section spans SectionBlocks &lt;&lt; L. Runs are packed ulongs, stored top-down,
/// contiguous per column, addressed by a prefix-offset table - compact, fast to
/// serialize, and cheap to mip (concepts per DESIGN.md §4, informed by DH/Voxy).
/// </summary>
public class LodSection
{
    public const int GridSize = 64;                 // columns per section edge
    public const int ColumnStepBlocks = 1;          // blocks per column at level 0 - full DH-parity resolution
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

    /// <summary>
    /// Set when a section was deserialized off the main thread: palette BlockIds are
    /// not resolved yet, because the game's block registry may only be touched from
    /// the main thread. Resolved and cleared on install, before anything reads ids.
    /// </summary>
    public string[]? PendingPaletteCodes;

    public bool IsEmpty => Runs.Length == 0;

    public int RunCount(int col) => ColumnStart[col + 1] - ColumnStart[col];

    /// <summary>Enumerate a column's runs: callback(paletteId, yTop, yBottom).</summary>
    public Span<ulong> ColumnRuns(int col) =>
        Runs.AsSpan(ColumnStart[col], ColumnStart[col + 1] - ColumnStart[col]);

    public int FindOrAddPaletteEntry(int blockId, int color, byte flags, byte tintSlot = 0)
    {
        for (int i = 0; i < Palette.Count; i++)
        {
            if (Palette[i].BlockId == blockId) return i;
        }
        Palette.Add(new LodPaletteEntry
        {
            BlockId = blockId,
            Color = color,
            Flags = flags,
            TintSlot = tintSlot,
        });
        return Palette.Count - 1;
    }

    /// <summary>
    /// Drop every run whose palette entry carries <paramref name="flag"/>, rebuilding the
    /// run storage. Applied after a section is loaded so terrain already in the cache is
    /// corrected in place - no re-exploration, no cache wipe.
    /// </summary>
    public void RemoveRunsWithFlag(byte flag)
    {
        bool anyFlagged = false;
        for (int i = 0; i < Palette.Count; i++)
        {
            if ((Palette[i].Flags & flag) != 0) { anyFlagged = true; break; }
        }
        if (!anyFlagged) return;

        int total = GridSize * GridSize;
        var nextRuns = new ulong[Runs.Length];
        var nextStart = new int[total + 1];
        int offset = 0;

        for (int col = 0; col < total; col++)
        {
            nextStart[col] = offset;
            int from = ColumnStart[col], to = ColumnStart[col + 1];
            for (int r = from; r < to; r++)
            {
                if ((Palette[RunPaletteId(Runs[r])].Flags & flag) != 0) continue;
                nextRuns[offset++] = Runs[r];
            }
        }
        nextStart[total] = offset;

        Array.Resize(ref nextRuns, offset);
        Runs = nextRuns;
        ColumnStart = nextStart;
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
    /// Replace many columns in one pass (one array rebuild total, not one per column) -
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
