using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VintageHorizons.Net;

namespace VintageHorizons;

public class VintageHorizonsConfig
{
    /// <summary>0 = unlimited.</summary>
    public int FarViewDistanceCap = 0;

    /// <summary>Distance at which detail starts halving; see LodWorld.DetailDistance.</summary>
    public int DetailDistance = 512;

    /// <summary>
    /// Draw even when another LOD mod is installed and switched on. The escape hatch for
    /// the mods whose own switch we cannot read; see <see cref="OtherLodMods"/>. Read at
    /// startup only, because turning the mod on mid-session is not something the startup
    /// path supports.
    /// </summary>
    public bool IgnoreOtherLodMods = false;

    /// <summary>
    /// Remove the game's edge fade at the view distance while LOD terrain draws behind it.
    /// In a frame where the mod draws no LOD terrain, the fade returns. See
    /// <see cref="VanillaEdgeFade"/>.
    /// </summary>
    public bool RemoveVanillaEdgeFade = true;
}

/// <summary>
/// Client entry point: wires the shared <see cref="LodPipeline"/> to client events and
/// owns everything the server has no equivalent of - the renderer, tint registry, chat
/// commands and telemetry. Chunk columns arrive from `ChunkDirty` and go straight to the
/// pipeline, which does the capture, mip and persistence work.
/// See DESIGN.md at the repo root.
/// </summary>
public class VintageHorizonsModSystem : ModSystem
{

    ICoreClientAPI capi = null!;
    LodPipeline pipeline = null!;
    LodTerrainRenderer renderer = null!;

    /// <summary>Block -> live tint slot; shared by capture, cache loads and the renderer.</summary>
    readonly LodTintRegistry tints = new();
    long tickListenerId;

    readonly BlockPos paletteSamplePos = new(0, 0, 0);

    // Dev auto-explore (unattended stress testing): teleport along an expanding
    // spiral so fresh chunks stream through the pipeline continuously.
    bool autoExplore;
    int exploreLeg;      // spiral leg counter
    int exploreStep;     // steps taken on current leg
    int exploreDirX = 1, exploreDirZ;
    double exploreX, exploreZ;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    /// <summary>Everything the config file holds, so a partial save cannot drop a setting.</summary>
    VintageHorizonsConfig config = new();

    /// <summary>Set when another LOD mod is drawing; we then stay out of its way.</summary>
    string? deferringTo;

    /// <summary>Why the edge-fade patch is not active, or null when it is installed.</summary>
    string? edgeFadeBlocked;

    /// <summary>
    /// Optional server assist (DESIGN.md §10). Created even while deferring, because the
    /// channel has to be registered before the handshake either way; it simply never
    /// greets, so it stays silent.
    /// </summary>
    LodAssistClient? assist;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        try
        {
            config = capi.LoadModConfig<VintageHorizonsConfig>("vintagehorizons.json") ?? new VintageHorizonsConfig();
        }
        catch
        {
            config = new VintageHorizonsConfig();
        }

        // Before anything that can return early, and before the connection handshake:
        // the server learns we speak this channel at handshake time and never again. A
        // client that skips the registration makes the game log "Server sends me channel
        // name vintagehorizons, but no client side mod registered it" on every join,
        // which reads like a broken install. The channel stays inert until Greet(), and
        // only the active path reaches that.
        assist = new LodAssistClient(capi, Mod.Logger, Mod.Info.Version);
        assist.Register();

        // Two LOD mods both extend the camera's far plane and both draw terrain out
        // there, so they z-fight over the same ground. Defer rather than fight. What we
        // must not do is defer to a mod that is installed but switched off, which leaves
        // the player with nothing at all.
        deferringTo = ChooseDeferralTarget();
        if (deferringTo != null)
        {
            Mod.Logger.Notification(
                "'{0}' is installed and switched on, so VintageHorizons stays idle. Two LOD mods "
                + "draw over each other, because both set the camera far plane. Switch '{0}' off in its "
                + "own settings, then restart the game to use VintageHorizons instead. The mod "
                + "decides this once at startup: if you switch the other mod off now, nothing "
                + "changes until the next start.",
                deferringTo);
            RegisterCommands();

            // The idle path reaches no LevelFinalize handler of its own, so the dev hook
            // is wired straight to the event here. Without it the matrix cannot drive
            // '.vhdefer off' from the state that command exists for.
            capi.Event.LevelFinalize += RegisterAutoCommand;
            return;
        }

        LodWorld.DetailDistance = GameMath.Clamp(config.DetailDistance,
            (int)LodWorld.MinDetailDistance, (int)LodWorld.MaxDetailDistance);

        pipeline = new LodPipeline(capi, Mod.Logger, DescribePalette, block => (byte)tints.SlotFor(block));

        // Repairs a cache written while a block lookup was poisoned, which saved sections
        // with no palette colour at all and drew them as black ground. Client-side only:
        // it needs the texture atlas, and a server stores 0 on purpose.
        pipeline.RepairUncoloredPalette = section => LodPaletteRepair.Fill(section, AtlasColorOf);
        renderer = new LodTerrainRenderer(capi, pipeline.World, pipeline.Worker, tints)
        {
            AutoUnpause = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOUNPAUSE") == "1",
            FarViewDistanceCap = config.FarViewDistanceCap,
        };

