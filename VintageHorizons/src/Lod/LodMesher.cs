namespace VintageHorizons;

/// <summary>
/// Turns a section snapshot into raw vertex data (worker thread). Every run is a box:
/// top/bottom faces appear where the column has gaps, side faces where the adjacent
/// column's runs don't cover the span (interval subtraction, DH-style). Neighbor
/// sections cull faces across section borders; a missing neighbor means the frontier
/// of explored space, which renders as a wall.
///
/// Water runs go into a separate buffer for a blended second pass. Coverage rules:
/// solid faces are only culled by solid neighbors (so terrain shows through
/// translucent water); water faces are culled by any coverage (water-water and
/// water-solid interfaces are invisible).
/// </summary>
public static class LodMesher
{
    const int W = 0, E = 1, N = 2, S = 3;
    const byte WaterAlpha = 168;

    class Buffers
    {
        public readonly List<float> Xyz = new(16384);
        public readonly List<byte> Rgba = new(8192);
        public readonly List<int> Indices = new(12288);
    }

    public static MeshResult BuildMesh(MeshJob job)
    {
        int level = LodWorld.KeyLevel(job.Key);
        int step = LodSection.ColumnStepBlocks << level;
        int gs = LodSection.GridSize;
        SectionSnapshot self = job.Self;

        var opaque = new Buffers();
        var water = new Buffers();

        for (int cz = 0; cz < gs; cz++)
        {
            for (int cx = 0; cx < gs; cx++)
            {
                int col = LodSection.ColumnIndex(cx, cz);
                if (!self.Captured[col]) continue;

                Span<ulong> runs = self.ColumnRuns(col);
                if (runs.Length == 0) continue;

                float x0 = cx * step;
                float x1 = x0 + step;
                float z0 = cz * step;
                float z1 = z0 + step;

                for (int r = 0; r < runs.Length; r++)
                {
                    ulong run = runs[r];
                    int yTop = LodSection.RunYTop(run);
                    int yBottom = LodSection.RunYBottom(run);
                    int pid = LodSection.RunPaletteId(run);
                    bool isWater = (self.PaletteFlags[pid] & LodPaletteEntry.FlagWater) != 0;
                    int color = self.PaletteColors[pid];
                    byte alpha = isWater ? WaterAlpha : (byte)255;
                    Buffers buf = isWater ? water : opaque;

                    // Vertical neighbors within the column: a face is covered only when
                    // the adjacent run is the same phase (solid-solid or water-water) —
                    // a solid floor under water still needs its top face drawn.
                    bool topCovered = r > 0
                        && LodSection.RunYBottom(runs[r - 1]) == yTop
                        && IsWater(self, runs[r - 1]) == isWater;
                    if (!topCovered) AddQuad(buf, color, alpha,
                        x0, yTop, z0, x1, yTop, z0, x1, yTop, z1, x0, yTop, z1);

                    bool bottomCovered = r < runs.Length - 1
                        && LodSection.RunYTop(runs[r + 1]) == yBottom
                        && IsWater(self, runs[r + 1]) == isWater;
                    if (!bottomCovered && yBottom > 1 && !isWater) AddQuad(buf, color, alpha,
                        x0, yBottom, z0, x0, yBottom, z1, x1, yBottom, z1, x1, yBottom, z0);

                    // Side faces: solid runs are covered only by solid neighbor runs;
                    // water is covered by anything.
                    bool solidCoverOnly = !isWater;
                    EmitSide(buf, color, alpha, yTop, yBottom, job, cx - 1, cz, solidCoverOnly, true, x0, z0, z1);
                    EmitSide(buf, color, alpha, yTop, yBottom, job, cx + 1, cz, solidCoverOnly, true, x1, z0, z1);
                    EmitSide(buf, color, alpha, yTop, yBottom, job, cx, cz - 1, solidCoverOnly, false, z0, x0, x1);
                    EmitSide(buf, color, alpha, yTop, yBottom, job, cx, cz + 1, solidCoverOnly, false, z1, x0, x1);
                }
            }
        }

        return new MeshResult
        {
            Key = job.Key,
            Xyz = opaque.Xyz.ToArray(),
            Rgba = opaque.Rgba.ToArray(),
            Indices = opaque.Indices.ToArray(),
            VertexCount = opaque.Xyz.Count / 3,
            IndexCount = opaque.Indices.Count,
            WaterXyz = water.Xyz.Count > 0 ? water.Xyz.ToArray() : null,
            WaterRgba = water.Xyz.Count > 0 ? water.Rgba.ToArray() : null,
            WaterIndices = water.Xyz.Count > 0 ? water.Indices.ToArray() : null,
            WaterVertexCount = water.Xyz.Count / 3,
            WaterIndexCount = water.Indices.Count,
        };
    }

