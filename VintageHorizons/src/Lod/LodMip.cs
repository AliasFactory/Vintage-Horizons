namespace VintageHorizons;

/// <summary>
/// Child→parent section downsampling: every 2×2 child columns merge into one parent
/// column via a y-boundary slice sweep (DH's approach). A slice is solid when at
/// least two of the four child columns cover it; the slice takes the most common
/// covering block. Adjacent same-block slices re-merge into runs.
/// </summary>
public static class LodMip
{
    [ThreadStatic] static List<int>? boundaries;
    [ThreadStatic] static List<ulong>? outRuns;

    /// <summary>Merge the whole child section into one parent quadrant. Returns true if the parent changed.</summary>
    public static bool DownsampleIntoParent(LodSection child, LodSection parent, int qx, int qz)
    {
        const int gs = LodSection.GridSize;
        const int half = gs / 2;

        // Child palette id → parent palette id, registered lazily.
        var paletteMap = new int[child.Palette.Count];
        for (int i = 0; i < paletteMap.Length; i++) paletteMap[i] = -1;

        var batch = new ulong[]?[gs * gs];
        var mergedCols = new ulong[4][];

        for (int pz = 0; pz < half; pz++)
        {
            for (int px = 0; px < half; px++)
            {
                int captured = 0;
                for (int dz = 0; dz < 2; dz++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int ci = LodSection.ColumnIndex(px * 2 + dx, pz * 2 + dz);
                        if (!child.Captured[ci]) continue;
                        mergedCols[captured++] = child.ColumnRuns(ci).ToArray();
                    }
                }
                if (captured == 0) continue;

                ulong[] merged = MergeColumns(mergedCols, captured);

                // Remap child palette ids to the parent palette.
                for (int i = 0; i < merged.Length; i++)
                {
                    int cpid = LodSection.RunPaletteId(merged[i]);
                    int ppid = paletteMap[cpid];
                    if (ppid < 0)
                    {
                        LodPaletteEntry e = child.Palette[cpid];
                        ppid = parent.FindOrAddPaletteEntry(e.BlockId, e.Color, e.Flags);
                        paletteMap[cpid] = ppid;
                    }
                    merged[i] = LodSection.PackRun(ppid, LodSection.RunYTop(merged[i]), LodSection.RunYBottom(merged[i]));
                }

                batch[LodSection.ColumnIndex(qx * half + px, qz * half + pz)] = merged;
            }
        }

        return parent.ReplaceColumns(batch);
    }

    /// <summary>Merge up to four columns' runs into one, majority-occupancy per slice.</summary>
    static ulong[] MergeColumns(ulong[][] cols, int count)
    {
        var bounds = boundaries ??= new List<int>(64);
        bounds.Clear();

        for (int c = 0; c < count; c++)
        {
            foreach (ulong run in cols[c])
            {
                bounds.Add(LodSection.RunYTop(run));
                bounds.Add(LodSection.RunYBottom(run));
            }
        }
        if (bounds.Count == 0) return Array.Empty<ulong>();

        bounds.Sort();
        // Deduplicate in place, then walk slices top-down.
        int uniqueCount = 0;
        for (int i = 0; i < bounds.Count; i++)
        {
            if (uniqueCount == 0 || bounds[uniqueCount - 1] != bounds[i]) bounds[uniqueCount++] = bounds[i];
        }

        var result = outRuns ??= new List<ulong>(16);
        result.Clear();

        int majority = count >= 2 ? 2 : 1;

        for (int i = uniqueCount - 1; i > 0; i--)
        {
            int sliceTop = bounds[i];
            int sliceBottom = bounds[i - 1];
            int mid = (sliceTop + sliceBottom) / 2;

            // Which columns cover this slice, and with what block?
            int covering = 0;
            int bestPid = -1;
            int bestPidCount = 0;
            for (int c = 0; c < count; c++)
            {
                foreach (ulong run in cols[c])
                {
                    if (LodSection.RunYBottom(run) <= mid && mid < LodSection.RunYTop(run))
                    {
                        covering++;
                        int pid = LodSection.RunPaletteId(run);
                        // Cheap mode estimate over ≤4 values (Boyer–Moore style).
                        if (bestPidCount == 0) { bestPid = pid; bestPidCount = 1; }
                        else if (pid == bestPid) bestPidCount++;
                        else bestPidCount--;
                        break;
                    }
                }
            }

            if (covering < majority) continue;

            // Merge with the previous run when contiguous and same block.
            if (result.Count > 0)
            {
                ulong prev = result[^1];
                if (LodSection.RunYBottom(prev) == sliceTop && LodSection.RunPaletteId(prev) == bestPid)
                {
                    result[^1] = LodSection.PackRun(bestPid, LodSection.RunYTop(prev), sliceBottom);
                    continue;
                }
            }
            result.Add(LodSection.PackRun(bestPid, sliceTop, sliceBottom));
        }

        return result.ToArray();
    }
}
