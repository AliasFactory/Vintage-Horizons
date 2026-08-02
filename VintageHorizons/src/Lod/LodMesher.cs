namespace VintageHorizons;

/// <summary>
/// Turns a section snapshot into raw vertex data, on a worker thread. Each run is a box. The
/// mesher collects the faces first. Then it merges them greedily, and only then does it write
/// them.
///
/// - A horizontal face, which is a top or a bottom, goes into a Y plane for each combination
///   of y, palette and side. Then a standard 2D greedy sweep merges the faces into the
///   largest possible rectangles. Thus an ocean surface or a plain goes from thousands of
///   quads to a small number.
/// - A vertical face, which is a wall, merges along its strip axis. This occurs where the
///   adjacent columns show identical segments of span and palette. Thus a cliff or a
///   frontier wall becomes one long shape.
///
/// The coverage rules are the same as the rules of the mesher without the greedy merge. A
/// solid neighbour culls a solid face, and nothing else does. Thus terrain is visible through
/// translucent water. Any coverage culls a water face.
///
/// A neighbour section that is absent is the frontier of the explored space, and the mesher
/// draws it as a wall.
/// </summary>
public static class LodMesher
{
    const int W = 0, E = 1, N = 2, S = 3;

    // The tint slot travels in the alpha byte. Thus the vertex format stays position and
    // color:
    //   0..63    opaque,     tint slot = alpha
    //   64..127  water,      tint slot = alpha - 64
    //   128..191 thin plant, tint slot = alpha - 128
    // Slot 0 is the tint that changes nothing. The band selects the blend factor in the
    // shader. Thus water and a flower can have different quantities of transparency.
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

    /// <summary>One horizontal face that is visible. It holds the column cell, the y of the
    /// plane, and the palette.</summary>
    readonly record struct HFace(short Cx, short Cz, short Y, short Pid, bool Bottom, bool Water, bool Thin, short YBottom);

    /// <summary>One wall segment that is visible, on the edge of a column.</summary>
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

        // ---- Phase 1: collect the faces that are visible ----

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

                    bool isThin = (self.PaletteFlags[pid] & LodPaletteEntry.FlagThin) != 0;

                    // Ground cover is a few centimetres of plant inside a cell of one
                    // block. Thus the mesher draws it as a low mat, with a top face only,
                    // immediately above the soil.
                    //
                    // A full cube turned a meadow into a field of solid blocks. That was the
                    // real problem. Most flowers hold a correct color.
                    if (isThin)
                    {
                        hf.Add(new HFace((short)cx, (short)cz, (short)yTop, (short)pid, false, true, true, (short)yBottom));
                        continue;
                    }

                    bool topCovered = r > 0
                        && LodSection.RunYBottom(runs[r - 1]) == yTop
                        && !IsThinRun(self, runs[r - 1])
                        && IsTranslucentRun(self, runs[r - 1]) == isTranslucent;
                    if (!topCovered) hf.Add(new HFace((short)cx, (short)cz, (short)yTop, (short)pid, false, isTranslucent, false, (short)yBottom));

                    bool bottomCovered = r < runs.Length - 1
                        && LodSection.RunYTop(runs[r + 1]) == yBottom
                        && !IsThinRun(self, runs[r + 1])
                        && IsTranslucentRun(self, runs[r + 1]) == isTranslucent;
                    if (!bottomCovered && yBottom > 1 && !isTranslucent)
                    {
                        hf.Add(new HFace((short)cx, (short)cz, (short)yBottom, (short)pid, true, false, false, (short)yBottom));
                    }

                    bool solidCoverOnly = !isTranslucent;
                    CollectSide(vf, job, W, cx, cz, cx - 1, cz, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                    CollectSide(vf, job, E, cx, cz, cx + 1, cz, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                    CollectSide(vf, job, N, cx, cz, cx, cz - 1, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                    CollectSide(vf, job, S, cx, cz, cx, cz + 1, yTop, yBottom, pid, isTranslucent, solidCoverOnly);
                }
            }
        }