    static bool IsWater(SectionSnapshot s, ulong run) =>
        (s.PaletteFlags[LodSection.RunPaletteId(run)] & LodPaletteEntry.FlagWater) != 0;

    static (SectionSnapshot? snap, int col) NeighborColumn(MeshJob job, int cx, int cz)
    {
        int gs = LodSection.GridSize;

        if (cx >= 0 && cx < gs && cz >= 0 && cz < gs)
        {
            return (job.Self, LodSection.ColumnIndex(cx, cz));
        }

        SectionSnapshot? nb;
        int ncx = cx, ncz = cz;
        if (cx < 0) { nb = job.Neighbors[W]; ncx = gs - 1; }
        else if (cx >= gs) { nb = job.Neighbors[E]; ncx = 0; }
        else if (cz < 0) { nb = job.Neighbors[N]; ncz = gs - 1; }
        else { nb = job.Neighbors[S]; ncz = 0; }

        return (nb, nb == null ? 0 : LodSection.ColumnIndex(ncx, ncz));
    }

    /// <summary>Emit wall segments for [yBottom, yTop) minus the neighbor's covered intervals.</summary>
    static void EmitSide(Buffers buf, int color, byte alpha, int yTop, int yBottom,
        MeshJob job, int ncx, int ncz, bool solidCoverOnly, bool xWall, float fixedCoord, float a0, float a1)
    {
        var (nb, ncol) = NeighborColumn(job, ncx, ncz);
        Span<ulong> neighborRuns = nb != null && nb.Captured[ncol] ? nb.ColumnRuns(ncol) : Span<ulong>.Empty;

        int cur = yTop;

        for (int i = 0; i < neighborRuns.Length && cur > yBottom; i++)
        {
            if (solidCoverOnly && IsWater(nb!, neighborRuns[i])) continue;

            int nTop = LodSection.RunYTop(neighborRuns[i]);
            int nBottom = LodSection.RunYBottom(neighborRuns[i]);
            if (nTop <= yBottom) break;      // neighbor runs are top-down; nothing below overlaps
            if (nBottom >= cur) continue;    // entirely above our remaining span

            int coverTop = Math.Min(nTop, cur);
            if (coverTop < cur)
            {
                EmitWall(buf, color, alpha, cur, coverTop, xWall, fixedCoord, a0, a1);
            }
            cur = Math.Max(nBottom, yBottom);
        }

        if (cur > yBottom) EmitWall(buf, color, alpha, cur, yBottom, xWall, fixedCoord, a0, a1);
    }

    static void EmitWall(Buffers buf, int color, byte alpha,
        int segTop, int segBottom, bool xWall, float fixedCoord, float a0, float a1)
    {
        int baseVert = buf.Xyz.Count / 3;
        if (xWall)
        {
            AddVert(buf, color, alpha, fixedCoord, segBottom, a0);
            AddVert(buf, color, alpha, fixedCoord, segBottom, a1);
            AddVert(buf, color, alpha, fixedCoord, segTop, a1);
            AddVert(buf, color, alpha, fixedCoord, segTop, a0);
        }
        else
        {
            AddVert(buf, color, alpha, a0, segBottom, fixedCoord);
            AddVert(buf, color, alpha, a1, segBottom, fixedCoord);
            AddVert(buf, color, alpha, a1, segTop, fixedCoord);
            AddVert(buf, color, alpha, a0, segTop, fixedCoord);
        }
        AddQuadIndices(buf, baseVert);
    }

    static void AddQuad(Buffers buf, int color, byte alpha,
        float x0, float y0, float z0, float x1, float y1, float z1,
        float x2, float y2, float z2, float x3, float y3, float z3)
    {
        int baseVert = buf.Xyz.Count / 3;
        AddVert(buf, color, alpha, x0, y0, z0);
        AddVert(buf, color, alpha, x1, y1, z1);
        AddVert(buf, color, alpha, x2, y2, z2);
        AddVert(buf, color, alpha, x3, y3, z3);
        AddQuadIndices(buf, baseVert);
    }

    static void AddVert(Buffers buf, int color, byte alpha, float x, float y, float z)
    {
        buf.Xyz.Add(x);
        buf.Xyz.Add(y);
        buf.Xyz.Add(z);
        buf.Rgba.Add((byte)(color & 0xFF));
        buf.Rgba.Add((byte)((color >> 8) & 0xFF));
        buf.Rgba.Add((byte)((color >> 16) & 0xFF));
        buf.Rgba.Add(alpha);
    }

    static void AddQuadIndices(Buffers buf, int baseVert)
    {
        buf.Indices.Add(baseVert);
        buf.Indices.Add(baseVert + 1);
        buf.Indices.Add(baseVert + 2);
        buf.Indices.Add(baseVert);
        buf.Indices.Add(baseVert + 2);
        buf.Indices.Add(baseVert + 3);
    }
}
