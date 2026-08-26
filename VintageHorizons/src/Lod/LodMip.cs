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

    /// <summary>Convenience overload for main-thread callers: snapshot the child and
    /// downsample it. The worker path uses the snapshot overload directly.</summary>
    public static bool DownsampleIntoParent(LodSection child, LodSection parent, int qx, int qz) =>
        DownsampleIntoParent(SectionSnapshot.Of(child), parent, qx, qz);

    /// <summary>Merge the whole child section into one parent quadrant. Returns true if the parent changed.</summary>
    public static bool DownsampleIntoParent(SectionSnapshot child, LodSection parent, int qx, int qz)
    {
        const int gs = LodSection.GridSize;
        const int half = gs / 2;

        // Child palette id → parent palette id, registered lazily.
        var paletteMap = new int[child.PaletteColors.Length];
        for (int i = 0; i < paletteMap.Length; i++) paletteMap[i] = -1;

        var batch = new ulong[]?[gs * gs];

        // The four child columns are described by where they sit in the child's own run
        // array, rather than copied out of it. Copying cost four allocations per parent
        // column and there are 1024 of those per call, three calls per tick, on the game
        // thread. Hoisted out of the loops: a stackalloc inside one grows the frame every
        // iteration.
        Span<int> colStart = stackalloc int[4];
        Span<int> colEnd = stackalloc int[4];

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
                        colStart[captured] = child.ColumnStart[ci];
                        colEnd[captured] = child.ColumnStart[ci + 1];
                        captured++;
                    }
                }
                if (captured == 0) continue;

                ulong[] merged = MergeColumns(child.Runs, colStart, colEnd, captured);

                // Remap child palette ids to the parent palette.
                for (int i = 0; i < merged.Length; i++)
                {
                    int cpid = LodSection.RunPaletteId(merged[i]);
                    int ppid = paletteMap[cpid];
                    if (ppid < 0)
                    {
                        ppid = parent.FindOrAddPaletteEntry(
                            child.PaletteBlockIds[cpid], child.PaletteColors[cpid],
                            child.PaletteFlags[cpid], child.PaletteTintSlots[cpid]);
                        paletteMap[cpid] = ppid;
                    }
                    merged[i] = LodSection.PackRun(ppid, LodSection.RunYTop(merged[i]), LodSection.RunYBottom(merged[i]));
                }

                batch[LodSection.ColumnIndex(qx * half + px, qz * half + pz)] = merged;
            }
        }

        return parent.ReplaceColumns(batch);
    }

    /// <summary>
    /// Merge up to four columns' runs into one, majority-occupancy per slice.
    ///
    /// The columns are given as ranges into the child section's own run array. That array
    /// is immutable once created (writes swap the whole array), which is the same property
    /// the worker snapshots rely on, so reading through it here needs no copy.
    /// </summary>
    static ulong[] MergeColumns(ulong[] runs, ReadOnlySpan<int> colStart, ReadOnlySpan<int> colEnd, int count)
    {
        var bounds = boundaries ??= new List<int>(64);
        bounds.Clear();

        // Sliced once per column and walked with foreach, not indexed with a running
        // offset: the JIT drops the bounds check on a foreach over a span, and cannot on
        // an index it is unable to prove in range.
        for (int c = 0; c < count; c++)
        {
            foreach (ulong run in runs.AsSpan(colStart[c], colEnd[c] - colStart[c]))
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
                foreach (ulong run in runs.AsSpan(colStart[c], colEnd[c] - colStart[c]))
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
