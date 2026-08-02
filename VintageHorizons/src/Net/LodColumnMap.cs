namespace VintageHorizons.Net;

/// <summary>
/// What a bulk pass over the world can safely do at one position.
/// </summary>
public enum EnumColumnAction
{
    /// <summary>Not in the savegame. Safe to generate transiently from the seed.</summary>
    Peek,

    /// <summary>In the savegame, with a complete neighbourhood. Safe to load as it is.</summary>
    Load,

    /// <summary>In the savegame, but a neighbour is missing. Touch nothing.</summary>
    SkipFrontier,
}

/// <summary>
/// Records which chunk columns the savegame holds, and decides what a bulk pass can
/// safely do at each position. The class has no game types on purpose: this rule keeps
/// the "generates nothing" promise, so it must be testable without a game process.
/// Extracted from <see cref="LodSavegameSweep"/>, which was the only holder before
/// generation needed the same map.
/// </summary>
public class LodColumnMap
{
    /// <summary>
    /// How far the neighbourhood must be intact before a column is safe to load.
    ///
    /// Four, from measurement rather than reasoning. The worldgen pass dependency
    /// reaches much further than the intuitive one ring. We swept one world at each
    /// setting and counted the chunk columns the savegame gained:
    ///
    ///   no check   1460 generated
    ///   radius 1    714 generated
    ///   radius 2    509 generated
    ///   radius 4      0 generated
    ///
    /// 3 was not tested, so 4 can be one wider than strictly necessary. Wide is the
    /// safe direction. Too narrow silently breaks the promise. Too wide only leaves a
    /// slightly thicker border of real terrain uncaptured.
    /// </summary>
    public const int SafeNeighbourhood = 4;

    /// <summary>Positions known to hold generated terrain, packed as cz&lt;&lt;32 | cx.</summary>
    readonly HashSet<long> exists = new();

    public static long Key(int cx, int cz) => ((long)cz << 32) | (uint)cx;
    public static int KeyCx(long key) => (int)(key & 0xFFFFFFFF);
    public static int KeyCz(long key) => (int)(key >> 32);

    /// <summary>
    /// Index to offset on a square spiral centred on 0,0. Walks ring by ring, so any
    /// prefix of the sequence is a filled square around the centre. That is the order
    /// coverage is wanted in when a run is interrupted: a partial spiral is a usable
    /// disc, and a partial raster is a band across the map.
    /// </summary>
    public static (int X, int Z) SpiralAt(int i)
    {
        if (i == 0) return (0, 0);

        // Which ring: the k-th ring ends at index (2k+1)^2 - 1.
        int ring = (int)Math.Ceiling((Math.Sqrt(i + 1) - 1) / 2);
        int ringStart = (2 * ring - 1) * (2 * ring - 1);
        int offset = i - ringStart;
        int side = 2 * ring;

        return (offset / side) switch
        {
            0 => (ring, -ring + 1 + offset % side),
            1 => (ring - 1 - offset % side, ring),
            2 => (-ring, ring - 1 - offset % side),
            _ => (-ring + 1 + offset % side, -ring),
        };
    }

    /// <summary>Positions recorded as holding terrain.</summary>
    public int Count => exists.Count;

    public bool Add(int cx, int cz) => exists.Add(Key(cx, cz));

    public bool Contains(int cx, int cz) => exists.Contains(Key(cx, cz));

    /// <summary>
    /// True when every position within <see cref="SafeNeighbourhood"/> of this column
    /// is on disk. Loading such a column cannot make the engine generate anything.
    /// </summary>
    public bool NeighbourhoodComplete(int cx, int cz)
    {
        for (int dz = -SafeNeighbourhood; dz <= SafeNeighbourhood; dz++)
        {
            for (int dx = -SafeNeighbourhood; dx <= SafeNeighbourhood; dx++)
            {
                if (!exists.Contains(Key(cx + dx, cz + dz))) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The whole safety decision, in one place. The asymmetry is the point. A column
    /// that does not exist is always safe to PEEK, however bare its surroundings: a
    /// peek reads the seed and touches neither the savegame nor the loaded chunk list.
    /// A column that exists is safe to LOAD only with an intact neighbourhood, and it
    /// must never be peeked - a peek regenerates from the seed, so it would describe
    /// the terrain as it was before anyone built on it.
    /// </summary>
    public EnumColumnAction Classify(int cx, int cz) =>
        !Contains(cx, cz)               ? EnumColumnAction.Peek
        : NeighbourhoodComplete(cx, cz) ? EnumColumnAction.Load
        :                                 EnumColumnAction.SkipFrontier;

    /// <summary>
    /// Up to <paramref name="max"/> positions inside the square that are NOT in the
    /// map, sampled evenly across the spiral. These are the positions a bulk pass
    /// promises to leave ungenerated. The caller re-probes them afterward, because the
    /// promise gets measured, not trusted - a worldgen mod can do anything during a
    /// load or a peek, and this is the only detector that runs on every server.
    /// </summary>
    public List<long> AbsentSample(int centreCx, int centreCz, int radiusChunks, int max)
    {
        int total = (2 * radiusChunks + 1) * (2 * radiusChunks + 1);

        int absent = 0;
        for (int i = 0; i < total; i++)
        {
            (int dx, int dz) = LodColumnMap.SpiralAt(i);
            if (!Contains(centreCx + dx, centreCz + dz)) absent++;
        }

        var sample = new List<long>(Math.Min(max, absent));
        if (absent == 0 || max <= 0) return sample;

        int stride = Math.Max(1, absent / max);
        int seen = 0;
        for (int i = 0; i < total && sample.Count < max; i++)
        {
            (int dx, int dz) = LodColumnMap.SpiralAt(i);
            int cx = centreCx + dx, cz = centreCz + dz;
            if (Contains(cx, cz)) continue;
            if (seen++ % stride == 0) sample.Add(Key(cx, cz));
        }
        return sample;
    }
}
