using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VintageHorizons.Net;

namespace VintageHorizons;

public class VintageHorizonsConfig
{
    /// <summary>A value of 0 is unlimited.</summary>
    public int FarViewDistanceCap = 0;

    /// <summary>The distance at which the detail starts to decrease. Read
    /// LodWorld.DetailDistance.</summary>
    public int DetailDistance = 512;
}

/// <summary>
/// The entry point on the client. It connects the shared <see cref="LodPipeline"/> to the
/// client events. It also owns each part that the server has no equivalent of: the renderer,
/// the tint registry, the chat commands and the telemetry.
///
/// A chunk column arrives from `ChunkDirty` and goes directly to the pipeline. The pipeline
/// does the capture, the mip and the persistence.
///
/// Read DESIGN.md at the root of the repository.
/// </summary>
public class VintageHorizonsModSystem : ModSystem
{
    const int ChunkSize = GlobalConstants.ChunkSize;

    ICoreClientAPI capi = null!;
    LodPipeline pipeline = null!;
    LodTerrainRenderer renderer = null!;

    /// <summary>A map from a block to its live tint slot. The capture, the cache loads and
    /// the renderer all use it.</summary>
    readonly LodTintRegistry tints = new();
    long tickListenerId;

    readonly BlockPos paletteSamplePos = new(0, 0, 0);

    // Automatic exploration, for development and for an unattended stress test. It teleports
    // the player along a spiral that grows. Thus new chunks stream through the pipeline
    // continuously.
    bool autoExplore;
    int exploreLeg;      // spiral leg counter
    int exploreStep;     // steps taken on current leg
    int exploreDirX = 1, exploreDirZ;
    double exploreX, exploreZ;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    /// <summary>
    /// The other LOD mods, by their mod id. Farseer, ChunkLOD and TopoHorizon are all
    /// Universal with requiredOnClient. Thus a server that runs one of them makes each client
    /// load it. A player cannot refuse those mods, and can refuse this one only.
    /// </summary>
    static readonly string[] CompetingLodMods = { "farseer", "chunklod", "vistasbeyond", "topohorizon" };

    /// <summary>The mod sets this when another LOD mod is present. Then this mod does
    /// nothing.</summary>
    string? deferringTo;

