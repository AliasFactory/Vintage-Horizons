namespace VintageHorizons.Checks;

/// <summary>
/// From a section snapshot to vertex data. Two things need a test here.
///
/// The first is the greedy merge. For the same terrain it is the difference between five
/// quads and four thousand.
///
/// The second is the coverage rules. They are asymmetric on purpose. A person found each one
/// after the symmetric version made a visible defect.
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
    /// This is the reason for the shape of the mesher. A flat plain is 4096 columns with
    /// identical tops. Without a merge that is 4096 quads for the surface, and one wall for
    /// each column edge. After the merge it is one rectangle and four frontier shapes.
    /// </summary>
    static void GreedyMerge(Check c)
    {
        LodSection flat = Solid(yTop: 10, yBottom: 0);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(flat));

        // There is 1 top rectangle and 4 frontier walls. There is no bottom face, because
        // yBottom is 0. The mesher skips a floor at y=1 or below, because nothing can see
        // below the world.
        c.Eq(5, Quads(mesh.VertexCount), "a flat 64x64 plain collapses to five quads");
        c.Eq(20, mesh.VertexCount, "five quads is twenty vertices");
        c.Eq(30, mesh.IndexCount, "five quads is thirty indices (two triangles each)");

        // The merged top must cover the section. A claim alone is not sufficient.
        float[] xs = Every3rd(mesh.Xyz, 0);
        float[] zs = Every3rd(mesh.Xyz, 2);
        c.Eq(0f, xs.Min(), "the merged surface starts at the section's near edge");
        c.Eq((float)Gs, xs.Max(), "the merged surface reaches the section's far edge");
        c.Eq(0f, zs.Min(), "the merged surface starts at the near z edge");
        c.Eq((float)Gs, zs.Max(), "the merged surface reaches the far z edge");

        // A hole must stop the merge. Without that, the rectangle covers terrain that is
        // absent.
        LodSection holed = Solid(yTop: 10, yBottom: 0);
        holed.SetColumn(LodSection.ColumnIndex(32, 32), Array.Empty<ulong>());
        MeshResult holedMesh = LodMesher.BuildMesh(Fixtures.Job(holed));
        c.True(Quads(holedMesh.VertexCount) > 5, "a hole in the plain prevents a single-rectangle merge");

        // Two different heights also cannot merge into one plane.
        LodSection stepped = Solid(yTop: 10, yBottom: 0);
        stepped.SetColumn(LodSection.ColumnIndex(32, 32), new[] { LodSection.PackRun(0, 11, 0) });
        MeshResult steppedMesh = LodMesher.BuildMesh(Fixtures.Job(stepped));
        c.True(Quads(steppedMesh.VertexCount) > 5, "a column at a different height breaks the plane");
    }

    /// <summary>
    /// The horizontal extent scales with the block step of the level. Y does not scale,
    /// because a y value is in absolute world blocks at each level. A scale on Y moves coarse
    /// terrain into the ground, and it moves further at each level outward.
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
    /// The tint slot travels in the alpha byte of the vertex, in three bands. The vertex
    /// format is a position and a color, thus there is no other place for it.
    ///
    /// The shader divides by TINT_SLOTS to find the band. Thus the band boundaries here and
    /// the constant in the GLSL are the same number, from two sides.
    /// </summary>
    static void AlphaBands(Check c)
    {
        c.Eq((byte)5, AlphaOf(Column(flags: 0, tintSlot: 5)), "opaque encodes the slot directly");
        c.Eq((byte)(LodTintRegistry.MaxSlots + 5),
            AlphaOf(Column(LodPaletteEntry.FlagWater, tintSlot: 5)), "water sits in the second band");
        c.Eq((byte)(LodTintRegistry.MaxSlots * 2 + 5),
            AlphaOf(Column(LodPaletteEntry.FlagThin, tintSlot: 5)), "thin cover sits in the third band");

        // A slot that is out of range must use the tint that changes nothing. It must not
        // move into the next band and give the block the appearance of water.
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

        // Water has no floor quad. Such a quad z-fights with the sea floor below it.
        LodSection land = Solid(yTop: 10, yBottom: 0);
        MeshResult landMesh = LodMesher.BuildMesh(Fixtures.Job(land));
        c.Eq(0, landMesh.WaterVertexCount, "an all-solid section contributes nothing to the water pass");
    }

    /// <summary>
    /// Ground cover is a few centimetres of plant inside a cell of one block. As a cube it
    /// turned a meadow into a field of solid color. Thus the mesher draws it as a mat: a top
    /// face only, no walls, and a quarter of a block above the soil.
    ///
    /// The offset goes UP from the bottom of the run. It never goes down from the top. The
    /// mip merge joins adjacent thin runs. Thus at a coarse level one run covers several
    /// blocks, and a fixed distance from the top left the mat in the air.
    /// </summary>
    static void ThinMats(Check c)
    {
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 10, yBottom: 4)));

        c.Eq(0, mesh.VertexCount, "thin cover draws nothing in the opaque pass");
        c.Eq(1, Quads(mesh.WaterVertexCount), "thin cover is a single quad: a top face and no walls");
        c.Eq(4.25f, Every3rd(mesh.WaterXyz!, 1).Max(), "the mat sits a quarter block above its own base");

        // A tall run from the mip merge must still be on the ground, and not at its top.
        MeshResult tall = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 40, yBottom: 4)));
        c.Eq(4.25f, Every3rd(tall.WaterXyz!, 1).Max(), "a mip-merged tall thin run still sits on its base");

        // The clamp stops the mat from going above the run that it represents.
        MeshResult flat = LodMesher.BuildMesh(Fixtures.Job(Column(LodPaletteEntry.FlagThin, yTop: 5, yBottom: 5)));
        c.Eq(5f, Every3rd(flat.WaterXyz!, 1).Max(), "a zero-height thin run is clamped to its own top");
    }

    /// <summary>
    /// Three rules that are asymmetric on purpose. Each one corrects a specific defect.
    ///
    ///   - A solid neighbour culls a solid face, and nothing else does. Thus a sea floor
    ///     stays visible through the water.
    ///   - Any coverage culls a water face. Thus a cliff under water does not appear two
    ///     times.
    ///   - Thin cover never culls anything. A fern on a shoreline removed the wall of the
    ///     pond beside it, and a player saw through the edge of the water.
    /// </summary>
    static void CoverageRules(Check c)
    {
        // A solid face beside water: the solid wall stays.
        c.True(WallsBetween(c, LodPaletteEntry.FlagWater, 0) > 0,
            "a solid wall is not culled by water beside it");

        // A water face beside a solid: the mesher culls the water wall.
        c.Eq(0, WallsBetween(c, 0, LodPaletteEntry.FlagWater),
            "a water wall is culled by solid beside it");

        // A solid face beside a solid: the mesher culls it. This is the normal case.
        c.Eq(0, WallsBetween(c, 0, 0), "a solid wall is culled by solid beside it");

        // A solid face beside thin cover: the wall stays, because a mat covers nothing.
        c.True(WallsBetween(c, LodPaletteEntry.FlagThin, 0) > 0,
            "a solid wall is not culled by thin cover beside it");
    }

    /// <summary>
    /// A neighbour section that is absent is the edge of the explored space, and the mesher
    /// draws it as a wall.
    ///
    /// A treatment of that neighbour as cover opens the world at each frontier. A treatment
    /// of a present but empty neighbour as a wall builds a wall across each plain.
    /// </summary>
    static void Frontier(Check c)
    {
        LodSection flat = Solid(yTop: 10, yBottom: 0);

        MeshResult alone = LodMesher.BuildMesh(Fixtures.Job(flat));
        c.Eq(5, Quads(alone.VertexCount), "with no neighbours, all four frontier walls are drawn");

        // The west neighbour is present and it matches. Thus that wall goes away.
        var withWest = new SectionSnapshot?[4];
        withWest[0] = Fixtures.Snap(flat);
        MeshResult joined = LodMesher.BuildMesh(Fixtures.Job(flat, 0, withWest));
        c.Eq(4, Quads(joined.VertexCount), "a matching west neighbour removes the west wall");

        // All four neighbours are present. Thus only the surface stays.
        var allFour = new SectionSnapshot?[4];
        for (int i = 0; i < 4; i++) allFour[i] = Fixtures.Snap(flat);
        MeshResult surrounded = LodMesher.BuildMesh(Fixtures.Job(flat, 0, allFour));
        c.Eq(1, Quads(surrounded.VertexCount), "fully surrounded terrain is just its surface");

        // A neighbour that is present but shorter leaves the visible part of the wall.
        LodSection shorter = Solid(yTop: 4, yBottom: 0);
        var withShort = new SectionSnapshot?[4];
        for (int i = 0; i < 4; i++) withShort[i] = Fixtures.Snap(shorter);
        MeshResult stepped = LodMesher.BuildMesh(Fixtures.Job(flat, 0, withShort));
        c.Eq(5, Quads(stepped.VertexCount), "a shorter neighbour leaves the exposed wall above it");
    }

    // ---- The helpers ----

    /// <summary>
    /// The walls that the subject column writes on the edge that it shares with its
    /// neighbour.
    ///
    /// The walls of both columns are on the same plane. The east face of the subject and the
    /// west face of the neighbour are both at x = 11. Thus the plane alone cannot separate
    /// them.
    ///
    /// The pass can separate them. A translucent column writes to the water buffer, and an
    /// opaque column writes to the opaque buffer. The two columns here always differ in
    /// exactly that.
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
