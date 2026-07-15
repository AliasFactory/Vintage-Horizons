namespace VintageHorizons;

/// <summary>
/// Turns a section snapshot into raw vertex data (worker thread). Every run is a box:
/// top/bottom faces appear where the column has air gaps, side faces where the
/// adjacent column's runs don't cover the span (interval subtraction, DH-style).
/// Neighbor sections cull faces across section borders; a missing neighbor means
/// the frontier of explored space, which renders as a wall.
/// </summary>
public static class LodMesher
{
    const int W = 0, E = 1, N = 2, S = 3;

    public static MeshResult BuildMesh(MeshJob job)
    {
        int level = LodWorld.KeyLevel(job.Key);
        int step = LodSection.ColumnStepBlocks << level;
        int gs = LodSection.GridSize;
        SectionSnapshot self = job.Self;

        var xyz = new List<float>(16384);
        var rgba = new List<byte>(8192);
        var indices = new List<int>(12288);

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
                    int color = self.PaletteColors[LodSection.RunPaletteId(run)];

                    // Top face: exposed unless the run above ends exactly where we start.
                    bool topCovered = r > 0 && LodSection.RunYBottom(runs[r - 1]) == yTop;
                    if (!topCovered) AddQuad(xyz, rgba, indices, color,
                        x0, yTop, z0, x1, yTop, z0, x1, yTop, z1, x0, yTop, z1);

                    // Bottom face: exposed when there's a gap below (overhang undersides).
                    bool bottomCovered = r < runs.Length - 1 && LodSection.RunYTop(runs[r + 1]) == yBottom;
                    if (!bottomCovered && yBottom > 1) AddQuad(xyz, rgba, indices, color,
                        x0, yBottom, z0, x0, yBottom, z1, x1, yBottom, z1, x1, yBottom, z0);

                    // Side faces against the four neighboring columns.
                    EmitSide(xyz, rgba, indices, color, yTop, yBottom,
                        GetNeighborRuns(job, cx - 1, cz), true, x0, z0, z1);
                    EmitSide(xyz, rgba, indices, color, yTop, yBottom,
                        GetNeighborRuns(job, cx + 1, cz), true, x1, z0, z1);
                    EmitSide(xyz, rgba, indices, color, yTop, yBottom,
                        GetNeighborRuns(job, cx, cz - 1), false, z0, x0, x1);
                    EmitSide(xyz, rgba, indices, color, yTop, yBottom,
                        GetNeighborRuns(job, cx, cz + 1), false, z1, x0, x1);
                }
            }
        }

        return new MeshResult
        {
            Key = job.Key,
            Xyz = xyz.ToArray(),
            Rgba = rgba.ToArray(),
            Indices = indices.ToArray(),
            VertexCount = xyz.Count / 3,
            IndexCount = indices.Count,
        };
    }

    /// <summary>
    /// Runs of the adjacent column; empty span when the neighbor column is genuinely
    /// empty, null-ish (uncaptured/missing section) is treated as empty too so the
    /// frontier of explored space gets a wall.
    /// </summary>
    static Span<ulong> GetNeighborRuns(MeshJob job, int cx, int cz)
    {
        int gs = LodSection.GridSize;

        if (cx >= 0 && cx < gs && cz >= 0 && cz < gs)
        {
            int col = LodSection.ColumnIndex(cx, cz);
            return job.Self.Captured[col] ? job.Self.ColumnRuns(col) : Span<ulong>.Empty;
        }

        SectionSnapshot? nb;
        int ncx = cx, ncz = cz;
        if (cx < 0) { nb = job.Neighbors[W]; ncx = gs - 1; }
        else if (cx >= gs) { nb = job.Neighbors[E]; ncx = 0; }
        else if (cz < 0) { nb = job.Neighbors[N]; ncz = gs - 1; }
        else { nb = job.Neighbors[S]; ncz = 0; }

        if (nb == null) return Span<ulong>.Empty;
        int ncol = LodSection.ColumnIndex(ncx, ncz);
        return nb.Captured[ncol] ? nb.ColumnRuns(ncol) : Span<ulong>.Empty;
    }

    /// <summary>Emit wall segments for [yBottom, yTop) minus the neighbor's covered intervals.</summary>
    static void EmitSide(List<float> xyz, List<byte> rgba, List<int> indices, int color,
        int yTop, int yBottom, Span<ulong> neighborRuns, bool xWall, float fixedCoord, float a0, float a1)
    {
        int cur = yTop;

        for (int i = 0; i < neighborRuns.Length && cur > yBottom; i++)
        {
            int nTop = LodSection.RunYTop(neighborRuns[i]);
            int nBottom = LodSection.RunYBottom(neighborRuns[i]);
            if (nTop <= yBottom) break;      // neighbor runs are top-down; nothing below overlaps
            if (nBottom >= cur) continue;    // entirely above our remaining span

            int coverTop = Math.Min(nTop, cur);
            if (coverTop < cur)
            {
                EmitWall(xyz, rgba, indices, color, cur, coverTop, xWall, fixedCoord, a0, a1);
            }
            cur = Math.Max(nBottom, yBottom);
        }

        if (cur > yBottom) EmitWall(xyz, rgba, indices, color, cur, yBottom, xWall, fixedCoord, a0, a1);
    }

    static void EmitWall(List<float> xyz, List<byte> rgba, List<int> indices, int color,
        int segTop, int segBottom, bool xWall, float fixedCoord, float a0, float a1)
    {
        int baseVert = xyz.Count / 3;
        if (xWall)
        {
            AddVert(xyz, rgba, color, fixedCoord, segBottom, a0);
            AddVert(xyz, rgba, color, fixedCoord, segBottom, a1);
            AddVert(xyz, rgba, color, fixedCoord, segTop, a1);
            AddVert(xyz, rgba, color, fixedCoord, segTop, a0);
        }
        else
        {
            AddVert(xyz, rgba, color, a0, segBottom, fixedCoord);
            AddVert(xyz, rgba, color, a1, segBottom, fixedCoord);
            AddVert(xyz, rgba, color, a1, segTop, fixedCoord);
            AddVert(xyz, rgba, color, a0, segTop, fixedCoord);
        }
        AddQuadIndices(indices, baseVert);
    }

    static void AddQuad(List<float> xyz, List<byte> rgba, List<int> indices, int color,
        float x0, float y0, float z0, float x1, float y1, float z1,
        float x2, float y2, float z2, float x3, float y3, float z3)
    {
        int baseVert = xyz.Count / 3;
        AddVert(xyz, rgba, color, x0, y0, z0);
        AddVert(xyz, rgba, color, x1, y1, z1);
        AddVert(xyz, rgba, color, x2, y2, z2);
        AddVert(xyz, rgba, color, x3, y3, z3);
        AddQuadIndices(indices, baseVert);
    }

    static void AddVert(List<float> xyz, List<byte> rgba, int color, float x, float y, float z)
    {
        xyz.Add(x);
        xyz.Add(y);
        xyz.Add(z);
        rgba.Add((byte)(color & 0xFF));
        rgba.Add((byte)((color >> 8) & 0xFF));
        rgba.Add((byte)((color >> 16) & 0xFF));
        rgba.Add(255);
    }

    static void AddQuadIndices(List<int> indices, int baseVert)
    {
        indices.Add(baseVert);
        indices.Add(baseVert + 1);
        indices.Add(baseVert + 2);
        indices.Add(baseVert);
        indices.Add(baseVert + 2);
        indices.Add(baseVert + 3);
    }
}
