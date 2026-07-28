namespace VintageHorizons.Checks;

/// <summary>
/// Section snapshot to vertex data. Two things are worth pinning here: the greedy merge,
/// which is the difference between five quads and four thousand for the same terrain, and
/// the coverage rules, which are deliberately asymmetric and were each arrived at by
/// finding the artefact the symmetric version produced.
/// </summary>
public static class MesherChecks
{
    const int Gs = LodSection.GridSize;

    public static void Run(Check c)
    {
        Empty(c);
        GreedyMerge(c);
        LevelScaling(c);
        AlphaBands(c);
        WaterIsASeparatePass(c);
        ThinMats(c);
        CoverageRules(c);
        Frontier(c);
    }

    static void Empty(Check c)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(new LodSection()));
        c.Eq(0, mesh.VertexCount, "an empty section produces no vertices");
        c.Eq(0, mesh.IndexCount, "an empty section produces no indices");
        c.Eq(null, mesh.WaterXyz, "an empty section produces no water pass");
    }

    /// <summary>
    /// The reason the mesher exists in this shape. A flat plain is 4096 columns with
    /// identical tops; naively that is 4096 quads for the surface plus a wall per column
    /// edge. Merged it is one rectangle and four frontier ribbons.
    /// </summary>
    static void GreedyMerge(Check c)
    {
        LodSection flat = Solid(yTop: 10, yBottom: 0);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(flat));

        // 1 top rectangle + 4 frontier walls. No bottom faces: yBottom is 0, and the mesher
        // skips floors at or below y=1 because nothing can ever see under the world.
        c.Eq(5, Quads(mesh.VertexCount), "a flat 64x64 plain collapses to five quads");
        c.Eq(20, mesh.VertexCount, "five quads is twenty vertices");
        c.Eq(30, mesh.IndexCount, "five quads is thirty indices (two triangles each)");

        // The merged top must actually span the section, not just claim to.
        float[] xs = Every3rd(mesh.Xyz, 0);
        float[] zs = Every3rd(mesh.Xyz, 2);
        c.Eq(0f, xs.Min(), "the merged surface starts at the section's near edge");
        c.Eq((float)Gs, xs.Max(), "the merged surface reaches the section's far edge");
        c.Eq(0f, zs.Min(), "the merged surface starts at the near z edge");
        c.Eq((float)Gs, zs.Max(), "the merged surface reaches the far z edge");

        // A hole must break the merge, or the rectangle would pave over missing terrain.
        LodSection holed = Solid(yTop: 10, yBottom: 0);
        holed.SetColumn(LodSection.ColumnIndex(32, 32), Array.Empty<ulong>());
        MeshResult holedMesh = LodMesher.BuildMesh(Fixtures.Job(holed));
        c.True(Quads(holedMesh.VertexCount) > 5, "a hole in the plain prevents a single-rectangle merge");

        // Differing heights cannot merge into one plane either.
        LodSection stepped = Solid(yTop: 10, yBottom: 0);
        stepped.SetColumn(LodSection.ColumnIndex(32, 32), new[] { LodSection.PackRun(0, 11, 0) });
        MeshResult steppedMesh = LodMesher.BuildMesh(Fixtures.Job(stepped));
        c.True(Quads(steppedMesh.VertexCount) > 5, "a column at a different height breaks the plane");
    }

    /// <summary>
    /// Horizontal extent scales with the level's block step, but Y does not: y values are
    /// absolute world blocks at every level. Scaling Y too would sink coarse terrain into
    /// the ground, progressively further at each level out.
    /// </summary>
    static void LevelScaling(Check c)
    {
        LodSection flat = Solid(yTop: 10, yBottom: 0);

        MeshResult l0 = LodMesher.BuildMesh(Fixtures.Job(flat, LodWorld.SectionKey(0, 0, 0)));
        MeshResult l2 = LodMesher.BuildMesh(Fixtures.Job(flat, LodWorld.SectionKey(2, 0, 0)));

        c.Eq(Quads(l0.VertexCount), Quads(l2.VertexCount), "level does not change the quad count");
        c.Eq((float)Gs, Every3rd(l0.Xyz, 0).Max(), "L0 spans one block per column");
        c.Eq((float)(Gs * 4), Every3rd(l2.Xyz, 0).Max(), "L2 spans four blocks per column");
        c.Eq(10f, Every3rd(l2.Xyz, 1).Max(), "L2 keeps absolute block heights");
    }

    /// <summary>
    /// The tint slot rides in the vertex alpha byte in three bands, because the vertex
    /// format is position plus colour and there is nowhere else to put it. The shader
    /// divides by TINT_SLOTS to recover which band it is — so the band boundaries here and
    /// the constant in the GLSL are the same number seen from two sides.
    /// </summary>
    static void AlphaBands(Check c)
    {
        c.Eq((byte)5, AlphaOf(Column(flags: 0, tintSlot: 5)), "opaque encodes the slot directly");
        c.Eq((byte)(LodTintRegistry.MaxSlots + 5),
            AlphaOf(Column(LodPaletteEntry.FlagWater, tintSlot: 5)), "water sits in the second band");
        c.Eq((byte)(LodTintRegistry.MaxSlots * 2 + 5),
            AlphaOf(Column(LodPaletteEntry.FlagThin, tintSlot: 5)), "thin cover sits in the third band");

        // An out-of-range slot must fall back to the identity tint rather than wrap into
        // the next band and repaint the block as water.
        c.Eq((byte)LodTintRegistry.SlotNone,
            AlphaOf(Column(flags: 0, tintSlot: (byte)LodTintRegistry.MaxSlots)),
            "a slot at the limit falls back to no tint");
        c.Eq((byte)LodTintRegistry.SlotNone,
            AlphaOf(Column(flags: 0, tintSlot: 255)), "a wildly out-of-range slot falls back to no tint");
    }

    static void WaterIsASeparatePass(Check c)
    {
        LodSection sea = Solid(yTop: 10, yBottom: 0, flags: LodPaletteEntry.FlagWater);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(sea));

        c.Eq(0, mesh.VertexCount, "an all-water section contributes nothing to the opaque pass");
        c.True(mesh.WaterVertexCount > 0, "water geometry lands in the blended pass");
        c.True(mesh.WaterXyz != null && mesh.WaterIndices != null, "the water pass carries its own buffers");

        // Water has no floor quads: they would z-fight with the seabed below.
        LodSection land = Solid(yTop: 10, yBottom: 0);
        MeshResult landMesh = LodMesher.BuildMesh(Fixtures.Job(land));
        c.Eq(0, landMesh.WaterVertexCount, "an all-solid section contributes nothing to the water pass");
    }

    /// <summary>
    /// Ground cover is a few centimetres of plant in a one-block cell. Drawn as a cube it
    /// turned meadows into fields of solid colour, so it is drawn as a mat instead: top face
    /// only, no walls, lifted a quarter block off the soil.
    ///
    /// The offset is measured UP from the run's bottom, never down from its top. Mip merging
    /// fuses adjacent thin runs, so at coarse levels one run can span several blocks, and a
    /// fixed drop from the top left the mat floating in mid-air.
    /// </summary>
    static void ThinMats(Check c)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 10, yBottom: 4)));

        c.Eq(0, mesh.VertexCount, "thin cover draws nothing in the opaque pass");
        c.Eq(1, Quads(mesh.WaterVertexCount), "thin cover is a single quad: a top face and no walls");
        c.Eq(4.25f, Every3rd(mesh.WaterXyz!, 1).Max(), "the mat sits a quarter block above its own base");

        // A tall run left by mip merging must still sit on the ground, not at its top.
        MeshResult tall = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 40, yBottom: 4)));
        c.Eq(4.25f, Every3rd(tall.WaterXyz!, 1).Max(), "a mip-merged tall thin run still sits on its base");

        // Clamped so the mat can never rise above the run it stands for.
        MeshResult flat = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 5, yBottom: 5)));
        c.Eq(5f, Every3rd(flat.WaterXyz!, 1).Max(), "a zero-height thin run is clamped to its own top");
    }

    /// <summary>
    /// Three deliberately asymmetric rules, each one the fix for a specific artefact:
    ///   - solid is culled only by solid, so a seabed stays visible through the water;
    ///   - water is culled by anything, so a submerged cliff does not double up;
    ///   - thin cover never culls anything, because a fern on a shoreline was deleting the
    ///     wall of the pond beside it and letting you see through the water's edge.
    /// </summary>
    static void CoverageRules(Check c)
    {
        // Solid beside water: the solid wall survives.
        c.True(WallsBetween(c, LodPaletteEntry.FlagWater, 0) > 0,
            "a solid wall is not culled by water beside it");

        // Water beside solid: the water wall is culled.
        c.Eq(0, WallsBetween(c, 0, LodPaletteEntry.FlagWater),
            "a water wall is culled by solid beside it");

        // Solid beside solid: culled, the ordinary case.
        c.Eq(0, WallsBetween(c, 0, 0), "a solid wall is culled by solid beside it");

        // Solid beside thin: the wall survives, because a mat covers nothing.
        c.True(WallsBetween(c, LodPaletteEntry.FlagThin, 0) > 0,
            "a solid wall is not culled by thin cover beside it");
    }

    /// <summary>
    /// A missing neighbour section is the edge of explored space, and renders as a wall.
    /// Treating it as covered would open the world at every frontier; treating a present
    /// but empty neighbour as a wall would build one down the middle of every plain.
    /// </summary>
    static void Frontier(Check c)
    {
        LodSection flat = Solid(yTop: 10, yBottom: 0);

        MeshResult alone = LodMesher.BuildMesh(Fixtures.Job(flat));
        c.Eq(5, Quads(alone.VertexCount), "with no neighbours, all four frontier walls are drawn");

        // West neighbour present and matching: that wall goes away.
        var withWest = new SectionSnapshot?[4];
        withWest[0] = Fixtures.Snap(flat);
        MeshResult joined = LodMesher.BuildMesh(Fixtures.Job(flat, 0, withWest));
        c.Eq(4, Quads(joined.VertexCount), "a matching west neighbour removes the west wall");

        // All four present: only the surface remains.
        var allFour = new SectionSnapshot?[4];
        for (int i = 0; i < 4; i++) allFour[i] = Fixtures.Snap(flat);
        MeshResult surrounded = LodMesher.BuildMesh(Fixtures.Job(flat, 0, allFour));
        c.Eq(1, Quads(surrounded.VertexCount), "fully surrounded terrain is just its surface");

        // A neighbour that is present but shorter leaves the exposed part of the wall.
        LodSection shorter = Solid(yTop: 4, yBottom: 0);
        var withShort = new SectionSnapshot?[4];
        for (int i = 0; i < 4; i++) withShort[i] = Fixtures.Snap(shorter);
        MeshResult stepped = LodMesher.BuildMesh(Fixtures.Job(flat, 0, withShort));
        c.Eq(5, Quads(stepped.VertexCount), "a shorter neighbour leaves the exposed wall above it");
    }

    // ---- helpers ----

    /// <summary>
    /// Walls the subject column emits on the edge it shares with its neighbour.
    ///
    /// Both columns' walls land on the same plane — the subject's east face and the
    /// neighbour's west face are both at x = 11 — so the plane alone cannot tell them
    /// apart. The pass does: a translucent column writes to the water buffer and an opaque
    /// one to the opaque buffer, and the two columns here always differ in exactly that.
    /// </summary>
    static int WallsBetween(Check c, byte neighborFlags, byte subjectFlags)
    {
        var s = new LodSection();
        int subject = s.FindOrAddPaletteEntry(blockId: 1, color: 0x00808080, flags: subjectFlags);
        int neighbor = s.FindOrAddPaletteEntry(blockId: 2, color: 0x00304050, flags: neighborFlags);

        s.SetColumn(LodSection.ColumnIndex(10, 10), new[] { LodSection.PackRun(subject, 10, 0) });
        s.SetColumn(LodSection.ColumnIndex(11, 10), new[] { LodSection.PackRun(neighbor, 10, 0) });

        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(s));

        bool subjectIsTranslucent =
            (subjectFlags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin)) != 0;

        return QuadsOnEastEdgeOf(subjectIsTranslucent ? mesh.WaterXyz : mesh.Xyz, 11f);
    }

    /// <summary>Quads whose four vertices all sit on the given x plane, i.e. an east/west wall.</summary>
    static int QuadsOnEastEdgeOf(float[]? xyz, float x)
    {
        if (xyz == null) return 0;
        int count = 0;
        for (int v = 0; v + 12 <= xyz.Length; v += 12)
        {
            bool onPlane = true;
            for (int k = 0; k < 4; k++)
            {
                if (Math.Abs(xyz[v + k * 3] - x) > 0.0001f) { onPlane = false; break; }
            }
            if (onPlane) count++;
        }
        return count;
    }

    static byte AlphaOf(LodSection section)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(section));
        byte[]? rgba = mesh.VertexCount > 0 ? mesh.Rgba : mesh.WaterRgba;
        return rgba is { Length: >= 4 } ? rgba[3] : (byte)255;
    }

    /// <summary>A section with exactly one captured column.</summary>
    static LodSection Column(byte flags = 0, byte tintSlot = 0, int yTop = 10, int yBottom = 0)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: 1, color: 0x00A0B0C0, flags: flags, tintSlot: tintSlot);
        s.SetColumn(LodSection.ColumnIndex(5, 5), new[] { LodSection.PackRun(0, yTop, yBottom) });
        return s;
    }

    static LodSection Solid(int yTop, int yBottom, byte flags = 0)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: 1, color: 0x00607080, flags: flags);
        ulong[] run = { LodSection.PackRun(0, yTop, yBottom) };
        for (int col = 0; col < Fixtures.Total; col++) s.SetColumn(col, run);
        return s;
    }

    static int Quads(int vertexCount) => vertexCount / 4;

    static float[] Every3rd(float[] xyz, int offset)
    {
        var result = new float[xyz.Length / 3];
        for (int i = 0; i < result.Length; i++) result[i] = xyz[i * 3 + offset];
        return result;
    }
}
