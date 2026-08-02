namespace VintageHorizons.Checks;

/// <summary>
/// The packing of a section key, and the quadtree coordinate arithmetic above it.
///
/// These few functions hold each fact that the descent of the renderer, the storage rows and
/// the network manifest agree about. Thus a change here that gives no message damages all
/// three together.
/// </summary>
public static class KeyMathChecks
{
    public static void Run(Check c)
    {
        Packing(c);
        Family(c);
        Footprint(c);
        Distance(c);
    }

    static void Packing(Check c)
    {
        // level(4) | sz(30) | sx(30)
        foreach (int level in new[] { 0, 1, 3, LodWorld.MaxLevel })
        {
            foreach ((int sx, int sz) in new[] { (0, 0), (1, 0), (0, 1), (12345, 67890), (0x3FFFFFFF, 0x3FFFFFFF) })
            {
                long key = LodWorld.SectionKey(level, sx, sz);
                c.Eq(level, LodWorld.KeyLevel(key), $"level round-trips at L{level} {sx},{sz}");
                c.Eq(sx, LodWorld.KeySx(key), $"sx round-trips at L{level} {sx},{sz}");
                c.Eq(sz, LodWorld.KeySz(key), $"sz round-trips at L{level} {sx},{sz}");
            }
        }

        // A distinct value matters more than the exact layout. The full scheme is a
        // Dictionary key. Thus one collision joins two regions of the world, and it gives no
        // message.
        var seen = new HashSet<long>();
        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            for (int sx = 0; sx < 12; sx++)
            {
                for (int sz = 0; sz < 12; sz++) seen.Add(LodWorld.SectionKey(level, sx, sz));
            }
        }
        c.Eq((LodWorld.MaxLevel + 1) * 144, seen.Count, "no key collisions across levels and a 12x12 patch");

        // KeyLevel uses a shift without a sign. Thus the top bit of a maximum key must not
        // extend the sign into a negative level.
        long extreme = LodWorld.SectionKey(LodWorld.MaxLevel, 0x3FFFFFFF, 0x3FFFFFFF);
        c.Eq(LodWorld.MaxLevel, LodWorld.KeyLevel(extreme), "level survives a maximal sx/sz");
    }

    static void Family(Check c)
    {
        long parent = LodWorld.SectionKey(3, 10, 20);

        // Each child names its parent, and the four children are all different.
        var children = new HashSet<long>();
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                long child = LodWorld.ChildKey(parent, qx, qz);
                children.Add(child);
                c.Eq(2, LodWorld.KeyLevel(child), $"child ({qx},{qz}) is one level finer");
                c.Eq(parent, LodWorld.ParentKey(child), $"child ({qx},{qz}) round-trips to its parent");
            }
        }
        c.Eq(4, children.Count, "the four children are distinct");

        // The area of a parent is exactly the area of its children. That is what lets the
        // renderer stop the descent after all four have cover.
        c.Eq(LodWorld.KeyFootprintBlocks(parent),
            LodWorld.KeyFootprintBlocks(LodWorld.ChildKey(parent, 0, 0)) * 2,
            "a parent spans twice a child's edge");

        // An odd coordinate must go down to the parent. It must not round.
        c.Eq(LodWorld.SectionKey(1, 5, 5), LodWorld.ParentKey(LodWorld.SectionKey(0, 11, 11)),
            "odd child coordinates floor into the parent");

        long origin = LodWorld.SectionKey(0, 4, 4);
        c.Eq(LodWorld.SectionKey(0, 3, 4), LodWorld.NeighborKey(origin, -1, 0), "west neighbour");
        c.Eq(LodWorld.SectionKey(0, 5, 4), LodWorld.NeighborKey(origin, 1, 0), "east neighbour");
        c.Eq(LodWorld.SectionKey(0, 4, 3), LodWorld.NeighborKey(origin, 0, -1), "north neighbour");
        c.Eq(LodWorld.SectionKey(0, 4, 5), LodWorld.NeighborKey(origin, 0, 1), "south neighbour");

        // A step west from sx=0 goes to the top of the 30-bit field. It does not become
        // negative.
        //
        // That is safe only because Vintage Story world coordinates are never negative. Thus
        // the wrapped key names a section that cannot exist, and each lookup misses it.
        //
        // CAUTION: If world coordinates ever become negative, this becomes a wrong-neighbour
        // defect, and not a miss.
        long wrapped = LodWorld.NeighborKey(LodWorld.SectionKey(0, 0, 0), -1, 0);
        c.Eq(0x3FFFFFFF, LodWorld.KeySx(wrapped), "stepping west of the origin wraps rather than going negative");
        c.Eq(0, LodWorld.KeyLevel(wrapped), "the wrap does not corrupt the level field");
    }

    static void Footprint(Check c)
    {
        c.Eq(LodSection.SectionBlocks, LodWorld.KeyFootprintBlocks(LodWorld.SectionKey(0, 0, 0)),
            "an L0 section spans SectionBlocks");
        c.Eq(4096, LodWorld.KeyFootprintBlocks(LodWorld.SectionKey(6, 0, 0)),
            "an L6 section spans 4096 blocks");

        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            c.Eq(LodSection.SectionBlocks << level, LodWorld.KeyFootprintBlocks(LodWorld.SectionKey(level, 7, 7)),
                $"L{level} footprint doubles per level");
            c.Eq(LodSection.ColumnStepBlocks << level, LodWorld.ColumnStepBlocks(level),
                $"L{level} column step doubles per level");
        }
    }

    static void Distance(Check c)
    {
        long key = LodWorld.SectionKey(0, 2, 3); // occupies [128,192) x [192,256) at 64-block sections
        int size = LodSection.SectionBlocks;
        double minX = 2 * size, minZ = 3 * size;

        // This is the reason for the nearest edge, and not the center. A viewer inside a
        // section must get a distance of zero for it. An L6 section covers 4096 blocks. Thus
        // a center distance gives two kilometres, and the walk refuses to descend.
        c.Eq(0.0, LodWorld.NearestDistanceSqTo(key, minX + 1, minZ + 1), "inside the footprint is distance zero");
        c.Eq(0.0, LodWorld.NearestDistanceSqTo(key, minX, minZ), "the min corner is distance zero");
        c.Eq(0.0, LodWorld.NearestDistanceSqTo(key, minX + size - 0.001, minZ + size - 0.001),
            "just inside the max corner is distance zero");

        long big = LodWorld.SectionKey(6, 0, 0);
        c.Eq(0.0, LodWorld.NearestDistanceSqTo(big, 2000, 2000), "inside a 4096-block L6 section is distance zero");

        // The offset is along an axis. Thus only that axis adds to the distance.
        c.Eq(100.0, LodWorld.NearestDistanceSqTo(key, minX - 10, minZ + 1), "10 blocks west is 100");
        c.Eq(100.0, LodWorld.NearestDistanceSqTo(key, minX + 1, minZ - 10), "10 blocks north is 100");
        c.Eq(100.0, LodWorld.NearestDistanceSqTo(key, minX + size + 10, minZ + 1), "10 blocks east is 100");

        // The offset is diagonal. Thus both axes add to the distance.
        c.Eq(200.0, LodWorld.NearestDistanceSqTo(key, minX - 10, minZ - 10), "diagonal corner sums both axes");

        c.True(LodWorld.NearestDistanceSqTo(key, 0, 0) > 0, "the world origin is outside this section");
    }
}
