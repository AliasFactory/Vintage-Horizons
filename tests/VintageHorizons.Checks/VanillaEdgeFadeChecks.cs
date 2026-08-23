namespace VintageHorizons.Checks;

/// <summary>
/// The gate that controls the write to the game's viewDistance uniform. The two failure
/// directions are not the same size. An unwanted write removes the fade in front of bare
/// chunk edges, which players see. A missing write only keeps the vanilla look. Every rule
/// here must prefer the second.
/// </summary>
public static class VanillaEdgeFadeChecks
{
    public static void Run(Check c)
    {
        c.Eq(2048f, VanillaEdgeFade.ReplacementViewDistance(true, 1, 1024f),
            "doubles the view distance, so every fade formula saturates at the real edge");
        c.Eq(null, VanillaEdgeFade.ReplacementViewDistance(false, 500, 1024f),
            "when off, the mod writes nothing");
        c.Eq(null, VanillaEdgeFade.ReplacementViewDistance(true, 0, 1024f),
            "no LOD terrain drawn keeps the fade: it hides a bare chunk edge");
        c.Eq(null, VanillaEdgeFade.ReplacementViewDistance(true, -1, 1024f),
            "a negative draw count keeps the fade too");
        c.Eq(null, VanillaEdgeFade.ReplacementViewDistance(true, 1, 0f),
            "a zero view distance writes nothing");
        c.Eq(null, VanillaEdgeFade.ReplacementViewDistance(true, 1, -512f),
            "a negative view distance writes nothing");
        c.Eq(null, VanillaEdgeFade.ReplacementViewDistance(true, 1, float.NaN),
            "NaN writes nothing");
        c.Eq(null, VanillaEdgeFade.ReplacementViewDistance(true, 1, float.PositiveInfinity),
            "infinity writes nothing");

        SafeDirection(c);
    }

    /// <summary>
    /// chunkopaque.vsh line 95 divides by this uniform: alpha is
    /// 17 - 20 * distance / viewDistance. A larger value therefore weakens the fade and a
    /// smaller one pulls it nearer than the game intended. The caller picks the largest
    /// view distance it can name for exactly this reason, so the result must never fall
    /// when the input rises.
    /// </summary>
    static void SafeDirection(Check c)
    {
        float previous = 0f;
        foreach (float distance in new[] { 32f, 128f, 512f, 1024f, 4096f })
        {
            float got = VanillaEdgeFade.ReplacementViewDistance(true, 1, distance) ?? 0f;
            c.True(got > previous, $"a larger view distance gives a larger result at {distance}");
            c.True(got >= distance, $"the result never falls below the game's own value at {distance}");
            previous = got;
        }
    }
}
