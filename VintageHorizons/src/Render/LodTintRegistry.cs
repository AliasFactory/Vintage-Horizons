using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// Maps each block to a tint SLOT, and keeps the live color of each slot current.
///
/// Vintage Story has no single tint for foliage. Leaves take a season map for each species,
/// such as seasonalOak, seasonalNeedles, seasonalBirch or seasonalMaple. That map goes on top
/// of one of several climate maps. Water has its own climateWaterTint.
///
/// One "foliage" tint for all of that gave each leaf in the LOD the map of the block that the
/// registry scan found first. That block was a conifer, thus nothing changed color in autumn.
/// Water also stayed grey, with no tint.
///
/// A slot is one distinct pair of a climate map and a season map. The captured color keeps no
/// tint. The mod calculates the color of a slot again from the color maps of the game, every
/// few seconds. Thus distant terrain follows the calendar, and the mod captures nothing
/// again.
///
/// The mod calculates a slot from the live Block, and it never stores a slot. Thus a cache
/// that exists already gets the correct tint for each species, and a player does not explore
/// again. The map also stays correct when a game update or a mod update changes the map that
/// a block uses.
/// </summary>
public class LodTintRegistry
{
    /// <summary>Slot 0 is the tint that changes nothing. Each block with no color map uses
    /// it.</summary>
    public const int SlotNone = 0;

    /// <summary>
    /// This value is small on purpose. The alpha byte carries the slot, and the shader holds
    /// one vec3 for each slot. A value of 64 covers each map pair in the base game, and it
    /// leaves space.
    /// </summary>
    public const int MaxSlots = 64;

    // MaxSlots is also written as `const int TINT_SLOTS` in lodterrain.vsh and
    // lodterrain.fsh, because this version of the game cannot inject a #define.
    //
    // A second C# constant held a copy of that number, maintained by hand. The mod compared
    // it against MaxSlots at shader load. But a comparison of two constants in one file
    // cannot find an edit to a shader. The compiler said so, and it marked the branch as
    // unreachable.
    //
    // The real check reads the shader files. Read StaticAssetChecks, in the fast tier of
    // scripts/check.sh.

    readonly Dictionary<(string?, string?), int> slotByMaps = new();
    readonly List<Block?> representative = new();

    // There is one vec4 for each slot, because the upload path for a uniform takes 4
    // components for each element.
    //
    // There are two samples of altitude for each slot. The climate maps use the temperature
    // as their index, and the temperature decreases with the height. This is the same lapse
    // rate that the snow line uses.
    //
    // One sample at the feet of the player gave a mountain top the green of a valley. The
    // real grass up there is colder and more red. The shader interpolates between the two
    // samples, by the height of the vertex.
    readonly float[] tintsLow = new float[MaxSlots * 4];
    readonly float[] tintsHigh = new float[MaxSlots * 4];

    /// <summary>Refresh increases this value. Thus the renderer can skip an upload of tints
    /// that did not change.</summary>
    public int Version { get; private set; }
    public float[] TintsLow => tintsLow;
    public float[] TintsHigh => tintsHigh;

    /// <summary>The world Y at which the mod sampled the two tint tables.</summary>
    public float SampleYLow { get; private set; }
    public float SampleYHigh { get; private set; }

    public LodTintRegistry()
    {
        representative.Add(null);              // slot 0: no tint
        slotByMaps[(null, null)] = SlotNone;
        for (int i = 0; i < tintsLow.Length; i++) tintsLow[i] = tintsHigh[i] = 1f;
    }

    /// <summary>
    /// A block that carries climatePlantTint. Plants that declare no color map of their own
    /// use it.
    ///
    /// A fern is the case that made this necessary. The textures of a fern are greyscale, and
    /// the stored color is exactly RGB 148,148,148. Vanilla makes it green from its block
    /// class, and not from JSON. Thus an LOD cube with no tint was grey.
    /// </summary>
    public Block? PlantTintFallback;

    /// <summary>The slot for this block. The mod registers a new slot when it did not see
    /// this map pair before.</summary>
    public int SlotFor(Block? block)
    {
        if (block == null) return SlotNone;

        string? climate = block.ClimateColorMapResolved != null ? block.ClimateColorMap : null;
        string? season = block.SeasonColorMapResolved != null ? block.SeasonColorMap : null;

        if (climate == null && season == null)
        {
            return block.BlockMaterial == EnumBlockMaterial.Plant && PlantTintFallback != null
                ? SlotFor(PlantTintFallback)
                : SlotNone;
        }

        var key = (climate, season);
        if (slotByMaps.TryGetValue(key, out int slot)) return slot;

        if (representative.Count >= MaxSlots) return SlotNone; // out of slots: untinted beats wrong
        slot = representative.Count;
        representative.Add(block);
        slotByMaps[key] = slot;
        return slot;
    }

    /// <summary>
    /// Calculate the color of each slot again, for the current season and climate. The mod
    /// applies the color maps of the game to white, at the given position.
    /// </summary>
    public void Refresh(IClientWorldAccessor world, int x, int z)
    {
        // Cover the range of heights that the terrain around the viewer occupies. Thus the
        // interpolation goes from the valley floor to the peak, and it does not go past the
        // samples.
        Version++;
        SampleYLow = world.SeaLevel;
        SampleYHigh = world.SeaLevel + 320;

        for (int slot = 1; slot < representative.Count; slot++)
        {
            Block? block = representative[slot];
            if (block == null) continue;

            Sample(world, block, x, (int)SampleYLow, z, tintsLow, slot);
            Sample(world, block, x, (int)SampleYHigh, z, tintsHigh, slot);
        }
    }

    static void Sample(IClientWorldAccessor world, Block block, int x, int y, int z, float[] into, int slot)
    {
        int rgba = world.ApplyColorMapOnRgba(
            block.ClimateColorMapResolved, block.SeasonColorMapResolved,
            unchecked((int)0xFFFFFFFF), x, y, z);

        // ApplyColorMapOnRgba exchanges red and blue by default. Thus red is the high byte.
        // ColorUtil.ToRGBAFloats unpacks exactly that. This code uses it, and it does not
        // state the channel order of the engine again.
        //
        // ToRGBAFloats[0] is the HIGH byte, and ApplyColorMapOnRgba puts red there. A
        // connection from [2] to red exchanged R and B, and each grass tint became teal.
        float[] rgbaf = Vintagestory.API.MathTools.ColorUtil.ToRGBAFloats(rgba);
        into[slot * 4 + 0] = rgbaf[0];
        into[slot * 4 + 1] = rgbaf[1];
        into[slot * 4 + 2] = rgbaf[2];
        into[slot * 4 + 3] = 1f;
    }
}
