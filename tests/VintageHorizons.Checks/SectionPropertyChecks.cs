

namespace VintageHorizons.Checks;

/// <summary>
/// Random sequences of column edits, with the structural invariants re-checked after
/// every single one.
///
/// SectionChecks covers chosen cases, and chosen cases are chosen by whoever already
/// understands the code. This suite is here for the cases nobody thought of. The reason
/// it exists at all is that the worst bug in this file was a shape nobody would pick by
/// hand: SetColumn edits Runs IN PLACE when the run count happens to be unchanged, so a
/// snapshot that borrowed the array instead of copying it saw a section mutate under it.
/// A path that only breaks when two lengths coincide is exactly what random sequences
/// find and hand-written cases miss.
///
/// The seed is fixed. A property test that varies run to run reports a failure the next
/// run cannot reproduce, which is worse than no test.
/// </summary>
public static class SectionPropertyChecks
{
    const int Seed = 20260802;
    const int Rounds = 400;

    public static void Run(Check c)
    {
        RandomEditsKeepTheInvariants(c);
        InPlaceEditsDoNotEscape(c);
    }

    /// <summary>
    /// The four things that must be true of a section after any edit, whatever the edit
    /// was. Returns the first violation, or null.
    /// </summary>
    static string? Violation(LodSection section)
    {
        int total = LodSection.GridSize * LodSection.GridSize;

        if (section.ColumnStart.Length != total + 1) return "ColumnStart lost its length";
        if (section.ColumnStart[0] != 0) return "ColumnStart does not begin at 0";

        // Offsets rise, never fall. A fall means one column's span overlaps the previous.
        for (int col = 0; col < total; col++)
        {
            if (section.ColumnStart[col + 1] < section.ColumnStart[col])
            {
                return $"ColumnStart goes backwards at column {col}";
            }
        }

        // The prefix offsets must account for every run and no more. A mismatch here is
        // how a section starts reading a neighbouring column's data.
        if (section.ColumnStart[total] != section.Runs.Length)
        {
            return $"ColumnStart ends at {section.ColumnStart[total]} but Runs holds {section.Runs.Length}";
        }

        // Every run must decode to a span that goes upward. yTop <= yBottom means the
        // pack lost a field boundary.
        for (int i = 0; i < section.Runs.Length; i++)
        {
            ulong run = section.Runs[i];
            if (LodSection.RunYTop(run) <= LodSection.RunYBottom(run))
            {
                return $"run {i} has yTop <= yBottom";
            }
        }

        // CapturedColumns must match the Captured flags it counts.
        int flagged = 0;
        for (int col = 0; col < total; col++) if (section.Captured[col]) flagged++;
        if (flagged != section.CapturedColumns)
        {
            return $"CapturedColumns says {section.CapturedColumns}, flags say {flagged}";
        }

        return null;
    }

    static ulong[] RandomRuns(Random rng)
    {
        int count = rng.Next(0, 5);
        var runs = new ulong[count];
        int y = 1 + rng.Next(0, 40);
        for (int i = 0; i < count; i++)
        {
            int bottom = y;
            int top = bottom + 1 + rng.Next(0, 6);
            y = top;
            runs[i] = LodSection.PackRun(1 + rng.Next(0, 30), top, bottom);
        }
        return runs;
    }

    static void RandomEditsKeepTheInvariants(Check c)
    {
        var rng = new Random(Seed);
        var section = new LodSection();
        int total = LodSection.GridSize * LodSection.GridSize;

        string? firstBreak = null;
        string? breakAfter = null;

        for (int round = 0; round < Rounds && firstBreak == null; round++)
        {
            string what;
            switch (rng.Next(0, 3))
            {
                case 0:
                {
                    int col = rng.Next(0, total);
                    section.SetColumn(col, RandomRuns(rng));
                    what = $"SetColumn({col})";
                    break;
                }
                case 1:
                {
                    // A batch, with holes: a null entry must leave that column alone.
                    var batch = new ulong[]?[total];
                    int touched = 0;
                    for (int col = 0; col < total; col++)
                    {
                        if (rng.Next(0, 40) != 0) continue;
                        batch[col] = RandomRuns(rng);
                        touched++;
                    }
                    section.ReplaceColumns(batch);
                    what = $"ReplaceColumns({touched} columns)";
                    break;
                }
                default:
                {
                    byte flag = (byte)(1 << rng.Next(0, 3));
                    section.RemoveRunsWithFlag(flag);
                    what = $"RemoveRunsWithFlag({flag})";
                    break;
                }
            }

            firstBreak = Violation(section);
            if (firstBreak != null) breakAfter = $"round {round}, after {what}";
        }

        c.Eq(null, firstBreak, $"{Rounds} random edits keep every structural invariant"
            + (breakAfter == null ? "" : $" (broke at {breakAfter})"));

        // The section must still be readable after all that, not merely well-formed.
        c.NoThrow(() =>
        {
            for (int col = 0; col < total; col++) _ = section.ColumnRuns(col).Length;
        }, "every column is still readable after the random edits");
    }

    /// <summary>
    /// The in-place path, pinned directly. When a replacement has the same run count,
    /// SetColumn writes into the existing array rather than allocating a new one - so
    /// anything that kept a reference to Runs sees the change. That is correct and
    /// deliberate for speed, and it is also the trap: a save snapshot must COPY.
    /// </summary>
    static void InPlaceEditsDoNotEscape(Check c)
    {
        var section = new LodSection();
        var first = new[] { LodSection.PackRun(3, 10, 4) };
        section.SetColumn(0, first);

        ulong[] borrowed = section.Runs;
        var sameLength = new[] { LodSection.PackRun(9, 20, 12) };
        c.True(section.SetColumn(0, sameLength), "a differing same-length column reports a change");
        c.True(ReferenceEquals(borrowed, section.Runs),
            "a same-length edit reuses the array, which is why a snapshot must copy it");
        c.Eq(sameLength[0], borrowed[0], "the borrowed array saw the edit, as the hazard describes");
        c.Eq(null, Violation(section), "a same-length edit keeps the invariants");

        // A different length must rebuild instead, leaving any borrowed array behind.
        ulong[] beforeGrow = section.Runs;
        section.SetColumn(0, new[] { LodSection.PackRun(1, 6, 2), LodSection.PackRun(2, 14, 6) });
        c.False(ReferenceEquals(beforeGrow, section.Runs), "a length change rebuilds the array");
        c.Eq(null, Violation(section), "a length change keeps the invariants");

        // Writing the identical content again must report no change, or the capture path
        // marks a section dirty every tick and rewrites it to disk forever.
        c.False(section.SetColumn(0, section.ColumnRuns(0).ToArray()),
            "rewriting identical content reports no change");
    }
}
