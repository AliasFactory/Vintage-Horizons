using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// Maps each block to a tint SLOT, and keeps every slot's live colour up to date.
///
/// Vintage Story does not have one foliage tint: leaves pick a seasonal map per
/// species (seasonalOak, seasonalNeedles, seasonalBirch, seasonalMaple, ...) on top of
/// one of several climate maps, and water has its own climateWaterTint. Collapsing all
/// of that into a single "foliage" tint meant every leaf in the LOD took whichever
/// block the registry scan happened to hit first - a conifer, so nothing ever turned
/// for autumn - and water was left untinted grey.
///
/// A slot is one distinct (climate map, season map) pair. The captured colour stays
/// untinted and the slot's colour is recomputed from the game's own colour maps every
/// few seconds, so distant terrain follows the calendar without re-capturing anything.
///
/// Slots are derived from the live Block, never persisted: an existing cache picks up
/// correct per-species tints with no re-exploration, and the mapping stays right if a
/// game or mod update changes which map a block uses.
/// </summary>
public class LodTintRegistry
{
    /// <summary>Slot 0 is the identity tint, used by everything with no colour map.</summary>
    public const int SlotNone = 0;

    /// <summary>
    /// Kept small on purpose: the alpha byte carries the slot, and the shader holds one
    /// vec3 per slot. 64 covers every map pair in the base game with room to spare.
    /// </summary>
    public const int MaxSlots = 64;

    // MaxSlots is also hardcoded as `const int TINT_SLOTS` in lodterrain.vsh/.fsh, because
    // this game version offers no way to inject a #define. There used to be a second C#
    // constant mirroring that number by hand, compared against MaxSlots at shader load -
    // but comparing two constants in the same file cannot detect a shader being edited,
    // and the compiler said so, flagging the branch as unreachable. The real check reads
    // the shader files: see StaticAssetChecks in the fast tier of scripts/check.sh.

    readonly Dictionary<(string?, string?), int> slotByMaps = new();
    readonly List<Block?> representative = new();

    // vec4 per slot: the uniform upload path takes 4 components per element.
    // Two altitude samples per slot, because the climate maps are indexed by
    // temperature and temperature falls with height - the same lapse rate the snow
    // line uses. Sampling once at the player's feet painted mountaintops with valley
    // green instead of the colder, redder grass that actually grows up there. The
    // shader interpolates between these by vertex height.
    readonly float[] tintsLow = new float[MaxSlots * 4];
    readonly float[] tintsHigh = new float[MaxSlots * 4];

    /// <summary>Bumped by Refresh; lets the renderer skip re-uploading unchanged tints.</summary>
    public int Version { get; private set; }
    public float[] TintsLow => tintsLow;
    public float[] TintsHigh => tintsHigh;

    /// <summary>World Y the two tint tables were sampled at.</summary>
    public float SampleYLow { get; private set; }
    public float SampleYHigh { get; private set; }

    public LodTintRegistry()
    {
        representative.Add(null);              // slot 0: no tint
        slotByMaps[(null, null)] = SlotNone;
        for (int i = 0; i < tintsLow.Length; i++) tintsLow[i] = tintsHigh[i] = 1f;
    }

    /// <summary>
    /// A block carrying climatePlantTint, used for plants that declare no colour map of
    /// their own. Ferns are the case that forced this: their textures ship greyscale
    /// (stored colour is exactly RGB 148,148,148) and vanilla greens them from its block
    /// class rather than from JSON, so an untinted LOD cube came out grey.
    /// </summary>
    public Block? PlantTintFallback;

    /// <summary>Slot for this block, registering a new one if this map pair is unseen.</summary>
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
    /// Recompute every slot's colour for the current season and climate, by applying the
    /// game's own maps to white at the given position.
    /// </summary>
    public void Refresh(IClientWorldAccessor world, int x, int z)
    {
        // Span the height range terrain actually occupies around the viewer, so the
        // interpolation covers valley floor to peak rather than extrapolating.
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

        // ApplyColorMapOnRgba flips red and blue by default, so red is the high byte -
        // which is exactly what ColorUtil.ToRGBAFloats unpacks, rather than restating
        // the engine's channel order here.
        // ToRGBAFloats[0] is the HIGH byte, which is where ApplyColorMapOnRgba puts red
        // (it flips red and blue by default). Wiring [2] to red swapped R and B and
        // turned every grass tint teal.
        float[] rgbaf = Vintagestory.API.MathTools.ColorUtil.ToRGBAFloats(rgba);
        into[slot * 4 + 0] = rgbaf[0];
        into[slot * 4 + 1] = rgbaf[1];
        into[slot * 4 + 2] = rgbaf[2];
        into[slot * 4 + 3] = 1f;
    }
}
