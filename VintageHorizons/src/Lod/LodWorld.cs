using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// The section pyramid in memory. It holds the LodSection objects at each detail level, the
/// dirty tracking, and the mip propagation from a child to its parent.
///
/// Each change occurs on the main thread. The worker thread reads snapshots only, and those
/// snapshots do not change. A write replaces a full Runs array or ColumnStart array. It never
/// edits one in place.
/// </summary>
public class LodWorld
{
    public const int MaxLevel = 6; // An L6 section covers 4096 blocks, with 64-block columns at the horizon.

    /// <summary>
    /// The walk selects level 0 out to two times this distance. Each level then doubles
    /// the width of its band.
    ///
    /// A larger value moves each detail level outward. This is the largest single control
    /// over the quality that a player sees, and over the cost. The band of level 0 grows
    /// with the square of this value. Thus a value two times larger asks for approximately
    /// four times as many leaf meshes.
    ///
    /// A player can change this value during play, with `.vhdetail`. The selection walk then
    /// selects different levels in the next frame, and it meshes what it wants now.
    /// </summary>
    public static double DetailDistance = 512;

    public const double MinDetailDistance = 256;
    public const double MaxDetailDistance = 4096;

    public readonly Dictionary<long, LodSection> Sections = new();

    /// <summary>The coordinator sets this when persistence is available. It loads a section
    /// from the disk after an eviction.</summary>
    public Func<long, LodSection?>? LoadFromStore;

    public int EvictedSectionsTotal { get; private set; }

    /// <summary>The sections whose mesh is not current.</summary>
    public readonly HashSet<long> RenderDirty = new();

    /// <summary>The sections whose database row is not current.</summary>
    public readonly HashSet<long> SaveDirty = new();

    /// <summary>The sections whose parent must still take in their content. The database
    /// stores this as ApplyToParent.</summary>
    public readonly HashSet<long> MipDirty = new();

    /// <summary>Each key, at each level, that holds data or that has a descendant with data.
    /// This set drives the descent of the quadtree.</summary>
    public readonly HashSet<long> HasDataSet = new();

    /// <summary>The ancestor keys at the top level, which is MaxLevel. These are the roots
    /// of the quadtree.</summary>
    public readonly HashSet<long> TopLevelKeys = new();

    // ---- Key packing: level(4) | sz(30) | sx(30). Vintage Story world coordinates are
    // never negative. ----

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

    /// <summary>The area of a section in blocks, at the level of this key.</summary>
    public static int KeyFootprintBlocks(long key) => LodSection.SectionBlocks << KeyLevel(key);

    /// <summary>
    /// The square of the distance from a point to the nearest edge of the area of a section.
    ///
    /// The distance is to the nearest edge, and not to the center. An L6 section covers 4096
    /// blocks. Thus a center distance gives a large value for a section that the viewer is
    /// inside.
    /// </summary>
    public static double NearestDistanceSqTo(long key, double x, double z)
    {
        int footprint = KeyFootprintBlocks(key);
        double minX = KeySx(key) * (double)footprint;
        double minZ = KeySz(key) * (double)footprint;
        double dx = Math.Max(0, Math.Max(minX - x, x - (minX + footprint)));
        double dz = Math.Max(0, Math.Max(minZ - z, z - (minZ + footprint)));
        return dx * dx + dz * dz;
    }

    public static int ColumnStepBlocks(int level) => LodSection.ColumnStepBlocks << level;

    public LodSection GetOrCreateSection(long key)
    {
        if (Sections.TryGetValue(key, out LodSection? section)) return section;

        // A section that the mod evicted before must return from the disk. It must not
        // start empty. An empty start lets a capture merge or a mip propagation overwrite
        // the stored data.
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
        LoadFailed.Remove(key); // The key has data again. An earlier miss must not stop a reload.
        RegisterInTree(key);
        return section;
    }

    /// <summary>Get a section from RAM or from the disk, and do not make an empty one. The
    /// mesh scheduler uses this.</summary>
    public bool TryGetOrLoad(long key, out LodSection section)
    {
        if (Sections.TryGetValue(key, out section!)) return true;
        if (!HasDataSet.Contains(key)) return false;

        LodSection? loaded = LoadFromStore?.Invoke(key);
        if (loaded == null) return false;

        Sections[key] = section = loaded;
        return true;
    }

    /// <summary>Ask the storage thread to load an evicted section again. This is null when
    /// no storage thread exists.</summary>
    public Action<long>? RequestAsyncLoad;

    /// <summary>The keys that have a load in progress. Thus the render path stops asking for
    /// them again.</summary>
    public readonly HashSet<long> LoadsInFlight = new();

    /// <summary>
    /// The keys whose load returned nothing. The row is absent, or the mod deleted it
    /// because the mod cannot read it.
    ///
    /// Without this set, the selection walk asks for those keys again in each frame, forever,
    /// because the section never becomes resident.
    /// </summary>
    public readonly HashSet<long> LoadFailed = new();

