using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// One entry in the palette of a section. It is a block with its LOD appearance.
///
/// The color is RGBA packed, with R in the low byte. The main thread finds it when the mod
/// first registers the palette entry, because Block.GetColor touches state that exists on
/// the client only.
/// </summary>
public struct LodPaletteEntry
{
    public int BlockId;

    /// <summary>The base color, with no tint. The shader applies the tint for the season
    /// and the climate.</summary>
    public int Color;

    public byte Flags;

    /// <summary>
    /// The live tint that applies. Read LodTintRegistry.
    ///
    /// The mod calculates this from the block, and it never stores it. Thus a cache that
    /// exists already gets the correct tint for each species, with no new capture. The map
    /// also stays correct when a game update moves a block to a different color map.
    /// </summary>
    public byte TintSlot;

    public const byte FlagWater = 1;
    // Bits 2 and 4 are free. They held tint classes, and TintSlot replaced those.

    /// <summary>
    /// This block is not terrain. Fire and a meta marker are examples. The capture removes
    /// it, thus it never becomes geometry.
    ///
    /// The capture does NOT skip thin ground cover. Read FlagThin.
    /// </summary>
    public const byte FlagSkip = 8;

    /// <summary>
    /// Thin decorative geometry, such as a flower, which vanilla draws as crossed quads.
    ///
    /// As a solid LOD cube it looks like a grey shape. The mod draws it so that a player can
    /// see through it. Then it looks like a plant, and the ground is visible behind it.
    /// </summary>
    public const byte FlagThin = 16;
}

/// <summary>
/// The leaf data model of M4. A section holds 64 x 64 vertical RLE columns, over a local
/// palette.
///
/// At level L, each column covers (ColumnStepBlocks &lt;&lt; L) blocks. Thus a section covers
/// SectionBlocks &lt;&lt; L.
///
/// A run is a packed ulong. The mod stores the runs from the top down, and the runs of one
/// column are next to each other. A prefix-offset table gives the position of each column.
/// This layout is compact, it serializes fast, and a mip over it is cheap. DESIGN.md section
/// 4 gives the concepts, which come from Distant Horizons and Voxy.
/// </summary>
public class LodSection
{
    public const int GridSize = 64;                 // columns per section edge
    public const int ColumnStepBlocks = 1;          // blocks per column at level 0 - full DH-parity resolution
    public const int SectionBlocks = GridSize * ColumnStepBlocks; // 64 at level 0

    /// <summary>The packing of a run: paletteId(16) | yTop(14) | yBottom(14). A run covers
    /// [yBottom, yTop).</summary>
    public static ulong PackRun(int paletteId, int yTop, int yBottom) =>
        ((ulong)(uint)paletteId << 28) | ((ulong)(uint)(yTop & 0x3FFF) << 14) | (uint)(yBottom & 0x3FFF);

    public static int RunPaletteId(ulong run) => (int)(run >> 28);
    public static int RunYTop(ulong run) => (int)((run >> 14) & 0x3FFF);
    public static int RunYBottom(ulong run) => (int)(run & 0x3FFF);

    /// <summary>The prefix offsets into Runs. Column c owns
    /// Runs[ColumnStart[c] .. ColumnStart[c+1]).</summary>
    public int[] ColumnStart = new int[GridSize * GridSize + 1];

    public ulong[] Runs = Array.Empty<ulong>();

    public readonly List<LodPaletteEntry> Palette = new();

    /// <summary>The columns that the mod captured one time or more. An empty column is not
    /// the same as a column that the mod did not capture.</summary>
    public readonly bool[] Captured = new bool[GridSize * GridSize];

    public int CapturedColumns;

    /// <summary>
    /// The mod sets this when a thread other than the main thread deserialized a section.
    /// The BlockIds of the palette are not found yet, because only the main thread can touch
    /// the block registry of the game.
    ///
    /// The mod finds the ids at install, and clears this field, before anything reads an
    /// id.
    /// </summary>
    public string[]? PendingPaletteCodes;

    public bool IsEmpty => Runs.Length == 0;

    public int RunCount(int col) => ColumnStart[col + 1] - ColumnStart[col];

    /// <summary>Give each run of a column to the callback, as (paletteId, yTop,
    /// yBottom).</summary>
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
    /// Remove each run whose palette entry has <paramref name="flag"/>, and build the run
    /// storage again.
    ///
    /// The mod does this after it loads a section. Thus it corrects the terrain that is in
    /// the cache already. A player does not explore that area again, and the mod does not
    /// empty the cache.
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
    /// Replace the runs of one column. Each run value must point at the palette of this
    /// section already. The result is true when the content of the column changed.
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
    /// Replace many columns in one pass. This builds the array one time, and not one time
    /// for each column. The capture path applies all the LOD columns of one chunk column
    /// together.
    ///
    /// An entry in newRunsByCol can be null, and then that column does not change. The
    /// result is true when the content of any column changed.
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
