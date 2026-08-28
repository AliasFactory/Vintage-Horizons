using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// The code that installs and drives <see cref="VanillaEdgeFade"/>.
///
/// A Harmony postfix on ShaderProgramBase.Use() rewrites the viewDistance uniform of the
/// four visible chunk passes. It runs immediately after the game binds each program. The
/// game refills its uniforms on every bind, so the moment after Use() is the only moment
/// the new value survives. The liquid-depth pass is the exception: ChunkRenderer assigns
/// its ViewDistance property after Use(), so this intercepts that assignment instead.
///
/// The DeFog mod patches the same two places. Its repository has no license file, so this
/// was written from the game's own shaders, not copied from DeFog.
///
/// This is the mod's one Harmony instance, and it holds two patches. Every part of it can
/// fail with no change to the rest of the mod. A failed installation writes one warning
/// and leaves the game's fade as it was. A uniform write that fails disables its own pass
/// and no other.
/// </summary>
public static class VanillaFadePatch
{
    const string HarmonyId = "vintagehorizons.edgefade";
    const string ViewDistanceUniform = "viewDistance";

    static Harmony? harmony;
    static ILogger? logger;
    static volatile bool enabled;
    static volatile int drawnSections;
    static volatile float rendererViewDistance;

    // Only the render thread touches this. The same is true of every read and write of a
    // shader program.
    static int retiredMask;

    /// <summary>
    /// When off, the postfix reads two fields and writes no uniform. The patches stay
    /// installed, because Harmony cannot remove and reinstall them safely while the render
    /// thread is inside Use().
    /// </summary>
    public static void SetEnabled(bool value) => enabled = value;

    /// <summary>
    /// Publishes one frame of renderer state. The draw count is the gate; see
    /// <see cref="VanillaEdgeFade.ReplacementViewDistance"/> for why both it and the view
    /// distance are one frame old, and why the larger view distance is the safe one.
    /// </summary>
    public static void PublishFrame(int drawnCount, float viewDistance)
    {
        drawnSections = drawnCount;
        rendererViewDistance = viewDistance;
    }

    /// <summary>
    /// Applies the two patches. False means the game build no longer has the expected
    /// methods. The caller then runs without this feature and writes one warning.
    /// </summary>
    public static bool Install(ILogger log)
    {
        logger = log;
        retiredMask = 0;

        MethodInfo? use = typeof(ShaderProgramBase).GetMethod(
            nameof(ShaderProgramBase.Use),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            binder: null, Type.EmptyTypes, modifiers: null);
        MethodInfo? liquidDepthSetter = typeof(ShaderProgramChunkliquiddepth)
            .GetProperty(nameof(ShaderProgramChunkliquiddepth.ViewDistance))?.SetMethod;

        // If Use() is virtual, the runtime dispatches subclass overrides around the patch.
        // The postfix then runs for some passes and not others, which looks worse than no
        // postfix at all: the band goes on part of the terrain and stays on the rest.
        if (use == null || liquidDepthSetter == null || (use.IsVirtual && !use.IsFinal))
        {
            return false;
        }

        harmony = new Harmony(HarmonyId);
        harmony.Patch(use, postfix: new HarmonyMethod(typeof(VanillaFadePatch), nameof(UsePostfix)));
        harmony.Patch(liquidDepthSetter, prefix: new HarmonyMethod(typeof(VanillaFadePatch), nameof(LiquidDepthPrefix)));
        return true;
    }

    public static void Uninstall()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        enabled = false;
        drawnSections = 0;
        rendererViewDistance = 0f;
        logger = null;
    }

    /// <summary>One bit per pass, so a failure disables one pass and keeps the others.</summary>
    static int PassBit(string passName) => passName switch
    {
        "chunkopaque" => 0,
        "chunktopsoil" => 1,
        "chunktransparent" => 2,
        "chunkliquid" => 3,
        _ => -1,
    };

    /// <summary>
    /// The largest view distance this mod can name. ClientSettings holds what the player
    /// asked for; the renderer holds that value capped by what the server approved. The
    /// larger of the two is the safe one to double, and which of them the game itself
    /// wrote into the uniform then does not matter.
    /// </summary>
    static float SafeViewDistance() => Math.Max(ClientSettings.ViewDistance, rendererViewDistance);

    public static void UsePostfix(ShaderProgramBase __instance)
    {
        string? pass = __instance.PassName;
        if (pass == null) return;
        int bit = PassBit(pass);
        if (bit < 0 || (retiredMask & (1 << bit)) != 0) return;

        float? replacement = VanillaEdgeFade.ReplacementViewDistance(
            enabled, drawnSections, SafeViewDistance());
        if (replacement == null) return;

        try
        {
            if (!__instance.HasUniform(ViewDistanceUniform))
            {
                Retire(bit, pass, "the pass has no viewDistance uniform");
                return;
            }
            __instance.Uniform(ViewDistanceUniform, replacement.Value);
        }
        catch (Exception e)
        {
            Retire(bit, pass, e.Message);
        }
    }

    /// <summary>
    /// This patch has nothing to disable: the new value goes into the game's own
    /// assignment, and no uniform write of this mod's can fail here.
    /// </summary>
    public static void LiquidDepthPrefix(ref float __0)
    {
        float? replacement = VanillaEdgeFade.ReplacementViewDistance(
            enabled, drawnSections, Math.Max(__0, SafeViewDistance()));
        if (replacement != null) __0 = replacement.Value;
    }

    static void Retire(int bit, string pass, string reason)
    {
        retiredMask |= 1 << bit;
        logger?.Warning(
            "VintageHorizons no longer removes the edge fade for the '{0}' pass: {1}. "
            + "The game's edge fade remains for that pass.",
            pass, reason);
    }
}