    /// <summary>
    /// The variant for the render path, which does not block. It returns false and starts a
    /// load in the background. Thus a decompress does not delay the frame.
    ///
    /// The selection walk asks for the mesh again in a later frame. Thus the mod uses the
    /// section after it arrives.
    /// </summary>
    public bool TryGetForRender(long key, out LodSection section)
    {
        if (Sections.TryGetValue(key, out section!)) return true;
        if (!HasDataSet.Contains(key) || LoadFailed.Contains(key)) return false;

        if (RequestAsyncLoad == null)
        {
            // There is no storage thread, because this session has no persistence. Do the
            // load in this call instead.
            return TryGetOrLoad(key, out section);
        }

        if (LoadsInFlight.Add(key)) RequestAsyncLoad(key);
        return false;
    }

    /// <summary>
    /// Install a section that a background load completed.
    ///
    /// A section can become resident while the read is in progress, because a capture made it
    /// or loaded it in the same call. That section is newer. Thus the mod discards the copy
    /// that arrives.
    /// </summary>
    public void InstallLoaded(long key, LodSection? section)
    {
        LoadsInFlight.Remove(key);
        if (section == null)
        {
            LoadFailed.Add(key);
            return;
        }
        if (Sections.ContainsKey(key)) return;

        Sections[key] = section;

        // Do not mark this section as render-dirty. The render path asks for a load, and
        // the mip propagation asks for a load also. The selection walk asks for a mesh
        // itself in the next frame, if it still wants one here. A mark on each arrival
        // meshes the sections that only the propagation asked for.
    }

    /// <summary>
    /// Remove cold sections from RAM. Their rows stay on the disk, and HasDataSet keeps the
    /// meaning of the quadtree correct.
    ///
    /// A section is cold when two conditions are true. The walk wants this area at least two
    /// levels coarser than this section. And no dirty set holds this key.
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
            // Data that the mod did not save, or did not propagate, holds a section in
            // RAM. A mesh rebuild that waits does NOT hold it. The scheduler loads the
            // section from the disk again when its turn arrives.
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

        // The mesh of a neighbour culls its faces against the edge columns of this
        // section. Refresh all four neighbours. A record of where the change occurred can
        // come later.
        for (int d = 0; d < 4; d++)
        {
            long nk = NeighborKey(key, d == 0 ? -1 : d == 1 ? 1 : 0, d == 2 ? -1 : d == 3 ? 1 : 0);
            if (Sections.ContainsKey(nk)) RenderDirty.Add(nk);
        }
    }

    /// <summary>
    /// Registers the KEY of a stored section from the cache. No data comes with it.
    ///
    /// The structure of the quadtree, which is HasDataSet and TopLevelKeys, comes from the
    /// keys alone. The pending mip flags come from the keys also. The mod loads the data of a
    /// section when something first needs it.
    ///
    /// Thus the join time and the RAM use do not increase with the quantity of terrain that
    /// anyone explored.
    /// </summary>
    public void InstallStoredKey(int level, int sx, int sz, bool applyToParent)
    {
        long key = SectionKey(level, sx, sz);
        RegisterInTree(key);
        if (applyToParent && level < MaxLevel) MipDirty.Add(key);
    }

    // ---- Mip propagation from a child to its parent. This runs on the main thread, with a
    // budget. ----

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
            // Both sections must be in RAM before the mod clears the flag. If the mod
            // clears the flag while a section is still on the disk, the propagation is
            // lost. Thus a section that waits for a load stays pending, and the mod tries
            // again on a later tick.
            long parentKey = ParentKey(childKey);
            if (!EnsureResident(childKey)) continue;
            if (!EnsureResident(parentKey)) continue;

            MipDirty.Remove(childKey);
            SaveDirty.Add(childKey); // Store the ApplyToParent flag in its cleared state.

            if (!Sections.TryGetValue(childKey, out LodSection? child) || child.CapturedColumns == 0) continue;

            LodSection parent = GetOrCreateSection(parentKey);

            if (LodMip.DownsampleIntoParent(child, parent, KeySx(childKey) & 1, KeySz(childKey) & 1))
            {
                MarkChanged(parentKey);
            }
        }
    }

    /// <summary>
    /// True when the section is in RAM, or when there is nothing to load for it. Then the
    /// caller can continue.
    ///
    /// False means that a background load started. Then the caller must leave its pending
    /// work as it is, and try again later.
    ///
    /// This is how the mip propagation does not block the frame on a decompress. It also
    /// never makes an empty section. Such a section hides a stored row, and then it
    /// overwrites that row.
    ///
    /// The rule is the rule of TryGetForRender, with one difference. Here a key with nothing
    /// to load means "continue", and not "no mesh".
    /// </summary>
    public bool EnsureResident(long key) =>
        TryGetForRender(key, out _) || !LoadsInFlight.Contains(key);

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
        LoadsInFlight.Clear();
        LoadFailed.Clear();
    }
}
