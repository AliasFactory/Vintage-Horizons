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
    const int ChunkSize = GlobalConstants.ChunkSize;

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

    /// <summary>
    /// Other LOD mods, by mod id. Farseer, ChunkLOD and TopoHorizon are all Universal
    /// with requiredOnClient, so a server running one forces every client to load it
    /// too -- the player cannot opt out of theirs, only out of ours.
    /// </summary>
    static readonly string[] CompetingLodMods = { "farseer", "chunklod", "vistasbeyond", "topohorizon" };

    /// <summary>Set when another LOD mod is present; we then stay out of its way.</summary>
    string? deferringTo;

    /// <summary>Optional server assist (DESIGN.md §10); null while deferring.</summary>
    LodAssistClient? assist;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        foreach (string modid in CompetingLodMods)
        {
            if (!capi.ModLoader.IsModEnabled(modid)) continue;

            // Two LOD mods both extend the camera's far plane and both draw terrain out
            // there: they would reset the projection matrix against each other every
            // frame and z-fight over the same ground. Defer rather than fight, because
            // a server-side mod is mandatory for anyone on that server while we are not.
            deferringTo = modid;
            Mod.Logger.Notification(
                "'{0}' is loaded, so VintageHorizons is staying idle to avoid drawing over it "
                + "and fighting it for the camera far plane. Remove '{0}' to use VintageHorizons instead.",
                modid);
            RegisterCommands();
            return;
        }

        VintageHorizonsConfig config;
        try
        {
            config = capi.LoadModConfig<VintageHorizonsConfig>("vintagehorizons.json") ?? new VintageHorizonsConfig();
        }
        catch
        {
            config = new VintageHorizonsConfig();
        }

        LodWorld.DetailDistance = GameMath.Clamp(config.DetailDistance,
            (int)LodWorld.MinDetailDistance, (int)LodWorld.MaxDetailDistance);

        pipeline = new LodPipeline(capi, Mod.Logger, DescribePalette, block => (byte)tints.SlotFor(block));
        renderer = new LodTerrainRenderer(capi, pipeline.World, pipeline.Worker, tints)
        {
            AutoUnpause = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOUNPAUSE") == "1",
            FarViewDistanceCap = config.FarViewDistanceCap,
        };

        // Registered here rather than on world join: channel registration has to be in
        // place before the connection handshake runs, or the server never learns we
        // speak it. Against a vanilla server this stays an unused registration.
        assist = new LodAssistClient(capi, Mod.Logger, Mod.Info.Version);
        assist.Register();

        capi.Event.ChunkDirty += OnChunkDirty;
        capi.Event.LevelFinalize += OnLevelFinalize;
        capi.Event.LeaveWorld += OnLeaveWorld;

        tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, 50);

        RegisterCommands();

        Mod.Logger.Notification("VintageHorizons {0} loaded (client-only)", Mod.Info.Version);
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
                "Server assist: {0} sections offered that this client has never captured; "
                + "fetching them as the view needs them.", pipeline.RemoteOnly.Count);
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
        if (localOffers == null) return;

        // The sweep writes continuously, so re-reading the key list picks up whatever has
        // landed since. AddRemoteKeys ignores anything already known, and anything local
        // disk already holds.
        long[] offered = localOffers.Keys();
        if (offered.Length > 0) pipeline.AddRemoteKeys(offered);

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

        if (!loggedLocalOffers && offered.Length > 0)
        {
            loggedLocalOffers = true;
            Mod.Logger.Notification(
                "Savegame sweep: {0} sections built from terrain generated in earlier "
                + "sessions are available; adopting them as the view needs them.", offered.Length);
        }
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
            if (entry.BlockId <= 0) continue;

            Block block = capi.World.Blocks[entry.BlockId];
            int subId = block.TextureSubIdForBlockColor;
            entry.Color = IsUsableAtlasTexture(subId)
                ? capi.BlockTextureAtlas.GetAverageColor(subId)
                : ColorFromAnyTexture(block, ColorUtil.WhiteArgb);
            section.Palette[i] = entry;
        }
    }

    /// <summary>
    /// The client half of palette registration: the untinted average colour from the
    /// texture atlas, plus which live tint applies. Stored untinted on purpose, so the
    /// shader can follow the calendar instead of freezing the season it was captured in.
    /// A server has no atlas and cannot answer this at all (DESIGN.md §10.4).
    /// </summary>
    (int Color, byte TintSlot) DescribePalette(int blockId, int cx, int cz, int sampleY)
    {
        Block block = capi.World.Blocks[blockId];
        paletteSamplePos.Set(cx * ChunkSize + ChunkSize / 2, sampleY, cz * ChunkSize + ChunkSize / 2);
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
        if (missingTextureColorFallback.TryGetValue(block.BlockId, out int cached)) return cached;

        int found = fallback;
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
                "Block '{0}' has no usable block-colour texture (vanilla resolved it to unknown.png); "
                + "using another of its own textures instead so it does not render wrong at distance.",
                block.Code);
        }
        return found;
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
        Mod.Logger.Debug("Missing-texture colour is {0:X8}{1}", unknownTextureColor,
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

        autoExplore = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOEXPLORE") == "1";
        if (autoExplore)
        {
            exploreX = capi.World.Player.Entity.Pos.X;
            exploreZ = capi.World.Player.Entity.Pos.Z;
            capi.Event.RegisterCallback(_ => capi.SendChatMessage("/gamemode creative"), 10000);
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

        if (storageThread?.FirstSaveError != null && !loggedFirstSaveError)
        {
            loggedFirstSaveError = true;
            Mod.Logger.Warning("First storage-write error was: {0}", storageThread.FirstSaveError);
        }
        pipeline.ResetStorageStats();
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
                    $"[VintageHorizons] idle: '{deferringTo}' is also installed and is drawing the "
                    + "distant terrain. Remove it to use VintageHorizons instead.")
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
                $"server assist: {assist?.Status ?? "off"}" +
                (assist != null && assist.RemoteKeys.Count > 0
                    ? $", server offers {assist.RemoteKeys.Count} sections " +
                      $"({pipeline.RemoteOnly.Count} not held locally, {pipeline.ForeignSectionsInstalled} fetched, " +
                      $"{assist.InFlight} in flight, {assist.SectionsRefused} declined)" +
                      (assist.ManifestComplete ? "" : " (manifest still arriving)")
                    : "")));

        // The remaining commands drive the renderer, which does not exist when we are
        // deferring to another LOD mod.
        if (deferringTo != null) return;

        capi.ChatCommands.Create("vhwhy")
            .WithDescription("Explain why nearby LOD terrain is drawn coarser than it should be")
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
            .WithDescription("Distance in blocks before LOD detail starts halving (default 512; higher = sharper far terrain, more VRAM/CPU)")
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
    }

    /// <summary>Writes every setting: a partial write would silently reset the others.</summary>
    void SaveConfig()
    {
        capi.StoreModConfig(new VintageHorizonsConfig
        {
            FarViewDistanceCap = renderer.FarViewDistanceCap,
            DetailDistance = (int)LodWorld.DetailDistance,
        }, "vintagehorizons.json");
    }

    public override void Dispose()
    {
        if (capi != null)
        {
            capi.Event.ChunkDirty -= OnChunkDirty;
            capi.Event.LevelFinalize -= OnLevelFinalize;
            capi.Event.LeaveWorld -= OnLeaveWorld;
            capi.Event.UnregisterGameTickListener(tickListenerId);
            // Stops the storage writer before the connection it writes through.
            pipeline?.Dispose();
            renderer?.Dispose();
        }
    }
}
