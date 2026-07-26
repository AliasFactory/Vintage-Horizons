using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// How a block is drawn, or whether it is drawn at all. Shared by both sides: a section
/// captured by a server and one captured by a client have to agree about what counts as
/// terrain, or the same ground would come out differently depending on who saw it first.
///
/// Which tint applies is a separate question, answered client-side by LodTintRegistry.
/// </summary>
public static class LodBlockPolicy
{
    public static byte FlagsFor(Block block)
    {
        if (block.BlockMaterial is EnumBlockMaterial.Water or EnumBlockMaterial.Lava or EnumBlockMaterial.Ice)
        {
            return LodPaletteEntry.FlagWater;
        }

        // Sparse, mostly-transparent ground cover. As solid LOD cubes these come out as
        // pale grey blobs, because their textures average toward the transparent pixels.
        // Not all of EnumBlockMaterial.Plant: skipping every plant was tried and
        // flattened the landscape, and dense cover like grass reads fine as solid colour.
        // The Plant guard also keeps ferntree (material Wood, an actual tree) opaque.
        if (block.BlockMaterial == EnumBlockMaterial.Plant && IsThinGroundCover(block))
        {
            return LodPaletteEntry.FlagThin;
        }

        // Not terrain at all, so it never becomes geometry.
        if (block.BlockMaterial is EnumBlockMaterial.Fire or EnumBlockMaterial.Meta)
        {
            return LodPaletteEntry.FlagSkip;
        }

        return 0;
    }

    static readonly string[] ThinGroundCoverPrefixes = { "flower", "fern", "tallfern" };

    static bool IsThinGroundCover(Block block)
    {
        string? path = block.Code?.Path;
        if (path == null) return false;

        foreach (string prefix in ThinGroundCoverPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
