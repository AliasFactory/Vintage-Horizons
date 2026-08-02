using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Re-probes positions that a bulk pass promised to leave absent from the savegame.
///
/// The sweep promises to generate nothing. Generation promises that a peek persists
/// nothing. The matrix tier tests both promises, but only against vanilla worldgen on
/// one machine. This class measures them at the end of every run, on every server,
/// with whatever worldgen mods are installed - about one second for a full sample at
/// the measured ~245 probes per second.
///
/// One source of false alarms is filtered out first: the engine generates terrain
/// around each connected player as normal play, so a sampled position inside a
/// player's view radius can come to exist for that reason. Those positions are skipped
/// and reported as skipped, not counted either way.
/// </summary>
public class LodAbsenceVerifier
{
    /// <summary>Sample size cap. Big enough to catch a systematic leak, small enough to finish in seconds.</summary>
    public const int MaxSample = 256;

    readonly ICoreServerAPI sapi;
    readonly List<long> sample;
    readonly Action<LodAbsenceVerifier> onDone;
    int pending;

    /// <summary>Positions re-probed, after the player-proximity filter.</summary>
    public int Checked { get; private set; }

    /// <summary>Positions that existed at re-probe although the pass must not create them.</summary>
    public int Regrown { get; private set; }

    /// <summary>Positions skipped because a player was near enough to explain growth.</summary>
    public int SkippedNearPlayers { get; private set; }

    public LodAbsenceVerifier(ICoreServerAPI sapi, List<long> sample, Action<LodAbsenceVerifier> onDone)
    {
        this.sapi = sapi;
        this.sample = sample;
        this.onDone = onDone;
    }

    public void Start()
    {
        int cs = GlobalConstants.ChunkSize;
        int exclusion = sapi.Server.Config.MaxChunkRadius + 2;
        var players = new List<(int cx, int cz)>();
        foreach (var player in sapi.World.AllOnlinePlayers)
        {
            var pos = player.Entity?.Pos;
            if (pos != null) players.Add(((int)pos.X / cs, (int)pos.Z / cs));
        }

        var toCheck = new List<long>(sample.Count);
        foreach (long key in sample)
        {
            int cx = LodColumnMap.KeyCx(key), cz = LodColumnMap.KeyCz(key);
            bool near = false;
            foreach ((int px, int pz) in players)
            {
                if (Math.Max(Math.Abs(px - cx), Math.Abs(pz - cz)) <= exclusion) { near = true; break; }
            }
            if (near) SkippedNearPlayers++;
            else toCheck.Add(key);
        }

        pending = toCheck.Count;
        if (pending == 0)
        {
            onDone(this);
            return;
        }

        foreach (long key in toCheck)
        {
            int cx = LodColumnMap.KeyCx(key), cz = LodColumnMap.KeyCz(key);
            sapi.WorldManager.TestMapChunkExists(cx, cz, hit =>
            {
                // The callback need not be on the main thread, and the counters are not safe.
                sapi.Event.EnqueueMainThreadTask(() =>
                {
                    Checked++;
                    if (hit) Regrown++;
                    if (--pending == 0) onDone(this);
                }, "vh-absence-verify");
            });
        }
    }

    /// <summary>The clause both finish lines append. The matrix tier asserts on it.</summary>
    public string Describe() =>
        $"Verified {Checked - Regrown}/{Checked} sampled absent positions still absent"
        + (SkippedNearPlayers > 0 ? $" ({SkippedNearPlayers} skipped near players)" : "");
}
