namespace VintageHorizons;

/// <summary>
/// The rule that moves the game's edge fade past the far edge, by doubling the number its
/// shaders divide by.
///
/// The game's chunk shaders fade terrain to the fog color as the terrain nears the view
/// distance. chunkopaque.vsh line 95 computes vertex alpha as
/// 17 - 20 * length(worldPos.xz) / viewDistance, and the topsoil, transparent, liquid and
/// liquid-depth passes use the same formula. With LOD terrain behind that edge, the fade
/// turns a clean boundary into a blue band. Two field reports named it, and the DeFog mod
/// exists only to remove it.
///
/// A write of double the real view distance moves the whole fade past everything the game
/// draws. At the true edge the opaque formula becomes 17 - 20 * (1/2) = 7, which clamps to
/// fully opaque. DeFog writes that value every frame. This mod writes it only while it
/// draws LOD terrain, because with nothing behind the boundary the fade hides the bare
/// chunk edges, and a write there shows a wall of them instead.
/// </summary>
public static class VanillaEdgeFade
{
    /// <summary>
    /// The value to write over the game's viewDistance uniform, or null to make no change.
    ///
    /// <paramref name="lodSectionsDrawn"/> is the renderer's draw count from the last
    /// completed frame, counted after the frustum cull. It covers the whole frame and not
    /// one direction: if the renderer draws any LOD section anywhere in view, this removes
    /// the fade everywhere in that frame.
    ///
    /// A value larger than the game's own is always safe, because it only weakens the
    /// fade. A smaller one is not, because it pulls the fade nearer than the game intended.
    /// So the caller must pass the largest view distance it can find, never the smallest.
    /// </summary>
    public static float? ReplacementViewDistance(bool enabled, int lodSectionsDrawn, float vanillaViewDistance)
    {
        if (!enabled) return null;
        if (lodSectionsDrawn <= 0) return null;
        if (!float.IsFinite(vanillaViewDistance) || vanillaViewDistance <= 0f) return null;
        return vanillaViewDistance * 2f;
    }
}
