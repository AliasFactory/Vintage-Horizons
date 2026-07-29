using Vintagestory.API.Common;

namespace VintageHorizons.Checks;

/// <summary>
/// How a block is classified for LOD. Shared by both sides deliberately: a section
/// captured by a server and one captured by a client must agree about what counts as
/// terrain, or the same ground looks different depending on who saw it first.
/// </summary>
public static class PolicyChecks
{
    public static void Run(Check c)
    {
        Materials(c);
        GroundCover(c);
        FernTree(c);
        Degenerate(c);
    }

    static void Materials(Check c)
    {
        c.Eq(LodPaletteEntry.FlagWater, Flags(EnumBlockMaterial.Water, "water-still-7"), "water is translucent");
        c.Eq(LodPaletteEntry.FlagWater, Flags(EnumBlockMaterial.Lava, "lava-still-7"), "lava uses the water path");
        c.Eq(LodPaletteEntry.FlagWater, Flags(EnumBlockMaterial.Ice, "lakeice"), "ice uses the water path");

        // Not terrain at all, so it never becomes geometry.
        c.Eq(LodPaletteEntry.FlagSkip, Flags(EnumBlockMaterial.Fire, "fire"), "fire is skipped");
        c.Eq(LodPaletteEntry.FlagSkip, Flags(EnumBlockMaterial.Meta, "meta-invisible"), "meta blocks are skipped");

        c.Eq((byte)0, Flags(EnumBlockMaterial.Stone, "rock-granite"), "stone is ordinary opaque terrain");
        c.Eq((byte)0, Flags(EnumBlockMaterial.Soil, "soil-medium-normal"), "soil is ordinary opaque terrain");
        c.Eq((byte)0, Flags(EnumBlockMaterial.Wood, "log-grown-pine-ud"), "wood is ordinary opaque terrain");
    }

    /// <summary>
    /// Sparse ground cover renders as a pale grey blob when drawn as a solid cube, because
    /// its texture averages toward its transparent pixels. Not all plants qualify: skipping
    /// every plant was tried and flattened the landscape, and dense cover like grass reads
    /// fine as solid colour.
    /// </summary>
    static void GroundCover(Check c)
    {
        c.Eq(LodPaletteEntry.FlagThin, Flags(EnumBlockMaterial.Plant, "flower-forgetmenot"), "flowers are thin");
        c.Eq(LodPaletteEntry.FlagThin, Flags(EnumBlockMaterial.Plant, "fern-normal"), "ferns are thin");
        c.Eq(LodPaletteEntry.FlagThin, Flags(EnumBlockMaterial.Plant, "tallfern-normal"), "tall ferns are thin");

        c.Eq((byte)0, Flags(EnumBlockMaterial.Plant, "tallgrass-tall"), "dense grass stays solid");
        c.Eq((byte)0, Flags(EnumBlockMaterial.Plant, "seaweed-top"), "unlisted plants stay solid");
    }

    /// <summary>
    /// "fern" is a prefix of "ferntree", and a ferntree is an actual tree. The material
    /// guard is the only thing that stops the prefix match from turning every ferntree
    /// trunk into a quarter-block mat - the prefix list alone would classify it as cover.
    /// </summary>
    static void FernTree(Check c)
    {
        c.Eq((byte)0, Flags(EnumBlockMaterial.Wood, "ferntree-grown-medium"),
            "a ferntree is opaque wood, not ground cover");
        c.Eq((byte)0, Flags(EnumBlockMaterial.Wood, "fern-something-wooden"),
            "the material guard, not the prefix list, is what protects wood");

        // And the guard is genuinely load-bearing: the same code path with material Plant
        // does match the prefix. If someone removes the material check thinking the prefix
        // list is enough, this pair is what shows the difference.
        c.Eq(LodPaletteEntry.FlagThin, Flags(EnumBlockMaterial.Plant, "ferntree-grown-medium"),
            "the same code as a plant would be treated as cover");
    }

    static void Degenerate(Check c)
    {
        // Code is null on a block that failed to resolve. Classification must survive it,
        // because a modded install is exactly where this happens and exactly where the
        // purple-block reports come from.
        c.NoThrow(() => LodBlockPolicy.FlagsFor(new Block { BlockMaterial = EnumBlockMaterial.Plant }),
            "a block with no code does not throw");
        c.Eq((byte)0, LodBlockPolicy.FlagsFor(new Block { BlockMaterial = EnumBlockMaterial.Plant }),
            "a plant with no code stays solid rather than guessing");
    }

    static byte Flags(EnumBlockMaterial material, string path) =>
        LodBlockPolicy.FlagsFor(new Block
        {
            BlockMaterial = material,
            Code = new AssetLocation("game", path),
        });
}
