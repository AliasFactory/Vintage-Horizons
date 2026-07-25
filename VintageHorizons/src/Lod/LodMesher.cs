namespace VintageHorizons;

/// <summary>
/// Turns a section snapshot into raw vertex data (worker thread). Every run is a box;
/// faces are collected first, then greedy-merged before emission:
///
/// - Horizontal faces (tops/bottoms) group into Y-planes per (y, palette, side) and
///   merge into maximal rectangles with a standard 2D greedy sweep — ocean surfaces
///   and plains collapse from thousands of quads to a handful.
/// - Vertical faces (walls) merge along their strip axis where adjacent columns
///   expose identical (span, palette) segments — cliffs and frontier walls collapse
///   into long ribbons.
///
/// Coverage rules (unchanged from the non-greedy mesher): solid faces are only
/// culled by solid neighbors so terrain shows through translucent water; water
/// faces are culled by any coverage. A missing neighbor section is the frontier
/// of explored space and renders as a wall.
/// </summary>
public static class LodMesher
{
    const int W = 0, E = 1, N = 2, S = 3;

    // The tint slot rides in the alpha byte, so the vertex format stays position+colour:
    //   0..63    opaque,     tint slot = alpha
    //   64..127  water,      tint slot = alpha - 64
    //   128..191 thin plant, tint slot = alpha - 128
    // Slot 0 is the identity tint. The band picks the blend factor in the shader, so
    // water and flowers can be see-through by different amounts.
    const byte WaterBase = LodTintRegistry.MaxSlots;
    const byte ThinBase = LodTintRegistry.MaxSlots * 2;

    static byte AlphaFor(byte paletteFlags, byte tintSlot)
    {
        byte slot = tintSlot < LodTintRegistry.MaxSlots ? tintSlot : (byte)LodTintRegistry.SlotNone;
        if ((paletteFlags & LodPaletteEntry.FlagWater) != 0) return (byte)(WaterBase + slot);
        if ((paletteFlags & LodPaletteEntry.FlagThin) != 0) return (byte)(ThinBase + slot);
        return slot;
    }

    class Buffers
    {
        public readonly List<float> Xyz = new(16384);
        public readonly List<byte> Rgba = new(8192);
        public readonly List<int> Indices = new(12288);
    }

    /// <summary>One exposed horizontal face: column cell + plane y + palette.</summary>
    readonly record struct HFace(short Cx, short Cz, short Y, short Pid, bool Bottom, bool Water);

    /// <summary>One exposed wall segment on a column edge.</summary>
    readonly record struct VFace(byte Dir, short Fixed, short Along, short YTop, short YBottom, short Pid, bool Water);

    [ThreadStatic] static List<HFace>? hFaces;
    [ThreadStatic] static List<VFace>? vFaces;

