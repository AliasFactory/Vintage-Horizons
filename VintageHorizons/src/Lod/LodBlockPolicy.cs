using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// How the mod draws a block, or whether it draws that block at all.
///
/// Both sides use this class. A section that a server captured and a section that a client
/// captured must agree about what is terrain. Without that agreement, the same ground looks
/// different, and the result depends on which side saw it first.
///
/// The tint is a separate question. LodTintRegistry answers it, on the client.
/// </summary>
public static class LodBlockPolicy
{
    public static byte FlagsFor(Block block)
    {
        if (block.BlockMaterial is EnumBlockMaterial.Water or EnumBlockMaterial.Lava or EnumBlockMaterial.Ice)
        {
            return LodPaletteEntry.FlagWater;
        }

        // Ground cover that is sparse and mostly transparent. As solid LOD cubes, these
        // blocks look like pale grey shapes, because the average of their textures moves
        // toward the transparent pixels.
        //
        // This is not each block of EnumBlockMaterial.Plant. A test skipped each plant, and
        // that made the landscape flat. Dense cover such as grass looks correct as a solid
        // color.
        //
        // The Plant guard also keeps ferntree opaque. Its material is Wood, and it is a real
        // tree.
        if (block.BlockMaterial == EnumBlockMaterial.Plant && IsThinGroundCover(block))
        {
            return LodPaletteEntry.FlagThin;
        }

        // This block is not terrain, thus it never becomes geometry.
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
