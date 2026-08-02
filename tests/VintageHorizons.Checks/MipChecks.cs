namespace VintageHorizons.Checks;

/// <summary>
/// Downsampling from a child to its parent. Four child columns become one parent column,
/// through a slice sweep on the y boundaries. A slice stays only when enough columns cover
/// it.
///
/// The appearance of the full quadtree depends on this code. It makes each level above 0, and
/// nothing else does. Thus a defect appears as distant terrain that is slightly wrong, and
/// nobody can trace that back to one block.
/// </summary>
public static class MipChecks
{
    const int Half = LodSection.GridSize / 2;

    public static void Run(Check c)
    {
        QuadrantPlacement(c);
        MajorityOccupancy(c);
        RunMerging(c);
        PaletteRemap(c);
        NothingToDo(c);
    }

    /// <summary>A child section fills exactly one quadrant of its parent, and no other
    /// quadrant.</summary>
    static void QuadrantPlacement(Check c)
    {
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                LodSection child = Solid(0, 10, 0);
                var parent = new LodSection();

                c.True(LodMip.DownsampleIntoParent(child, parent, qx, qz),
                    $"quadrant ({qx},{qz}) reports the parent changed");

                // A full child section covers one half by one half of the parent
                // columns.
                c.Eq(Half * Half, parent.CapturedColumns, $"quadrant ({qx},{qz}) captures a quarter of the parent");

                int inside = LodSection.ColumnIndex(qx * Half, qz * Half);
                int alsoInside = LodSection.ColumnIndex(qx * Half + Half - 1, qz * Half + Half - 1);
                c.Eq(1, parent.ColumnRuns(inside).Length, $"quadrant ({qx},{qz}) fills its near corner");
                c.Eq(1, parent.ColumnRuns(alsoInside).Length, $"quadrant ({qx},{qz}) fills its far corner");

                // The other three quadrants must not change. Without that rule, the
                // siblings overwrite each other, and only the last one stays.
                int outsideX = qx == 0 ? Half : 0;
                int outsideZ = qz == 0 ? Half : 0;
                c.False(parent.Captured[LodSection.ColumnIndex(outsideX, qz * Half)],
                    $"quadrant ({qx},{qz}) leaves the x-adjacent quadrant alone");
                c.False(parent.Captured[LodSection.ColumnIndex(qx * Half, outsideZ)],
                    $"quadrant ({qx},{qz}) leaves the z-adjacent quadrant alone");
            }
        }

        // All four siblings go into one parent. Together they cover it exactly one time.
        var shared = new LodSection();
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++) LodMip.DownsampleIntoParent(Solid(0, 10, 0), shared, qx, qz);
        }
        c.Eq(Fixtures.Total, shared.CapturedColumns, "four children cover the whole parent");
    }

    /// <summary>
    /// A slice stays when two or more of the four child columns cover it. Thus one thin
    /// spire does not become a pillar at the full width, at the coarser level.
    ///
    /// When the mod captured one column only, the threshold becomes one. Without that, the
    /// frontier of the explored terrain loses its edge columns, and no message occurs.
    /// </summary>
    static void MajorityOccupancy(Check c)
    {
        // One column reaches y=20. The other three stop at y=10.
        var child = new LodSection();
        child.FindOrAddPaletteEntry(blockId: 1, color: 0x00445566, flags: 0);
        child.SetColumn(LodSection.ColumnIndex(0, 0), new[] { LodSection.PackRun(0, 20, 0) });
        child.SetColumn(LodSection.ColumnIndex(1, 0), new[] { LodSection.PackRun(0, 10, 0) });
        child.SetColumn(LodSection.ColumnIndex(0, 1), new[] { LodSection.PackRun(0, 10, 0) });
        child.SetColumn(LodSection.ColumnIndex(1, 1), new[] { LodSection.PackRun(0, 10, 0) });

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);

        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, merged.Length, "a minority spire does not survive as its own run");
        c.Eq(10, LodSection.RunYTop(merged[0]), "the parent takes the height the majority agreed on");
        c.Eq(0, LodSection.RunYBottom(merged[0]), "the merged run keeps the shared floor");

        // One captured column has no majority against it.
        var lonely = new LodSection();
        lonely.FindOrAddPaletteEntry(blockId: 1, color: 0x00445566, flags: 0);
        lonely.SetColumn(LodSection.ColumnIndex(0, 0), new[] { LodSection.PackRun(0, 20, 0) });

        var lonelyParent = new LodSection();
        LodMip.DownsampleIntoParent(lonely, lonelyParent, 0, 0);

        ulong[] survived = lonelyParent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, survived.Length, "a lone captured column still produces a run");
        c.Eq(20, LodSection.RunYTop(survived[0]), "the lone column keeps its full height");
    }

    /// <summary>
    /// The sweep walks one boundary at a time. Thus a column with one material arrives as
    /// several slices that touch each other, and it must leave as one run.
    ///
    /// Without the merge, each parent column holds one run for each distinct y in its
    /// children. Then the run arrays grow with the depth, and they do not become smaller. A
    /// smaller array is the purpose of the pyramid.
    /// </summary>
    static void RunMerging(Check c)
    {
        var child = new LodSection();
        child.FindOrAddPaletteEntry(blockId: 1, color: 0x00112233, flags: 0);
        child.FindOrAddPaletteEntry(blockId: 2, color: 0x00445566, flags: 0);

        // Two runs of the SAME block, one above the other, with a division at y=10.
        ulong[] sameBlock = { LodSection.PackRun(0, 20, 10), LodSection.PackRun(0, 10, 0) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) child.SetColumn(LodSection.ColumnIndex(dx, dz), sameBlock);
        }

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);

        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, merged.Length, "abutting slices of one block fuse into a single run");
        c.Eq(20, LodSection.RunYTop(merged[0]), "the fused run keeps the top");
        c.Eq(0, LodSection.RunYBottom(merged[0]), "the fused run keeps the bottom");

        // Two different blocks must NOT join. Without that rule, stone takes the soil above
        // it.
        var layered = new LodSection();
        layered.FindOrAddPaletteEntry(blockId: 1, color: 0x00112233, flags: 0);
        layered.FindOrAddPaletteEntry(blockId: 2, color: 0x00445566, flags: 0);
        ulong[] twoBlocks = { LodSection.PackRun(1, 20, 10), LodSection.PackRun(0, 10, 0) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) layered.SetColumn(LodSection.ColumnIndex(dx, dz), twoBlocks);
        }

        var layeredParent = new LodSection();
        LodMip.DownsampleIntoParent(layered, layeredParent, 0, 0);

        ulong[] kept = layeredParent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(2, kept.Length, "runs of different blocks stay separate");
        c.Eq(20, LodSection.RunYTop(kept[0]), "the upper layer keeps its top");
        c.Eq(10, LodSection.RunYBottom(kept[0]), "the layers meet where they should");
        c.Eq(0, LodSection.RunYBottom(kept[1]), "the lower layer keeps its floor");
    }

    /// <summary>
    /// The parent and the child hold separate palettes. Thus the mod must translate an id,
    /// and it must not copy the id. A direct copy reads each run as the block at that index
    /// in the parent, and it gives no message.
    /// </summary>
    static void PaletteRemap(Check c)
    {
        var child = new LodSection();
        // This is deliberately not index 0. A map that changes nothing hides the defect.
        child.FindOrAddPaletteEntry(blockId: 100, color: 0x00AAAAAA, flags: 0);
        child.FindOrAddPaletteEntry(blockId: 200, color: 0x00BBCCDD,
            flags: LodPaletteEntry.FlagWater, tintSlot: 7);

        ulong[] runs = { LodSection.PackRun(1, 10, 0) };
        for (int dz = 0; dz < 2; dz++)
        {
            for (int dx = 0; dx < 2; dx++) child.SetColumn(LodSection.ColumnIndex(dx, dz), runs);
        }

        // Give the parent an unrelated entry first. Thus id 1 of the child cannot be the
        // same by chance.
        var parent = new LodSection();
        parent.FindOrAddPaletteEntry(blockId: 999, color: 0x00000001, flags: 0);

        LodMip.DownsampleIntoParent(child, parent, 0, 0);

        ulong[] merged = parent.ColumnRuns(LodSection.ColumnIndex(0, 0)).ToArray();
        c.Eq(1, merged.Length, "the remapped column has one run");

        int pid = LodSection.RunPaletteId(merged[0]);
        c.True(pid < parent.Palette.Count, "the remapped id is inside the parent palette");
        c.Eq(200, parent.Palette[pid].BlockId, "the run still names the child's block");
        c.Eq(0x00BBCCDD, parent.Palette[pid].Color, "colour survives the remap");
        c.Eq(LodPaletteEntry.FlagWater, parent.Palette[pid].Flags, "flags survive the remap");
        c.Eq((byte)7, parent.Palette[pid].TintSlot, "tint slot survives the remap");
        c.Eq(999, parent.Palette[0].BlockId, "the parent's existing palette entry is undisturbed");
    }

    static void NothingToDo(Check c)
    {
        var parent = new LodSection();
        c.False(LodMip.DownsampleIntoParent(new LodSection(), parent, 0, 0),
            "an empty child leaves the parent unchanged");
        c.Eq(0, parent.CapturedColumns, "an empty child captures nothing");

        // An identical downsample, run again, must change nothing. Without that, each mip
        // pass marks the parent dirty. Then it queues a save and a new mesh, forever.
        LodSection child = Solid(0, 10, 0);
        var target = new LodSection();
        c.True(LodMip.DownsampleIntoParent(child, target, 0, 0), "the first downsample changes the parent");
        c.False(LodMip.DownsampleIntoParent(child, target, 0, 0), "an identical re-run changes nothing");
    }

    /// <summary>A child section that the mod captured fully. Each column holds one run of
    /// palette id 0.</summary>
    static LodSection Solid(int paletteId, int yTop, int yBottom)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: paletteId + 1, color: 0x00708090, flags: 0);
        ulong[] run = { LodSection.PackRun(paletteId, yTop, yBottom) };
        for (int col = 0; col < Fixtures.Total; col++) s.SetColumn(col, run);
        return s;
    }
}