        // ---- Phase 2: merge greedily, then write ----

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

    // ---- Horizontal faces: 2D greedy rectangles in each plane ----

    [ThreadStatic] static int[]? planeGrid;
    [ThreadStatic] static int planeGridStamp;

    static void EmitHorizontalGreedy(List<HFace> faces, SectionSnapshot self, Buffers opaque, Buffers water, int step)
    {
        if (faces.Count == 0) return;
        int gs = LodSection.GridSize;

        // Group the faces into planes, by y, pid, side and phase. A grid cell holds a
        // stamp. Thus the mod never clears the array between two planes.
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
                && faces[end].Bottom == first.Bottom
                && faces[end].Thin == first.Thin
                && faces[end].YBottom == first.YBottom)
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

                // Increase the width along +X. Then increase the height along +Z, while the
                // full row is the same.
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
                // Ground cover is a quarter of a block above the soil below it. That
                // distance prevents z-fighting, and a player cannot see it as a step.
                //
                // Measure UP from the bottom of the run. Never measure down from its top.
                // The mip merge joins adjacent thin runs. Thus at a coarse level one run
                // covers several blocks, and a fixed distance down from the top left the mat
                // in the air.
                //
                // The value has a clamp, thus the mat can never go above the run that it
                // represents.
                //
                // Y is in absolute blocks, and step does NOT scale it. Thus step does not
                // scale the offset either.
                float y = first.Thin ? Math.Min(first.YBottom + 0.25f, first.Y) : first.Y;

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

    // ---- Vertical faces: merge along the axis of the strip ----

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

            // A west or east wall goes along Z, at a fixed X. A north or south wall goes
            // along X, at a fixed Z.
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

    // ---- Helpers that collect the faces ----

    static bool IsTranslucent(byte flags) =>
        (flags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin)) != 0;

    static bool IsTranslucentRun(SectionSnapshot s, ulong run) =>
        IsTranslucent(s.PaletteFlags[LodSection.RunPaletteId(run)]);

    /// <summary>
    /// The mesher draws thin ground cover as a mat that is a quarter of a block high. Thus
    /// it fills almost none of its cell, and it must never hide anything.
    ///
    /// Without this rule, a fern on a shoreline counted as cover for the water beside it.
    /// That removed the wall of the water, and a player saw through the edge of the pond.
    /// </summary>
    static bool IsThinRun(SectionSnapshot s, ulong run) =>
        (s.PaletteFlags[LodSection.RunPaletteId(run)] & LodPaletteEntry.FlagThin) != 0;

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

    /// <summary>Collect the wall segments that are visible for [yBottom, yTop), and remove
    /// the intervals that the neighbour covers.</summary>
    static void CollectSide(List<VFace> vf, MeshJob job, int dir, int cx, int cz, int ncx, int ncz,
        int yTop, int yBottom, int pid, bool isTranslucent, bool solidCoverOnly)
    {
        var (nb, ncol) = NeighborColumn(job, ncx, ncz);
        Span<ulong> neighborRuns = nb != null && nb.Captured[ncol] ? nb.ColumnRuns(ncol) : Span<ulong>.Empty;

        // For a west or east wall, the axis of the strip is Z, and the fixed value is cx.
        // For a north or south wall, the axis is X, and the fixed value is cz.
        short fix = (short)(dir is W or E ? cx : cz);
        short along = (short)(dir is W or E ? cz : cx);

        int cur = yTop;

        for (int i = 0; i < neighborRuns.Length && cur > yBottom; i++)
        {
            // A mat never covers anything. Then, a solid neighbour culls a solid face, and
            // nothing else does. Thus terrain stays visible through water.
            if (IsThinRun(nb!, neighborRuns[i])) continue;
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

    // ---- The primitives that write the vertices ----

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