    /// <summary>The optional server assist (DESIGN.md section 10). It is null while this mod
    /// defers to another LOD mod.</summary>
    LodAssistClient? assist;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        foreach (string modid in CompetingLodMods)
        {
            if (!capi.ModLoader.IsModEnabled(modid)) continue;

            // Two LOD mods both increase the far plane of the camera, and both draw
            // terrain there. Thus they set the projection matrix against each other in each
            // frame, and they z-fight over the same ground.
            //
            // This mod defers instead. A server-side mod is necessary for each player on
            // that server, and this mod is not.
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

        // Register here, and not at the world join. The registration of a channel must
        // exist before the connection handshake runs. Without that, the server never learns
        // that this mod uses the channel. Against a vanilla server, this registration stays
        // unused.
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
    /// Take what the server sent. Then ask for what the render path wants now. Both steps
    /// run on the game tick, because both change the LodWorld.
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

        // The manifest keys become visible to the quadtree here, and not in the packet
        // handler. HasDataSet belongs to this thread.
        if (assist.RemoteKeys.Count > 0) pipeline.AddRemoteKeys(assist.RemoteKeys);

        // Take the nearest first.
        //
        // The render path asks for exactly the sections that it wants. That is many more
        // than the in-flight limit permits at one time. A set with no order gives the
        // network the key that hashes first.
        //
        // Thus distant terrain arrived while the ground in front of the player stayed at its
        // coarse parent. The no-holes rule draws that parent until all four children arrive.
        //
        // The sort is here, and not in the pipeline. Thus the pipeline needs no knowledge of
        // the position of the viewer.
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
    /// The cache of the server side, for this same singleplayer world, when a sweep made
    /// one.
    ///
    /// This is null on a dedicated server, where the network assist covers the same need. It
    /// is also null on a world that never swept.
    /// </summary>
    LodLocalOfferSource? localOffers;
    bool loggedLocalOffers;

    /// <summary>
    /// Take the sections that the server side swept out of the savegame.
    ///
    /// The shape is the same as PumpServerAssist, on purpose. It has the same record of the
    /// remote keys, the same recolor at install, and the same nearest-first order.
    ///
    /// Only the transport is different. There is no in-flight limit, because a read of a
    /// local file has no round trip to protect.
    ///
    /// The budget for each tick exists so that a sweep of ten thousand sections does not
    /// install all of them in one frame.
    /// </summary>
    void PumpLocalOffers()
    {
        if (localOffers == null) return;

        // The sweep writes continuously. Thus a new read of the key list finds what arrived
        // since the last read. AddRemoteKeys ignores a key that it knows already, and a key
        // that the local disk holds already.
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
            // A miss is normal while the sweep runs. The list held the key, but the mod did
            // not write its row yet.
            //
            // MarkRemoteUnavailable is permanent. Thus do not use it for "not yet". Leave the
            // key as it is, and a later tick tries again.
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
    /// The number of sections from the local sweep that the mod installs in each tick.
    ///
    /// This is higher than the in-flight limit of the network, because there is no round trip
    /// to hide. It still has a limit, because each install decompresses a blob and gives a
    /// palette its colors, on the main thread.
    /// </summary>
    const int LocalOffersPerTick = 4;

    /// <summary>
    /// Give the colors to the palette of a section that a server captured. That server had
    /// no texture atlas, and it stored 0 for each color (DESIGN.md section 10.4).
    ///
    /// The deserializer found the block ids from the codes already. Thus this method needs
    /// the atlas only.
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
    /// The client half of the palette registration. It gives the average color from the
    /// texture atlas, with no tint, and the live tint that applies.
    ///
    /// The mod stores the color with no tint on purpose. Thus the shader can follow the
    /// calendar. Without that, the color holds the season of the capture forever.
    ///
    /// A server has no atlas, and it cannot answer this at all (DESIGN.md section 10.4).
    /// </summary>
    (int Color, byte TintSlot) DescribePalette(int blockId, int cx, int cz, int sampleY)
    {
        Block block = capi.World.Blocks[blockId];
        paletteSamplePos.Set(cx * ChunkSize + ChunkSize / 2, sampleY, cz * ChunkSize + ChunkSize / 2);
        int color = block.GetColorWithoutTint(capi, paletteSamplePos);

        if (!IsUsableAtlasTexture(block.TextureSubIdForBlockColor)
            // The guard tests for a value other than zero. If the atlas never filled
            // AvgColor, the comparison is against 0. Then the mod "corrects" each block that
            // is correctly black.
            || (unknownTextureColor != 0 && color == unknownTextureColor))
        {
            color = ColorFromAnyTexture(block, color);
        }
        return (color, (byte)tints.SlotFor(block));
    }

    /// <summary>The average color of unknown.png. It is near white, and it is not magenta.
    /// This was measured.</summary>
    int unknownTextureColor;

    /// <summary>
    /// Whether a sub-id of the atlas names a real texture.
    ///
    /// `GetAverageColor` on a sub-id with no assignment, or on a sub-id out of range, reads
    /// what the atlas holds at that position. That is the source of an incorrect LOD color.
    ///
    /// This test is better than the test for unknown.png, because it does not need any
    /// knowledge of the appearance of the placeholder.
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
    /// Find a color for a block whose block-color texture did not resolve. Thus the mod
    /// draws that block as itself. It does not draw a placeholder, or the content of the
    /// atlas at an incorrect id.
    ///
    /// Vanilla selects that texture in Block.LoadTextureSubIdForBlockColor. It tries the
    /// attribute 'textureCodeForBlockColor', then "up", then `Textures.First()`. That last
    /// step ends in `?? 0`.
    ///
    /// Thus a block whose first texture in dictionary order has no Baked entry resolves to
    /// atlas sub-id 0, which is unknown.png. No message occurs. The other faces of the block
    /// bake correctly. That is the reason why the block looks correct near the player, and
    /// magenta at LOD distance only.
    ///
    /// This was measured on the vanilla block 'fruitingbush-wild-blackberry-free'. Thus it is
    /// not a problem of modded content. A block pack with much content only meets it more
    /// often.
    ///
    /// The solution is to use any baked texture of the block, and not the first one. The
    /// result is cached for each block id. The answer cannot change during a session, and the
    /// mod registers a palette entry one time for each section, which is thousands of times
    /// for each world.
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
    /// Find a block that uses the standard plant tint. Thus a plant that declares no color
    /// map, such as a fern, can use that tint. Without it, the mod draws the greyscale
    /// texture of the plant.
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
        // Read this one time here, and not for each palette entry. The atlas exists now. A
        // reload changes the position object, but it does not change the appearance of
        // magenta.
        unknownTextureColor = capi.BlockTextureAtlas.UnknownTexturePosition.AvgColor;
        Mod.Logger.Debug("Missing-texture colour is {0:X8}{1}", unknownTextureColor,
            unknownTextureColor == 0 ? " (zero: magenta-block salvage disabled)" : "");
        renderer.ApplyZFar();
        pipeline.Open("ModData/vintagehorizons");
        joinClock.Restart();
        nextMilestone = 0;

        // In a singleplayer world, the server side sweeps the savegame and leaves the
        // results in a cache beside this one. There is nothing to open on a dedicated
        // server, where the same sections arrive over the network.
        if (pipeline.DbPath is string dbPath)
        {
            localOffers = LodLocalOfferSource.TryOpen(dbPath, Mod.Logger);
        }

        // Do this last, and after the pipeline runs. An exception in a LevelFinalize handler
        // skips each remaining step of that handler.
        //
        // Thus an optional extra must not come before the real work of the mod. It did come
        // before, and it broke exactly the vanilla-server case that it exists to leave
        // alone.
        assist?.Greet();

        Mod.Logger.Notification(
            "Level finalized. LOD capture active (render distance: unlimited, {0} sections from cache{1}).",
            pipeline.CachedSectionsLoaded, renderer.AutoUnpause ? ", auto-unpause on" : "");

        capi.Event.RegisterCallback(_ => LogStats("Stats after 30s"), 30000);

        // Continuous telemetry. This used AutoUnpause before. Thus an *attended* session had
        // no continuous numbers, and an attended session is the only kind where a person can
        // say "this looks wrong". It has its own switch now. Thus watching and driving are
        // separate.
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

        // A square spiral. Each second turn makes the legs longer.
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
        // This belongs to the world that the player leaves. The next world is a different
        // savegame with a different cache beside it. An open handle here also keeps a file
        // handle on a database that the server side can delete or replace.
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

        // The remaining commands drive the renderer. That renderer does not exist while this
        // mod defers to another LOD mod.
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

    /// <summary>Write each setting. A partial write returns the other settings to their
    /// defaults, and it gives no message.</summary>
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
            // Stop the storage writer before the connection that it writes through.
            pipeline?.Dispose();
            renderer?.Dispose();
        }
    }
}
