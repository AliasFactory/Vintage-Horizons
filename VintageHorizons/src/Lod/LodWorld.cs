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

    /// <summary>Level 0 is selected out to twice this distance; each level doubles the band.</summary>
    public const double DetailDistance = 512;

    public readonly Dictionary<long, LodSection> Sections = new();

    /// <summary>Set by the coordinator when persistence is available: reload an evicted section from disk.</summary>
    public Func<long, LodSection?>? LoadFromStore;

    public int EvictedSectionsTotal { get; private set; }

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

        // A previously-evicted section must come back from disk, not start empty —
        // capture merges and mip propagation would otherwise clobber stored data.
        if (HasDataSet.Contains(key))
        {
            section = LoadFromStore?.Invoke(key);
            if (section != null)
            {
                Sections[key] = section;
                return section;
            }
        }

        Sections[key] = section = new LodSection();
        RegisterInTree(key);
        return section;
    }

    /// <summary>Get a section from RAM or disk without creating an empty one. For mesh scheduling.</summary>
    public bool TryGetOrLoad(long key, out LodSection section)
    {
        if (Sections.TryGetValue(key, out section!)) return true;
        if (!HasDataSet.Contains(key)) return false;

        LodSection? loaded = LoadFromStore?.Invoke(key);
        if (loaded == null) return false;

        Sections[key] = section = loaded;
        return true;
    }

    /// <summary>
    /// Drop cold sections from RAM (their rows stay on disk; HasDataSet keeps the
    /// quadtree semantics intact). Cold = the walk wants this area at least two
    /// levels coarser than this section, and nothing dirty references it.
    /// </summary>
    public int LastSweepChecked { get; private set; }
    public int LastSweepPinned { get; private set; }
    public int LastSweepCold { get; private set; }

    public void EvictColdSections(double camX, double camZ, int budget)
    {
        List<long>? evict = null;
        LastSweepChecked = 0;
        LastSweepPinned = 0;
        LastSweepCold = 0;

        foreach ((long key, LodSection _) in Sections)
        {
            LastSweepChecked++;
            int level = KeyLevel(key);
            if (level >= MaxLevel) continue;
            // Unsaved or unpropagated data pins a section; a pending mesh rebuild does
            // NOT — the scheduler demand-reloads from disk when its turn comes.
            if (SaveDirty.Contains(key) || MipDirty.Contains(key)) { LastSweepPinned++; continue; }

            int footprint = KeyFootprintBlocks(key);
            double minX = KeySx(key) * (double)footprint;
            double minZ = KeySz(key) * (double)footprint;
            double dx = Math.Max(0, Math.Max(minX - camX, camX - (minX + footprint)));
            double dz = Math.Max(0, Math.Max(minZ - camZ, camZ - (minZ + footprint)));
            double dist = Math.Sqrt(dx * dx + dz * dz);

            if (WantedLevelFor(dist) < level + 2) continue;

            LastSweepCold++;
            (evict ??= new List<long>()).Add(key);
            if (evict.Count >= budget) break;
        }

        if (evict == null) return;
        foreach (long key in evict)
        {
            Sections.Remove(key);
            EvictedSectionsTotal++;
        }
    }

    public static int WantedLevelFor(double distance) =>
        (int)Math.Clamp(Math.Log2(Math.Max(1.0, distance / DetailDistance)), 0, MaxLevel);

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
    /// Registers a stored section KEY from the persistent cache — no data attached.
    /// The quadtree skeleton (HasDataSet/TopLevelKeys) and pending-mip flags come
    /// from keys alone; section data demand-loads when first needed, so join time
    /// and RAM stay independent of how much was ever explored.
    /// </summary>
    public void InstallStoredKey(int level, int sx, int sz, bool applyToParent)
    {
        long key = SectionKey(level, sx, sz);
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
            if (!TryGetOrLoad(childKey, out LodSection child) || child.CapturedColumns == 0) continue;

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