    public static MeshResult BuildMesh(MeshJob job)
    {
        int level = LodWorld.KeyLevel(job.Key);
        int step = LodSection.ColumnStepBlocks << level;
        int gs = LodSection.GridSize;
        SectionSnapshot self = job.Self;

        var opaque = new Buffers();
        var water = new Buffers();
        var hf = hFaces ??= new List<HFace>(8192);
        var vf = vFaces ??= new List<VFace>(8192);
        hf.Clear();
        vf.Clear();

        // ---- Phase 1: collect exposed faces ----

        for (int cz = 0; cz < gs; cz++)
        {
            for (int cx = 0; cx < gs; cx++)
            {
                int col = LodSection.ColumnIndex(cx, cz);
                if (!self.Captured[col]) continue;

                Span<ulong> runs = self.ColumnRuns(col);

                for (int r = 0; r < runs.Length; r++)
                {
                    ulong run = runs[r];
                    int yTop = LodSection.RunYTop(run);
                    int yBottom = LodSection.RunYBottom(run);
                    int pid = LodSection.RunPaletteId(run);
                    bool isTranslucent = IsTranslucent(self.PaletteFlags[pid]);

                    bool topCovered = r > 0
                        && LodSection.RunYBottom(runs[r - 1]) == yTop
                        && IsTranslucentRun(self, runs[r - 1]) == isTranslucent;
                    if (!topCovered) hf.Add(new HFace((short)cx, (short)cz, (short)yTop, (short)pid, false, isTranslucent));

                    bool bottomCovered = r < runs.Length - 1
                        && LodSection.RunYTop(runs[r + 1]) == yBottom
                        && IsTranslucentRun(self, runs[r + 1]) == isTranslucent;
                    if (!bottomCovered && yBottom > 1 && !isTranslucent)
                    {
                        hf.Add(new HFace((short)cx, (short)cz, (short)yBottom, (short)pid, true, false));
                    }

                    bool solidCoverOnly = !isTranslucent;
                    CollectSide(vf, job, W, cx, cz, cx - 1, cz, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                    CollectSide(vf, job, E, cx, cz, cx + 1, cz, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                    CollectSide(vf, job, N, cx, cz, cx, cz - 1, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                    CollectSide(vf, job, S, cx, cz, cx, cz + 1, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                }
            }
        }

        // ---- Phase 2: greedy-merge and emit ----

        EmitHorizontalGreedy(hf, self, opaque, water, step);
        EmitVerticalMerged(vf, self, opaque, water, step);

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

    // ---- Horizontal faces: per-plane 2D greedy rectangles ----

    [ThreadStatic] static int[]? planeGrid;
    [ThreadStatic] static int planeGridStamp;

    static void EmitHorizontalGreedy(List<HFace> faces, SectionSnapshot self, Buffers opaque, Buffers water, int step)
    {
        if (faces.Count == 0) return;
        int gs = LodSection.GridSize;

        // Group faces into planes by (y, pid, side, phase); grid cells hold a stamp so
        // we never clear the array between planes.
        faces.Sort((a, b) =>
        {
            int c = a.Y.CompareTo(b.Y);
            if (c != 0) return c;
            c = a.Pid.CompareTo(b.Pid);
            if (c != 0) return c;
            c = a.Bottom.CompareTo(b.Bottom);
            if (c != 0) return c;
            c = a.Cz.CompareTo(b.Cz);
            return c != 0 ? c : a.Cx.CompareTo(b.Cx);
        });

        var grid = planeGrid ??= new int[gs * gs];

        int start = 0;
        while (start < faces.Count)
        {
            HFace first = faces[start];
            int end = start;
            while (end < faces.Count
                && faces[end].Y == first.Y
                && faces[end].Pid == first.Pid
                && faces[end].Bottom == first.Bottom)
            {
                end++;
            }

            int stamp = ++planeGridStamp;
            for (int i = start; i < end; i++)
            {
                grid[faces[i].Cz * gs + faces[i].Cx] = stamp;
            }

            Buffers buf = first.Water ? water : opaque;
            int color = self.PaletteColors[first.Pid];
            byte alpha = AlphaFor(self.PaletteFlags[first.Pid], self.PaletteTintSlots[first.Pid]);

            for (int i = start; i < end; i++)
            {
                int cx = faces[i].Cx;
                int cz = faces[i].Cz;
                if (grid[cz * gs + cx] != stamp) continue; // already consumed

                // Extend width along +X, then height along +Z while the full row matches.
                int wRect = 1;
                while (cx + wRect < gs && grid[cz * gs + cx + wRect] == stamp) wRect++;

                int hRect = 1;
                while (cz + hRect < gs)
                {
                    bool rowOk = true;
                    for (int dx = 0; dx < wRect; dx++)
                    {
                        if (grid[(cz + hRect) * gs + cx + dx] != stamp) { rowOk = false; break; }
                    }
                    if (!rowOk) break;
                    hRect++;
                }

                for (int dz = 0; dz < hRect; dz++)
                {
                    for (int dx = 0; dx < wRect; dx++)
                    {
                        grid[(cz + dz) * gs + cx + dx] = 0;
                    }
                }

                float x0 = cx * step;
                float x1 = (cx + wRect) * step;
                float z0 = cz * step;
                float z1 = (cz + hRect) * step;
                float y = first.Y;

                if (first.Bottom)
                {
                    AddQuad(buf, color, alpha, x0, y, z0, x0, y, z1, x1, y, z1, x1, y, z0);
                }
                else
                {
                    AddQuad(buf, color, alpha, x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1);
                }
            }

            start = end;
        }
    }

    // ---- Vertical faces: merge along the strip axis ----

    static void EmitVerticalMerged(List<VFace> faces, SectionSnapshot self, Buffers opaque, Buffers water, int step)
    {
        if (faces.Count == 0) return;

        faces.Sort((a, b) =>
        {
            int c = a.Dir.CompareTo(b.Dir);
            if (c != 0) return c;
            c = a.Fixed.CompareTo(b.Fixed);
            if (c != 0) return c;
            c = a.YTop.CompareTo(b.YTop);
            if (c != 0) return c;
            c = a.YBottom.CompareTo(b.YBottom);
            if (c != 0) return c;
            c = a.Pid.CompareTo(b.Pid);
            return c != 0 ? c : a.Along.CompareTo(b.Along);
        });

        int i = 0;
        while (i < faces.Count)
        {
            VFace seg = faces[i];
            int alongEnd = seg.Along + 1;
            int j = i + 1;
            while (j < faces.Count)
            {
                VFace next = faces[j];
                if (next.Dir != seg.Dir || next.Fixed != seg.Fixed || next.YTop != seg.YTop
                    || next.YBottom != seg.YBottom || next.Pid != seg.Pid || next.Along != alongEnd)
                {
                    break;
                }
                alongEnd++;
                j++;
            }

            Buffers buf = seg.Water ? water : opaque;
            int color = self.PaletteColors[seg.Pid];
            byte alpha = AlphaFor(self.PaletteFlags[seg.Pid], self.PaletteTintSlots[seg.Pid]);

            // W/E walls run along Z at fixed X; N/S walls run along X at fixed Z.
            bool xWall = seg.Dir is W or E;
            float fixedCoord = seg.Dir switch
            {
                W => seg.Fixed * step,
                E => (seg.Fixed + 1) * step,
                N => seg.Fixed * step,
                _ => (seg.Fixed + 1) * step,
            };
            float a0 = seg.Along * step;
            float a1 = alongEnd * step;

            if (xWall)
            {
                AddQuad(buf, color, alpha,
                    fixedCoord, seg.YBottom, a0, fixedCoord, seg.YBottom, a1,
                    fixedCoord, seg.YTop, a1, fixedCoord, seg.YTop, a0);
            }
            else
            {
                AddQuad(buf, color, alpha,
                    a0, seg.YBottom, fixedCoord, a1, seg.YBottom, fixedCoord,
                    a1, seg.YTop, fixedCoord, a0, seg.YTop, fixedCoord);
            }

            i = j;
        }
    }

    // ---- Face collection helpers ----

    static bool IsTranslucent(byte flags) =>
        (flags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin)) != 0;

    static bool IsTranslucentRun(SectionSnapshot s, ulong run) =>
        IsTranslucent(s.PaletteFlags[LodSection.RunPaletteId(run)]);

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

    /// <summary>Collect exposed wall segments for [yBottom, yTop) minus the neighbor's covered intervals.</summary>
    static void CollectSide(List<VFace> vf, MeshJob job, int dir, int cx, int cz, int ncx, int ncz,
        int yTop, int yBottom, int pid, bool isTranslucent, bool solidCoverOnly)
    {
        var (nb, ncol) = NeighborColumn(job, ncx, ncz);
        Span<ulong> neighborRuns = nb != null && nb.Captured[ncol] ? nb.ColumnRuns(ncol) : Span<ulong>.Empty;

        // For W/E walls the strip axis is Z (fixed = cx); for N/S it's X (fixed = cz).
        short fix = (short)(dir is W or E ? cx : cz);
        short along = (short)(dir is W or E ? cz : cx);

        int cur = yTop;

        for (int i = 0; i < neighborRuns.Length && cur > yBottom; i++)
        {
            if (solidCoverOnly && IsTranslucentRun(nb!, neighborRuns[i])) continue;

            int nTop = LodSection.RunYTop(neighborRuns[i]);
            int nBottom = LodSection.RunYBottom(neighborRuns[i]);
            if (nTop <= yBottom) break;
            if (nBottom >= cur) continue;

            int coverTop = Math.Min(nTop, cur);
            if (coverTop < cur)
            {
                vf.Add(new VFace((byte)dir, fix, along, (short)cur, (short)coverTop, (short)pid, isTranslucent));
            }
            cur = Math.Max(nBottom, yBottom);
        }

        if (cur > yBottom)
        {
            vf.Add(new VFace((byte)dir, fix, along, (short)cur, (short)yBottom, (short)pid, isTranslucent));
        }
    }

    // ---- Emission primitives ----

    static void AddQuad(Buffers buf, int color, byte alpha,
        float x0, float y0, float z0, float x1, float y1, float z1,
        float x2, float y2, float z2, float x3, float y3, float z3)
    {
        int baseVert = buf.Xyz.Count / 3;
        AddVert(buf, color, alpha, x0, y0, z0);
        AddVert(buf, color, alpha, x1, y1, z1);
        AddVert(buf, color, alpha, x2, y2, z2);
        AddVert(buf, color, alpha, x3, y3, z3);
        buf.Indices.Add(baseVert);
        buf.Indices.Add(baseVert + 1);
        buf.Indices.Add(baseVert + 2);
        buf.Indices.Add(baseVert);
        buf.Indices.Add(baseVert + 2);
        buf.Indices.Add(baseVert + 3);
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
}
