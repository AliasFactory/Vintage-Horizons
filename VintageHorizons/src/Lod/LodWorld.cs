using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// The in-memory section pyramid: all detail levels of LodSections, dirty tracking,
/// and child→parent mip propagation. All mutation happens on the main thread; the
/// worker thread only ever reads immutable snapshots (Runs/ColumnStart arrays are
/// replaced wholesale, never edited in place).
/// </summary>
public class LodWorld
{
    public const int MaxLevel = 6; // L6 sections span 4096 blocks (64-block columns at the horizon)

    public readonly Dictionary<long, LodSection> Sections = new();

    /// <summary>Sections whose mesh is stale.</summary>
    public readonly HashSet<long> RenderDirty = new();

    /// <summary>Sections whose DB row is stale.</summary>
    public readonly HashSet<long> SaveDirty = new();

    /// <summary>Sections whose parent still needs to absorb their content (persisted as ApplyToParent).</summary>
    public readonly HashSet<long> MipDirty = new();

    /// <summary>Every key (all levels) that holds data or has any descendant with data. Drives quadtree descent.</summary>
    public readonly HashSet<long> HasDataSet = new();

    /// <summary>Top-level (MaxLevel) ancestor keys — the quadtree roots.</summary>
    public readonly HashSet<long> TopLevelKeys = new();

    // ---- Key packing: level(4) | sz(30) | sx(30). VS world coords are non-negative. ----

    public static long SectionKey(int level, int sx, int sz) =>
        ((long)level << 60) | ((long)(sz & 0x3FFFFFFF) << 30) | (uint)(sx & 0x3FFFFFFF);

    public static int KeyLevel(long key) => (int)(key >>> 60);
    public static int KeySx(long key) => (int)(key & 0x3FFFFFFF);
    public static int KeySz(long key) => (int)((key >> 30) & 0x3FFFFFFF);

    public static long ParentKey(long key) =>
        SectionKey(KeyLevel(key) + 1, KeySx(key) >> 1, KeySz(key) >> 1);

    public static long ChildKey(long key, int qx, int qz) =>
        SectionKey(KeyLevel(key) - 1, (KeySx(key) << 1) + qx, (KeySz(key) << 1) + qz);

    public static long NeighborKey(long key, int dx, int dz) =>
        SectionKey(KeyLevel(key), KeySx(key) + dx, KeySz(key) + dz);

    /// <summary>Section footprint in blocks at this key's level.</summary>
    public static int KeyFootprintBlocks(long key) => LodSection.SectionBlocks << KeyLevel(key);

    public static int ColumnStepBlocks(int level) => LodSection.ColumnStepBlocks << level;

    public LodSection GetOrCreateSection(long key)
    {
        if (Sections.TryGetValue(key, out LodSection? section)) return section;

        Sections[key] = section = new LodSection();
        RegisterInTree(key);
        return section;
    }

    void RegisterInTree(long key)
    {
        while (true)
        {
            HasDataSet.Add(key);
            if (KeyLevel(key) == MaxLevel)
            {
                TopLevelKeys.Add(key);
                return;
            }
            key = ParentKey(key);
        }
    }

    public void MarkChanged(long key)
    {
        RenderDirty.Add(key);
        SaveDirty.Add(key);
        if (KeyLevel(key) < MaxLevel) MipDirty.Add(key);

        // Neighbor meshes cull their faces against our edge columns; conservatively
        // refresh all four (change locality tracking can come later).
        for (int d = 0; d < 4; d++)
        {
            long nk = NeighborKey(key, d == 0 ? -1 : d == 1 ? 1 : 0, d == 2 ? -1 : d == 3 ? 1 : 0);
            if (Sections.ContainsKey(nk)) RenderDirty.Add(nk);
        }
    }

    /// <summary>
    /// Adds a section loaded from the persistent cache. Deliberately NOT render-dirty:
    /// the quadtree walk demand-requests meshes for exactly the nodes it selects,
    /// so startup meshes what's visible instead of everything ever explored.
    /// </summary>
    public void InstallLoadedSection(int level, int sx, int sz, LodSection section, bool applyToParent)
    {
        long key = SectionKey(level, sx, sz);
        Sections[key] = section;
        RegisterInTree(key);
        if (applyToParent && level < MaxLevel) MipDirty.Add(key);
    }

    // ---- Mip propagation (child → parent), main thread, budgeted ----

    public void ProcessPropagation(int maxSections)
    {
        if (MipDirty.Count == 0) return;

        List<long>? batch = null;
        foreach (long key in MipDirty)
        {
            (batch ??= new List<long>()).Add(key);
            if (batch.Count >= maxSections) break;
        }
        if (batch == null) return;

        foreach (long childKey in batch)
        {
            MipDirty.Remove(childKey);
            SaveDirty.Add(childKey); // persist the cleared ApplyToParent flag

            if (KeyLevel(childKey) >= MaxLevel) continue;
            if (!Sections.TryGetValue(childKey, out LodSection? child) || child.CapturedColumns == 0) continue;

            long parentKey = ParentKey(childKey);
            LodSection parent = GetOrCreateSection(parentKey);

            if (LodMip.DownsampleIntoParent(child, parent, KeySx(childKey) & 1, KeySz(childKey) & 1))
            {
                MarkChanged(parentKey);
            }
        }
    }

    public string DescribeLevels()
    {
        var counts = new int[MaxLevel + 1];
        foreach (long key in Sections.Keys) counts[KeyLevel(key)]++;
        return string.Join(" ", counts.Select((c, i) => $"L{i}:{c}"));
    }

    public void Clear()
    {
        Sections.Clear();
        RenderDirty.Clear();
        SaveDirty.Clear();
        MipDirty.Clear();
        HasDataSet.Clear();
        TopLevelKeys.Clear();
    }
}
