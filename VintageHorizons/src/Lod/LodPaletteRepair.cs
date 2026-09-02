namespace VintageHorizons;

/// <summary>
/// Filling in palette colours that are not there.
///
/// A capturing server has no texture atlas, so it stores 0 for every colour and the
/// receiving client fills them in (DESIGN.md §10.4). A client also PERSISTS what it
/// received, so anything that stopped the fill-in from happening was written to disk and
/// stayed there. That happened: a block code that failed to resolve used to be cached as
/// a failure for the rest of the session, so once a common code lost that race, every
/// foreign section installed afterwards was saved with its colours still at zero. Measured
/// on a real cache afterwards - 7 sections entirely uncoloured and 59 partly, on ordinary
/// ground like soil-low-normal, rock-slate and tallgrass-medium-free.
///
/// Zero is a safe sentinel for "never coloured". It is fully transparent black, which no
/// real block averages to, and it is exactly what the writer stores when it cannot answer.
///
/// Fixing the cause only helps caches that do not exist yet, so this runs over every
/// section as it loads and repairs what is already on disk.
/// </summary>
public static class LodPaletteRepair
{
    /// <summary>
    /// Never coloured, or coloured with a placeholder. Colour 0 is what a writer leaves
    /// when it cannot answer at all. Near-white is what an entry keeps when its colour
    /// texture never resolved. The game answers with the unknown.png average, which
    /// measures 0xFCFCFC, and HD packs land nearby. The scanner found 31 such entries on
    /// one berry bush in a real cache. The channel order here is the cache order: red in
    /// the low byte.
    /// </summary>
    public static bool NeedsColor(int color)
    {
        if (color == 0) return true;
        int r = color & 0xFF;
        int g = (color >> 8) & 0xFF;
        int b = (color >> 16) & 0xFF;
        return r >= 0xF0 && g >= 0xF0 && b >= 0xF0;
    }

    /// <summary>
    /// Give every uncoloured entry a colour, and report how many were fixed.
    /// <paramref name="colorOf"/> takes a block id and returns a packed colour; it is the
    /// texture atlas on a client, and the reason this takes a delegate at all is that the
    /// atlas cannot exist in a headless check.
    /// </summary>
    public static int Fill(LodSection section, System.Func<int, int> colorOf)
    {
        int repaired = 0;

        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry entry = section.Palette[i];
            if (!NeedsColor(entry.Color)) continue;

            int color = colorOf(entry.BlockId);

            // A colour provider that itself answers 0 would leave the entry exactly as it
            // was, and this would run again on the next load, every load, for ever. Take
            // the grey instead: it is wrong, but it is finished.
            entry.Color = NeedsColor(color) ? UnknownBlockColor : color;
            section.Palette[i] = entry;
            repaired++;
        }

        return repaired;
    }

    /// <summary>
    /// What a block nothing can identify is drawn as: a mid grey that reads as
    /// unremarkable stone at distance. Deliberately not black, which is what these
    /// entries were by omission, and deliberately not magenta, which would shout about
    /// something the player cannot act on. The log carries the detail.
    /// </summary>
    public const int UnknownBlockColor = unchecked((int)0xFF8C8C8C);
}