        // The game fades terrain to the fog color as the terrain nears the view distance.
        // That fade turns the boundary in front of LOD terrain into a blue band. See
        // VanillaEdgeFade for the rule and the shader evidence. DeFog writes the same
        // uniform with no gate, so when DeFog is present this mod installs no patch and
        // the two never write the same value twice a frame.
        if (capi.ModLoader.IsModEnabled("defog"))
        {
            edgeFadeBlocked = "the DeFog mod is installed and already removes the fade";
        }
        else
        {
            try
            {
                if (VanillaFadePatch.Install(Mod.Logger))
                {
                    VanillaFadePatch.SetEnabled(config.RemoveVanillaEdgeFade);
                    Mod.Logger.Notification(config.RemoveVanillaEdgeFade
                        ? "VintageHorizons removes the game's edge fade at the view distance (no fog). Use .vhfog on to show the game's own fade."
                        : "VintageHorizons keeps the game's edge fade (fog on). Use .vhfog off to hide it.");
                }
                else
                {
                    edgeFadeBlocked = "this game build does not have the expected shader method";
                    Mod.Logger.Warning(
                        "Did not patch the game's edge fade: this game build does not have "
                        + "the expected shader method. The game's edge fade remains.");
                }
            }
            catch (Exception e)
            {
                edgeFadeBlocked = "the patch installation failed";
                Mod.Logger.Warning(
                    "Did not patch the game's edge fade: {0}. The game's edge fade remains.",
                    e.Message);
            }
        }

        capi.Event.ChunkDirty += OnChunkDirty;
        capi.Event.LevelFinalize += OnLevelFinalize;
        capi.Event.LeaveWorld += OnLeaveWorld;

        tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, 50);

        RegisterCommands();

        Mod.Logger.Notification("VintageHorizons {0} loaded (client-only)", Mod.Info.Version);
    }

    /// <summary>
    /// A dev hook for the matrix tier: send one chat command shortly after the world is
    /// up. The harness cannot reach a detached server's console, and a second entry point
    /// into a command would test something other than what players type. The delay lets
    /// the handshake and the privilege grant settle first.
    ///
    /// Reachable from the idle path too, and not for symmetry: '.vhdefer off' is the
    /// escape hatch we tell an idle player to use, and it writes the config file from a
    /// state where the renderer does not exist. That is the one command whose failure
    /// would strand exactly the player it is meant to rescue.
    /// </summary>
    void RegisterAutoCommand()
    {
        if (Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOCMD") is not { Length: > 0 } autoCmd)
        {
            return;
        }

        capi.Event.RegisterCallback(_ =>
        {
            Mod.Logger.Notification("Auto-command: {0}", autoCmd);

            // SendChatMessage sends to the SERVER, so it runs '/' commands and silently
            // does nothing with a client-side '.' one: the message goes out, no handler
            // on either side claims it, and the log looks like a success. Client commands
            // have to be dispatched locally.
            if (autoCmd.StartsWith('.'))
            {
                capi.ChatCommands.ExecuteUnparsed(autoCmd, new TextCommandCallingArgs
                {
                    Caller = new Caller
                    {
                        Player = capi.World.Player,
                        Pos = capi.World.Player.Entity.Pos.XYZ,
                        FromChatGroupId = GlobalConstants.GeneralChatGroup,

                        // The game fills these in when a person types the command; a
                        // caller built here starts with none and is refused by its own
                        // privilege test. Wildcard, because this hook exists only to
                        // reproduce what a player does, and a test that cannot reach the
                        // command tests nothing.
                        CallerPrivileges = new[] { "*" },
                    },
                }, result => Mod.Logger.Notification("Auto-command result: {0}", result.StatusMessage));
                return;
            }

            capi.SendChatMessage(autoCmd);
        }, 15000);
    }

    /// <summary>
    /// The mod to stay idle for, or null to draw. Reports what it found either way: a
    /// player who sees no distant terrain needs the log to say which mod stopped us, and
    /// a player whose other mod is switched off needs to know we noticed.
    /// </summary>
    string? ChooseDeferralTarget()
    {
        (string? drawing, string[] switchedOff) =
            OtherLodMods.Inspect(capi.ModLoader.IsModEnabled, ReadOtherModSwitch);

        if (switchedOff.Length > 0)
        {
            Mod.Logger.Notification(
                "Installed but switched off in its own settings, so VintageHorizons is not "
                + "staying idle for it: {0}", string.Join(", ", switchedOff));
        }

        if (drawing != null && config.IgnoreOtherLodMods)
        {
            Mod.Logger.Warning(
                "'{0}' is switched on, and IgnoreOtherLodMods is set, so VintageHorizons is drawing "
                + "anyway. Switch '{0}' off in its own settings, or the two draw over the same ground.",
                drawing);
            return null;
        }

        return drawing;
    }

    /// <summary>Reads another mod's own switch. Null means "could not tell", which counts as on.</summary>
    bool? ReadOtherModSwitch(string file)
    {
        try
        {
            return capi.LoadModConfig<OtherLodModSwitch>(file)?.Enabled;
        }
        catch (Exception e)
        {
            // Another mod's config file is not ours to repair, and guessing "off" would
            // put us on the same ground as a mod that is still drawing.
            Mod.Logger.Warning("Could not read '{0}' to tell whether that mod is switched on: {1}",
                file, e.Message);
            return null;
        }
    }

    void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        pipeline.QueueColumn(chunkCoord.X, chunkCoord.Z);
    }

    void OnGameTick(float dt)
    {
        if (!pipeline.Active) return;

        ReportFillIn();
        PumpServerAssist();
        PumpLocalOffers();
        pipeline.Tick();

        var pos = capi.World.Player.Entity.Pos;
        if (pipeline.MaybeEvictAround(pos.X, pos.Z))
        {
            LodWorld world = pipeline.World;
            Mod.Logger.Notification("Evict sweep at {0},{1}: checked {2}, pinned {3}, cold {4}, total evicted {5}",
                (int)pos.X, (int)pos.Z, world.LastSweepChecked, world.LastSweepPinned,
                world.LastSweepCold, world.EvictedSectionsTotal);
        }
    }

    /// <summary>
    /// Adopt whatever the server sent, then ask for what the render path now wants.
    /// Both on the game tick, because both mutate the LodWorld.
    /// </summary>
    void PumpServerAssist()
    {
        if (assist == null || !assist.Available) return;

        int before = pipeline.RemoteOnly.Count;
        assist.Pump((key, blob) =>
        {
            if (blob.Length > 0 && pipeline.InstallForeignBlob(key, blob, RecolorForeignSection)) return true;
            pipeline.MarkRemoteUnavailable(key);
            return false;
        });

        // Manifest keys become quadtree-visible here rather than in the packet handler:
        // HasDataSet belongs to this thread.
        if (assist.RemoteKeys.Count > 0) pipeline.AddRemoteKeys(assist.RemoteKeys);

        // Nearest first. The render path asks for exactly the sections it wants, but far
        // more than the in-flight cap allows at once, and an unordered set hands the
        // network whatever hashes first - so distant terrain arrived while ground in front
        // of the player stayed at its coarse parent, which is what the no-holes rule draws
        // until all four children land. Sorting here rather than in the pipeline keeps the
        // pipeline free of any notion of where the viewer is.
        long[] wanted = pipeline.RemoteWanted();
        if (wanted.Length > 1)
        {
            var at = capi.World.Player.Entity.Pos;
            double px = at.X, pz = at.Z;
            Array.Sort(wanted, (a, b) =>
                LodWorld.NearestDistanceSqTo(a, px, pz).CompareTo(LodWorld.NearestDistanceSqTo(b, px, pz)));
        }
        pipeline.MarkRemoteRequested(assist.Request(wanted));

        if (pipeline.RemoteOnly.Count != before && !loggedRemoteKeys)
        {
            loggedRemoteKeys = true;
            Mod.Logger.Notification(
                "Server assist: {0} sections offered that this client never captured. "
                + "The mod fetches them as the view needs them.", pipeline.RemoteOnly.Count);
        }
    }

    bool loggedRemoteKeys;

    /// <summary>
    /// The server side's cache for this same singleplayer world, when it has swept one.
    /// Null on a dedicated server (the network assist covers that) and on any world that
    /// has never swept.
    /// </summary>
    LodLocalOfferSource? localOffers;
    bool loggedLocalOffers;
    int localOfferProbeTicks;
    int localOfferScanTicks;

    /// <summary>
    /// About 5s at the 50ms tick. The retry usually answers with one failed File.Exists,
    /// so it stays cheap; anything shorter is pointless for a cache that grows for
    /// minutes once it appears.
    /// </summary>
    const int LocalOfferProbeIntervalTicks = 100;

    /// <summary>
    /// About 1s at the 50ms tick, for re-reading the offered key list.
    ///
    /// Unlike the probe above, this one is NOT cheap: it is a full scan of the server
    /// side's Section table, and it ran on every tick for the whole session. A swept
    /// world holds thousands of sections, so that was tens of thousands of row decodes
    /// and a fresh key array every 50ms, on the main thread, to learn about the handful
    /// of rows a sweep writes per second.
    /// </summary>
    const int LocalOfferScanIntervalTicks = 20;

    /// <summary>
    /// Adopt sections the server side swept out of the savegame.
    ///
    /// Identical in shape to PumpServerAssist and deliberately so - same remote-key
    /// bookkeeping, same recolour on install, same nearest-first ordering. Only the
    /// transport differs, and there is no in-flight cap because a local file read has no
    /// round trip to protect. The budget per tick is there so a sweep of ten thousand
    /// sections does not try to install all of them in one frame.
    /// </summary>
    void PumpLocalOffers()
    {
        if (localOffers == null)
        {
            // A null here means "not yet", not "never": the server side can open its
            // cache long after this client finalized - /vhgen creates one on demand,
            // and a sweep's file can appear after a slow start. Opening once at
            // LevelFinalize and never looking again left both invisible for the whole
            // session.
            if (++localOfferProbeTicks < LocalOfferProbeIntervalTicks) return;
            localOfferProbeTicks = 0;
            if (pipeline.DbPath is not string dbPath) return;
            localOffers = LodLocalOfferSource.TryOpen(dbPath, Mod.Logger);
            if (localOffers == null) return;
        }

        // The sweep writes continuously, so re-reading the key list picks up whatever has
        // landed since. AddRemoteKeys ignores anything already known, and anything local
        // disk already holds.
        //
        // A sweep writes a few sections a second, so a second of latency here costs
        // nothing. The install budget below still runs every tick: what is throttled is
        // asking the database what exists, not acting on the answer.
        if (++localOfferScanTicks >= LocalOfferScanIntervalTicks)
        {
            localOfferScanTicks = 0;
            long[] offered = localOffers.Keys();
            if (offered.Length > 0)
            {
                pipeline.AddRemoteKeys(offered);
                if (!loggedLocalOffers)
                {
                    loggedLocalOffers = true;
                    // Sweeps and /vhgen both fill the sibling cache; this line covers either.
                    Mod.Logger.Notification(
                        "Server-side cache offers {0} sections locally. The mod adopts them as "
                        + "the view needs them.", offered.Length);
                }
            }
        }

        long[] wanted = pipeline.RemoteWanted();
        if (wanted.Length == 0) return;

        if (wanted.Length > 1)
        {
            var at = capi.World.Player.Entity.Pos;
            double px = at.X, pz = at.Z;
            Array.Sort(wanted, (a, b) =>
                LodWorld.NearestDistanceSqTo(a, px, pz).CompareTo(LodWorld.NearestDistanceSqTo(b, px, pz)));
        }

        int budget = Math.Min(wanted.Length, LocalOffersPerTick);
        var taken = new long[budget];
        for (int i = 0; i < budget; i++)
        {
            long key = wanted[i];
            taken[i] = key;

            byte[]? blob = localOffers.Blob(key);
            // A miss is ordinary while the sweep is still running: the key was listed but
            // its row is not written yet. MarkRemoteUnavailable is permanent, so it must
            // not be used for "not yet" - leaving the key alone lets a later tick retry.
            if (blob == null || blob.Length == 0) continue;

            if (!pipeline.InstallForeignBlob(key, blob, RecolorForeignSection))
            {
                pipeline.MarkRemoteUnavailable(key);
            }
        }
        pipeline.MarkRemoteRequested(taken);
    }

    /// <summary>
    /// Sections installed from the local sweep per tick. Higher than the network's in-flight
    /// cap because there is no round trip to hide, but still bounded: each install
    /// decompresses a blob and recolours a palette on the main thread.
    /// </summary>
    const int LocalOffersPerTick = 4;

    /// <summary>
    /// Fill in palette colours for a section captured by a server, which had no texture
    /// atlas and stored 0 for every one of them (DESIGN.md §10.4). Block ids are already
    /// resolved from codes by the deserializer, so this only needs the atlas.
    /// </summary>
    void RecolorForeignSection(LodSection section)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry entry = section.Palette[i];

            // A code the block registry could not answer for. It still carries the flags
            // the capturing side worked out, so it is still drawn as terrain - and a
            // server stores 0 for every colour, so leaving this one alone drew it at
            // exactly RGB 0,0,0. Black ground, correctly shaped, with nothing anywhere
            // saying why. The shader cannot save it either: shade bottoms out at 0.55 and
            // daylight is clamped to 0.02, but zero times anything is still zero.
            //
            // Neutral grey instead, and counted. Wrong-but-plausible stone beats a hole
            // in the world, and the count is what turns the next report into a log line.
            if (entry.BlockId <= 0)
            {
                entry.Color = LodPaletteRepair.UnknownBlockColor;
                section.Palette[i] = entry;
                uncoloredForeignEntries++;
                continue;
            }

            Block block = capi.World.Blocks[entry.BlockId];
            int subId = block.TextureSubIdForBlockColor;
            int color = IsUsableAtlasTexture(subId)
                ? capi.BlockTextureAtlas.GetAverageColor(subId)
                : ColorFromAnyTexture(block, ColorUtil.WhiteArgb);

            // A colour texture that resolved to the unknown.png placeholder is not a
            // colour, it is the absence of one. Chiselled blocks land here: their colour
            // lives in a block entity this path has no position to read (the section came
            // from a server or a peek), so the honest answer is the same neutral grey an
            // unknown block gets - not the placeholder's near-white.
            if (unknownTextureColor != 0 && color == unknownTextureColor)
            {
                color = ColorFromAnyTexture(block, LodPaletteRepair.UnknownBlockColor);
            }
            entry.Color = color;
            section.Palette[i] = entry;
        }
    }

    /// <summary>
    /// The client half of palette registration: the untinted average colour from the
    /// texture atlas, plus which live tint applies. Stored untinted on purpose, so the
    /// shader can follow the calendar instead of freezing the season it was captured in.
    /// A server has no atlas and cannot answer this at all (DESIGN.md §10.4).
    ///
    /// The position is the exact block being described, and that is load-bearing:
    /// GetColorWithoutTint reads the world at pos - chiselled blocks average the
    /// materials in their block entity there, the same way the world map colours them.
    /// A synthetic chunk-centre position made that lookup miss, so every chisel took
    /// the placeholder texture's near-white instead of its own materials.
    /// </summary>
    (int Color, byte TintSlot) DescribePalette(int blockId, int blockX, int blockY, int blockZ)
    {
        Block block = capi.World.Blocks[blockId];
        paletteSamplePos.Set(blockX, blockY, blockZ);
        int color = block.GetColorWithoutTint(capi, paletteSamplePos);

        if (!IsUsableAtlasTexture(block.TextureSubIdForBlockColor)
            // Guarded on non-zero: if the atlas never populated AvgColor we would be
            // comparing against 0 and "fixing" every legitimately black block.
            || (unknownTextureColor != 0 && color == unknownTextureColor))
        {
            color = ColorFromAnyTexture(block, color);
        }
        return (color, (byte)tints.SlotFor(block));
    }

    /// <summary>Average colour of unknown.png (near-white, not magenta - measured).</summary>
    int unknownTextureColor;

    /// <summary>Foreign palette entries drawn as grey because no block matched.</summary>
    int uncoloredForeignEntries;

    /// <summary>
    /// Colour for one block id, for repairing a cache saved without any. Separate from
    /// DescribePalette because that one also answers the tint slot and needs a world
    /// position; here the block is all there is to go on.
    /// </summary>
    int AtlasColorOf(int blockId)
    {
        if (blockId <= 0) return LodPaletteRepair.UnknownBlockColor;

        Block block = capi.World.Blocks[blockId];
        int subId = block.TextureSubIdForBlockColor;
        int color = IsUsableAtlasTexture(subId)
            ? capi.BlockTextureAtlas.GetAverageColor(subId)
            : ColorFromAnyTexture(block, ColorUtil.WhiteArgb);

        // Same rule as the foreign path: the placeholder's average is not a colour.
        // The repair has only a block id to go on, so a chiselled block repairs to
        // neutral grey here and gets its real materials on the next capture.
        if (unknownTextureColor != 0 && color == unknownTextureColor)
        {
            color = ColorFromAnyTexture(block, LodPaletteRepair.UnknownBlockColor);
        }
        return color;
    }

    /// <summary>
    /// Whether an atlas sub-id actually names a texture. `GetAverageColor` on an unassigned
    /// or out-of-range sub-id reads whatever the atlas holds there, which is where a
    /// nonsense LOD colour comes from - and unlike the unknown.png case, that does not
    /// require knowing what the placeholder looks like to detect.
    /// </summary>
    bool IsUsableAtlasTexture(int subId)
    {
        if (subId < 0) return false;

        TextureAtlasPosition[] positions = capi.BlockTextureAtlas.Positions;
        return subId < positions.Length && positions[subId] != null;
    }

    readonly Dictionary<int, int> missingTextureColorFallback = new();
    int missingTextureBlocks;
    bool loggedMissingTexture;

    /// <summary>
    /// Salvage a colour for a block whose block-colour texture did not resolve, so it draws
    /// as itself instead of as a placeholder or as whatever the atlas holds at a bogus id.
    ///
    /// Vanilla picks that texture in Block.LoadTextureSubIdForBlockColor: the
    /// 'textureCodeForBlockColor' attribute, else "up", else `Textures.First()` - and that
    /// last step ends in `?? 0`, so a block whose first texture in dictionary order has no
    /// Baked entry silently resolves to atlas subid 0, which is unknown.png. The block's
    /// other faces are baked fine, which is why it looks correct up close and magenta only
    /// in LOD. Measured firing on vanilla 'fruitingbush-wild-blackberry-free', so this is
    /// not a modded-content problem -- content-heavy block packs just hit it more often.
    ///
    /// So: use any of the block's own baked textures instead of the first one. Cached per
    /// block id - the answer cannot change within a session, and a palette entry is
    /// registered once per section, which is thousands of times per world.
    /// </summary>
    int ColorFromAnyTexture(Block block, int fallback)
    {
        // The cache holds only the block's own answer, with 0 for "no usable texture"
        // (no real block averages to 0, the same invariant colour-0 repair rests on).
        // The fallback is the caller's and is applied per call: the capture path passes
        // the probe colour, the foreign path passes grey, and caching whichever caller
        // came first hands one caller's stand-in to the others.
        if (missingTextureColorFallback.TryGetValue(block.BlockId, out int cached))
        {
            return cached != 0 ? cached : fallback;
        }

        int found = 0;
        if (block.Textures != null)
        {
            foreach (CompositeTexture tex in block.Textures.Values)
            {
                int subId = tex?.Baked?.TextureSubId ?? -1;
                if (!IsUsableAtlasTexture(subId)) continue;

                int candidate = capi.BlockTextureAtlas.GetAverageColor(subId);
                if (unknownTextureColor != 0 && candidate == unknownTextureColor) continue;

                found = candidate;
                break;
            }
        }

        missingTextureColorFallback[block.BlockId] = found;
        missingTextureBlocks++;
        if (!loggedMissingTexture)
        {
            loggedMissingTexture = true;
            Mod.Logger.Notification(
                "Block '{0}' has no usable block-color texture (vanilla resolved it to unknown.png). "
                + "The mod uses another of its own textures instead, so it does not render wrong at distance.",
                block.Code);
        }
        return found != 0 ? found : fallback;
    }

    /// <summary>
    /// Find a block using the standard plant tint, so plants that declare no colour map
    /// (ferns) can borrow it instead of rendering as their greyscale texture.
    /// </summary>
    void ResolvePlantTintFallback()
    {
        foreach (Block block in capi.World.Blocks)
        {
            if (block?.Code == null) continue;
            if (block.SeasonColorMapResolved != null) continue;
            if (block.ClimateColorMapResolved == null) continue;
            if (block.ClimateColorMap != "climatePlantTint") continue;

            tints.PlantTintFallback = block;
            return;
        }
    }

    readonly System.Diagnostics.Stopwatch joinClock = new();
    static readonly int[] FillInMilestones = { 100, 300, 600, 1200 };
    int nextMilestone;

    void ReportFillIn()
    {
        while (nextMilestone < FillInMilestones.Length && renderer.MeshCount >= FillInMilestones[nextMilestone])
        {
            Mod.Logger.Notification("Fill-in: {0} meshes after {1:0.0}s",
                FillInMilestones[nextMilestone], joinClock.Elapsed.TotalSeconds);
            nextMilestone++;
        }
    }

    void OnLevelFinalize()
    {
        ResolvePlantTintFallback();
        // Read once here rather than per palette entry: the atlas exists by now, and a
        // reload would change the position object but not what magenta looks like.
        unknownTextureColor = capi.BlockTextureAtlas.UnknownTexturePosition.AvgColor;
        Mod.Logger.Debug("Missing-texture color is {0:X8}{1}", unknownTextureColor,
            unknownTextureColor == 0 ? " (zero: magenta-block salvage disabled)" : "");
        renderer.ApplyZFar();
        pipeline.Open("ModData/vintagehorizons");
        joinClock.Restart();
        nextMilestone = 0;

        // A singleplayer world whose server side has swept the savegame leaves its results
        // in a sibling cache. Nothing to open on a dedicated server, where the same
        // sections arrive over the network instead.
        if (pipeline.DbPath is string dbPath)
        {
            localOffers = LodLocalOfferSource.TryOpen(dbPath, Mod.Logger);
        }

        // Last, and after the pipeline is live. An exception in a LevelFinalize handler
        // skips everything the handler has left to do, so an optional extra must not sit
        // upstream of the mod's actual job -- it did, and it broke exactly the
        // vanilla-server case it exists to stay out of the way of.
        assist?.Greet();

        Mod.Logger.Notification(
            "Level finalized. LOD capture active (render distance: unlimited, {0} sections from cache{1}).",
            pipeline.CachedSectionsLoaded, renderer.AutoUnpause ? ", auto-unpause on" : "");

        capi.Event.RegisterCallback(_ => LogStats("Stats after 30s"), 30000);

        // Continuous telemetry. Was tied to AutoUnpause, which meant an *attended* session
        // - the only kind where someone can say "this looks wrong" - was the one case with
        // no ongoing numbers to explain it. Its own switch now, so watching and driving are
        // independent.
        if (renderer.AutoUnpause || Environment.GetEnvironmentVariable("VINTAGEHORIZONS_STATS") == "1")
        {
            capi.Event.RegisterGameTickListener(_ => LogStats("Stats"), 15000);
        }

        RegisterAutoCommand();

        autoExplore = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOEXPLORE") == "1";

        // Creative for any unattended run, not only an exploring one.
        //
        // A survival player is moved by things the test did not ask for: knockback,
        // drowning, hunger, a mob, and at worst a death that respawns them somewhere
        // else entirely. Player position is an INPUT to most of what these scenarios
        // measure - which chunks stream in, where /vhgen centres, and which positions
        // the absence verifier excludes as explainable by a nearby player. A scenario
        // whose subject wanders is measuring the wander.
        //
        // Sent before the auto-command below fires, so a /vhgen run is already centred
        // on a player who will stay put.
        if (autoExplore || Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CREATIVE") == "1")
        {
            capi.Event.RegisterCallback(_ =>
            {
                Mod.Logger.Notification("Test run: entering creative so the player cannot be moved");
                capi.SendChatMessage("/gamemode creative");
            }, 10000);
        }

        if (autoExplore)
        {
            exploreX = capi.World.Player.Entity.Pos.X;
            exploreZ = capi.World.Player.Entity.Pos.Z;
            capi.Event.RegisterGameTickListener(_ => ExploreHop(), 60000);
            Mod.Logger.Notification("Auto-explore active (spiral teleports every 60s)");
        }
    }

    static readonly int ExploreHopBlocks =
        int.TryParse(Environment.GetEnvironmentVariable("VINTAGEHORIZONS_EXPLORE_HOP"), out int h) && h > 0 ? h : 350;

    void ExploreHop()
    {
        int hop = ExploreHopBlocks;

        exploreX += exploreDirX * hop;
        exploreZ += exploreDirZ * hop;

        // Square spiral: legs lengthen every second turn.
        if (++exploreStep >= exploreLeg / 2 + 1)
        {
            exploreStep = 0;
            exploreLeg++;
            (exploreDirX, exploreDirZ) = (-exploreDirZ, exploreDirX);
        }

        int y = capi.World.SeaLevel + 140;
        capi.SendChatMessage($"/tp ={(int)exploreX} {y} ={(int)exploreZ}");
    }

    bool loggedFirstCaptureError, loggedFirstMeshError, loggedFirstSaveError;
    int gen0AtLastReport, gen1AtLastReport, gen2AtLastReport;

    void LogStats(string prefix)
    {
        LodWorld world = pipeline.World;
        LodWorker worker = pipeline.Worker;
        LodStorageThread? storageThread = pipeline.StorageThread;

        if (!loggedFirstCaptureError && worker.FirstCaptureError != null)
        {
            loggedFirstCaptureError = true;
            Mod.Logger.Warning("First capture error was: {0}", worker.FirstCaptureError);
        }
        if (!loggedFirstMeshError && worker.FirstMeshError != null)
        {
            loggedFirstMeshError = true;
            Mod.Logger.Warning("First mesh error was: {0}", worker.FirstMeshError);
        }

        Mod.Logger.Notification(
            "{0}: {1} sections resident [{2}] ({3} RAM-evicted, {4} from cache), {5} meshes ({6} evicted), " +
            "{7} selected [{8}] minus {9} frustum-culled, {10} columns captured, {11} pending, " +
            "worker: {12} captures / {13} meshes queued / {14}+{15} errors, {16} awaiting mip, {17} render-dirty, {18} unsaved",
            prefix, world.Sections.Count, world.DescribeLevels(), world.EvictedSectionsTotal, pipeline.CachedSectionsLoaded,
            renderer.MeshCount, renderer.EvictedTotal, renderer.LastDrawCount, renderer.DescribeDrawnLevels(),
            renderer.LastCulledCount, pipeline.ColumnsCaptured, pipeline.PendingColumns, worker.PendingCaptures, worker.PendingMeshes,
            worker.CaptureErrors, worker.MeshErrors, world.MipDirty.Count, world.RenderDirty.Count,
            world.SaveDirty.Count);

        Mod.Logger.Notification(
            "  storage on main thread since last report: snapshot {0} calls, {1:0.00}ms avg, {2:0.00}ms max | " +
            "inline loads {3} calls, {4:0.00}ms avg, {5:0.00}ms max | storage thread: {6} write backlog, " +
            "{7} written, {8} write errors, {11} read, {9} async loads in flight, {10} read errors",
            pipeline.SaveCalls, pipeline.SaveCalls > 0 ? pipeline.SaveMsTotal / pipeline.SaveCalls : 0, pipeline.SaveMsMax,
            pipeline.LoadCalls, pipeline.LoadCalls > 0 ? pipeline.LoadMsTotal / pipeline.LoadCalls : 0, pipeline.LoadMsMax,
            storageThread?.Backlog ?? 0, storageThread?.SectionsWritten ?? 0, storageThread?.SaveErrors ?? 0,
            world.LoadsInFlight.Count, storageThread?.LoadErrors ?? 0, storageThread?.SectionsRead ?? 0);

        if (assist != null && assist.RemoteKeys.Count > 0)
        {
            Mod.Logger.Notification(
                "  server assist: {0} offered, {1} remote-only, {2} wanted by view, {3} requested, " +
                "{4} received, {5} installed, {6} in flight, {7} declined",
                assist.RemoteKeys.Count, pipeline.RemoteOnly.Count, pipeline.RemoteWanted().Length,
                assist.SectionsRequested, assist.SectionsReceived, pipeline.ForeignSectionsInstalled,
                assist.InFlight, assist.SectionsRefused);
        }

        // Repairing means the cache on disk was written without colours, which drew as
        // black ground. Worth saying out loud, and worth being able to watch go to zero.
        if (pipeline.PaletteEntriesRepaired > 0)
        {
            Mod.Logger.Notification(
                "  repaired {0} palette entries that were cached with no color at all. "
                + "They drew as black terrain, and the repair is written back as they load.",
                pipeline.PaletteEntriesRepaired);
        }

        // Warning, not notification: this is terrain drawn as a guess. Naming the codes is
        // the whole point - the previous symptom was black ground and nothing to grep for.
        if (uncoloredForeignEntries > 0)
        {
            string[] codes = pipeline.UnresolvedBlockCodes();
            Mod.Logger.Warning(
                "  {0} palette entries came from blocks this game does not have, and are drawn as "
                + "plain gray. {1}",
                uncoloredForeignEntries,
                codes.Length > 0
                    ? "Codes: " + string.Join(", ", codes.Take(12))
                      + (codes.Length > 12 ? $" and {codes.Length - 12} more" : "")
                    : "No unresolved codes were recorded, so these arrived over the network rather "
                      + "than from the cache.");
        }

        if (renderer != null && renderer.WalkCost.Calls > 0)
        {
            // Microseconds, because that is the scale these are at, and because rounding
            // them to milliseconds would print four zeroes and teach nobody anything.
            // A frame-rate comparison cannot see any of these: the benchmark's own
            // run-to-run spread is wider than all four added together.
            Mod.Logger.Notification(
                "  render thread per frame over {0} frames: prune {9:0.0}us avg / {10:0.0}us max | " +
                "schedule {1:0.0}/{2:0.0} | " +
                "far distance {3:0.0}/{4:0.0} | quadtree walk {5:0.0}/{6:0.0} | draw submit {7:0.0}/{8:0.0}",
                renderer.WalkCost.Calls,
                renderer.ScheduleCost.AvgUs, renderer.ScheduleCost.MaxUs,
                renderer.FarDistanceCost.AvgUs, renderer.FarDistanceCost.MaxUs,
                renderer.WalkCost.AvgUs, renderer.WalkCost.MaxUs,
                renderer.DrawCost.AvgUs, renderer.DrawCost.MaxUs,
                renderer.PruneCost.AvgUs, renderer.PruneCost.MaxUs);

            // Collections since the last report, beside the phase maxima, because the
            // two are related and the relationship is easy to get backwards. A phase
            // maximum is not a measurement of that phase: the far-distance scan averages
            // 2us over a few hundred meshes and has reported a 1624us maximum, which
            // nothing inside a loop that multiplies and compares can account for. What a
            // maximum records is whatever interrupted the frame, charged to whichever
            // phase was running. These counters say how much of that was collection.
            Mod.Logger.Notification(
                "  gc since last report: {0} gen0, {1} gen1, {2} gen2, {3} MB managed",
                GC.CollectionCount(0) - gen0AtLastReport,
                GC.CollectionCount(1) - gen1AtLastReport,
                GC.CollectionCount(2) - gen2AtLastReport,
                GC.GetTotalMemory(false) / (1024 * 1024));

            gen0AtLastReport = GC.CollectionCount(0);
            gen1AtLastReport = GC.CollectionCount(1);
            gen2AtLastReport = GC.CollectionCount(2);
        }

        if (storageThread?.FirstSaveError != null && !loggedFirstSaveError)
        {
            loggedFirstSaveError = true;
            Mod.Logger.Warning("First storage-write error was: {0}", storageThread.FirstSaveError);
        }
        pipeline.ResetStorageStats();
        renderer?.ResetPhaseCosts();
    }

    void OnLeaveWorld()
    {
        assist?.Reset();
        // Belongs to the world being left: the next one is a different savegame with a
        // different sibling cache, and holding this open would keep a file handle on a
        // database the server side may want to delete or replace.
        localOffers?.Dispose();
        localOffers = null;
        loggedLocalOffers = false;
        pipeline.Close();
        while (pipeline.Worker.MeshResults.TryDequeue(out _)) { }
        renderer.ClearMeshes();
    }

    void RegisterCommands()
    {
        capi.ChatCommands.Create("vhinfo")
            .WithDescription("VintageHorizons status")
            .HandleWith(_ => deferringTo != null
                ? TextCommandResult.Success(
                    $"[VintageHorizons] idle: '{deferringTo}' is installed and switched on, so it is "
                    + "drawing the distant terrain. Switch it off in its own settings, or run "
                    + "'.vhdefer off' to draw beside it. Either way, restart the game afterwards: "
                    + "this is decided once at startup.")
                : TextCommandResult.Success(
                $"[VintageHorizons] sections: {pipeline.World.Sections.Count} [{pipeline.World.DescribeLevels()}] " +
                $"({pipeline.CachedSectionsLoaded} from cache), meshes: {renderer.MeshCount}, " +
                $"drawn: {renderer.LastDrawCount} [{renderer.DescribeDrawnLevels()}], " +
                $"columns captured: {pipeline.ColumnsCaptured}, pending: {pipeline.PendingColumns}, " +
                $"worker: {pipeline.Worker.PendingCaptures}c/{pipeline.Worker.PendingMeshes}m, awaiting mip: {pipeline.World.MipDirty.Count}, " +
                $"unsaved: {pipeline.World.SaveDirty.Count}, persistence: {(pipeline.Persisting ? "on" : "off")}, " +
                $"render distance: {(renderer.FarViewDistanceCap > 0 ? renderer.FarViewDistanceCap + " (capped)" : "unlimited")}, " +
                $"current far edge: {(int)renderer.EffectiveFarDistance}, " +
                $"detail distance: {(int)LodWorld.DetailDistance} (.vhdetail to change), " +
                $"edge fade: {(edgeFadeBlocked != null ? "on - " + edgeFadeBlocked
                    : config.RemoveVanillaEdgeFade ? "off (removed, .vhfog on to show)" : "on (.vhfog off to hide)")}, " +
                $"server assist: {assist?.Status ?? "off"}" +
                (assist != null && assist.RemoteKeys.Count > 0
                    ? $", server offers {assist.RemoteKeys.Count} sections " +
                      $"({pipeline.RemoteOnly.Count} not held locally, {pipeline.ForeignSectionsInstalled} fetched, " +
                      $"{assist.InFlight} in flight, {assist.SectionsRefused} declined)" +
                      (assist.ManifestComplete ? "" : " (manifest still arriving)")
                    : "")));

        // Registered in both states on purpose: the player who most needs this one is the
        // player we are currently idle for.
        capi.ChatCommands.Create("vhdefer")
            .WithDescription("Stay idle when another LOD mod draws. Default on. Off draws anyway.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (args.Parsers[0].IsMissing)
                {
                    return TextCommandResult.Success(
                        $"[VintageHorizons] defer to other LOD mods: {(config.IgnoreOtherLodMods ? "off" : "on")}"
                        + (deferringTo != null ? $" - idle now, because '{deferringTo}' is drawing" : ""));
                }

                config.IgnoreOtherLodMods = !(bool)args[0];
                SaveConfig();

                // Saved, not applied. Starting the mod mid-session would have to register a
                // network channel after the handshake, run a LevelFinalize that has already
                // fired, and capture chunks whose ChunkDirty events are long past.
                return TextCommandResult.Success(config.IgnoreOtherLodMods
                    ? "[VintageHorizons] will draw even when another LOD mod is switched on (saved). "
                      + "Restart the game to apply. Switch the other mod off as well, or the two "
                      + "draw over the same ground."
                    : "[VintageHorizons] will stay idle when another LOD mod is drawing (saved). "
                      + "Restart the game to apply.");
            });

        // The remaining commands drive the renderer, which does not exist when we are
        // deferring to another LOD mod.
        if (deferringTo != null) return;

        capi.ChatCommands.Create("vhwhy")
            .WithDescription("Explain why nearby LOD terrain draws coarser than the detail setting allows")
            .HandleWith(_ =>
            {
                var at = capi.World.Player.Entity.Pos;
                return TextCommandResult.Success(
                    "[VintageHorizons] coarse draws:" + renderer.ExplainCoarseDraws(at.X, at.Z));
            });

        capi.ChatCommands.Create("vhfar")
            .WithDescription("Cap VintageHorizons render distance in blocks (0 = unlimited)")
            .WithArgs(capi.ChatCommands.Parsers.Int("blocks"))
            .HandleWith(args =>
            {
                int blocks = (int)args[0];
                renderer.FarViewDistanceCap = blocks <= 0 ? 0 : GameMath.Clamp(blocks, 1024, 262144);
                SaveConfig();
                return TextCommandResult.Success(renderer.FarViewDistanceCap > 0
                    ? $"[VintageHorizons] render distance capped at {renderer.FarViewDistanceCap} (saved)"
                    : "[VintageHorizons] render distance unlimited (saved)");
            });

        capi.ChatCommands.Create("vhdetail")
            .WithDescription("Distance in blocks before LOD detail starts to halve. Default 512. A higher value gives sharper far terrain and costs more VRAM and CPU.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalInt("blocks"))
            .HandleWith(args =>
            {
                if (args.Parsers[0].IsMissing)
                {
                    return TextCommandResult.Success(
                        $"[VintageHorizons] detail distance {(int)LodWorld.DetailDistance} " +
                        $"(full 1-block detail out to {(int)LodWorld.DetailDistance * 2} blocks). " +
                        $"Set between {(int)LodWorld.MinDetailDistance} and {(int)LodWorld.MaxDetailDistance}.");
                }

                LodWorld.DetailDistance = GameMath.Clamp((int)args[0],
                    (int)LodWorld.MinDetailDistance, (int)LodWorld.MaxDetailDistance);
                SaveConfig();
                return TextCommandResult.Success(
                    $"[VintageHorizons] detail distance {(int)LodWorld.DetailDistance} - full detail out to " +
                    $"{(int)LodWorld.DetailDistance * 2} blocks (saved). Terrain re-selects over the next few seconds.");
            });

        capi.ChatCommands.Create("vhfog")
            .WithDescription("Show or hide the game's edge fade at the view distance. The default is fog off (the fade is removed). .vhfog on shows the game's own fade.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (edgeFadeBlocked != null)
                {
                    return TextCommandResult.Success(
                        $"[VintageHorizons] this mod does not change the game's edge fade: {edgeFadeBlocked}.");
                }

                if (args.Parsers[0].IsMissing)
                {
                    return TextCommandResult.Success(
                        $"[VintageHorizons] fog at the view edge: {(config.RemoveVanillaEdgeFade ? "off" : "on")}. "
                        + "Use .vhfog off to hide it, .vhfog on to show the game's own fade.");
                }

                bool fogOn = (bool)args[0];
                config.RemoveVanillaEdgeFade = !fogOn;
                VanillaFadePatch.SetEnabled(config.RemoveVanillaEdgeFade);
                SaveConfig();
                return TextCommandResult.Success(fogOn
                    ? "[VintageHorizons] fog at the view edge is now on - the game's own fade shows (saved). Use .vhfog off to hide it."
                    : "[VintageHorizons] fog at the view edge is now off - the fade is removed while LOD terrain draws (saved). Use .vhfog on to show it.");
            });
    }

    /// <summary>Writes every setting: a partial write would silently reset the others.</summary>
    void SaveConfig()
    {
        // The renderer and the LodWorld statics do not exist while we defer, and what was
        // loaded from the file is still the right thing to write back.
        if (deferringTo == null)
        {
            config.FarViewDistanceCap = renderer.FarViewDistanceCap;
            config.DetailDistance = (int)LodWorld.DetailDistance;
        }

        capi.StoreModConfig(config, "vintagehorizons.json");
    }

    public override void Dispose()
    {
        if (capi == null) return;

        // The engine runs this on whichever thread is tearing the game down, and on the
        // vanilla shutdown crash path ("Can't use a disposed shader" out of a render
        // stage) that is not the main thread. Every engine call below refuses to run off
        // it, and an exception that escapes one step skips every step behind it. Two
        // rounds of this were needed to learn the general rule: the first version lost
        // the storage writer's shutdown, and the second, which guarded only the events,
        // lost the renderer's. So each step stands alone and none of them propagates.
        Quietly(() =>
        {
            capi.Event.ChunkDirty -= OnChunkDirty;
            capi.Event.LevelFinalize -= OnLevelFinalize;
            capi.Event.LevelFinalize -= RegisterAutoCommand;
            capi.Event.LeaveWorld -= OnLeaveWorld;

            // Nothing to unregister while deferring: that path registers no listener.
            if (deferringTo == null) capi.Event.UnregisterGameTickListener(tickListenerId);
        });

        // Stops the storage writer before the connection it writes through.
        Quietly(() => pipeline?.Dispose());
        Quietly(() => renderer?.Dispose());
        Quietly(VanillaFadePatch.Uninstall);
    }

    /// <summary>
    /// One shutdown step, and whatever it throws stays here. Only ever called from
    /// <see cref="Dispose"/>, where the game is already going down and the alternative is
    /// abandoning the steps behind this one.
    /// </summary>
    void Quietly(Action step)
    {
        try
        {
            step();
        }
        catch (Exception e)
        {
            Mod.Logger.Debug("A shutdown step was refused, which changes nothing by now: {0}",
                e.Message);
        }
    }
}
