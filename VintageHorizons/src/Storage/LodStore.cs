using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// Per-world SQLite cache for LOD regions, built on the game's own SQLiteDBConnection
/// (bundled Microsoft.Data.Sqlite — no external dependencies). One row per
/// (detail level, region); heights + colors + presence bitset are packed into a
/// single deflate-compressed blob. The ApplyToParent flag persists the mip-pyramid
/// propagation queue so it survives crashes (DH-style pull-based propagation).
/// </summary>
public class LodStore : SQLiteDBConnection
{
    const byte BlobFormatVersion = 1;

    /// <summary>Bump when stored data SEMANTICS change (not just schema); old rows are purged.</summary>
    const string SchemaVersion = "3";

    public override string DBTypeCode => "vintagehorizons lod cache";

    SqliteCommand? upsertCmd;

    public LodStore(ILogger logger) : base(logger)
    {
    }

    public bool Open(string filePath)
    {
        string error = "";
        bool ok = OpenOrCreate(filePath, ref error, true, true, false);
        if (!ok) logger.Error("[VintageHorizons] Could not open LOD cache {0}: {1}", filePath, error);
        return ok;
    }

    protected override void CreateTablesIfNotExists(SqliteConnection sqliteConn)
    {
        using (var cmd = sqliteConn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT);
                CREATE TABLE IF NOT EXISTS Region2 (
                    Detail INTEGER NOT NULL,
                    RX INTEGER NOT NULL,
                    RZ INTEGER NOT NULL,
                    Data BLOB NOT NULL,
                    ApplyToParent INTEGER NOT NULL DEFAULT 0,
                    ModifiedMs INTEGER NOT NULL,
                    PRIMARY KEY (Detail, RX, RZ)
                );
                DROP TABLE IF EXISTS Region;";
            cmd.ExecuteNonQuery();
        }

        PurgeOutdatedData(sqliteConn);
    }

    /// <summary>
    /// Cached data whose *meaning* predates the current sampling/mip rules is worse
    /// than no data (e.g. canopy-spike heights) — purge it and let exploration refill.
    /// </summary>
    void PurgeOutdatedData(SqliteConnection sqliteConn)
    {
        using (var check = sqliteConn.CreateCommand())
        {
            check.CommandText = "SELECT Value FROM Meta WHERE Key='FormatVersion'";
            if (check.ExecuteScalar() as string == SchemaVersion) return;
        }

        logger.Notification("[VintageHorizons] LOD cache semantics changed; discarding old cached regions");
        using var cmd = sqliteConn.CreateCommand();
        cmd.CommandText = "DELETE FROM Region2; INSERT OR REPLACE INTO Meta (Key, Value) VALUES ('FormatVersion', '" + SchemaVersion + "');";
        cmd.ExecuteNonQuery();
    }

    public override void OnOpened()
    {
        base.OnOpened();

        upsertCmd = sqliteConn.CreateCommand();
        upsertCmd.CommandText =
            "INSERT OR REPLACE INTO Region2 (Detail, RX, RZ, Data, ApplyToParent, ModifiedMs) " +
            "VALUES (@detail, @rx, @rz, @data, @atp, @ms)";
        upsertCmd.Parameters.Add("@detail", SqliteType.Integer);
        upsertCmd.Parameters.Add("@rx", SqliteType.Integer);
        upsertCmd.Parameters.Add("@rz", SqliteType.Integer);
        upsertCmd.Parameters.Add("@data", SqliteType.Blob);
        upsertCmd.Parameters.Add("@atp", SqliteType.Integer);
        upsertCmd.Parameters.Add("@ms", SqliteType.Integer);
        upsertCmd.Prepare();
    }

    public void SaveRegion(int level, int rx, int rz, LodRegion region, bool applyToParent)
    {
        if (upsertCmd == null) return;

        lock (transactionLock)
        {
            upsertCmd.Parameters["@detail"].Value = level;
            upsertCmd.Parameters["@rx"].Value = rx;
            upsertCmd.Parameters["@rz"].Value = rz;
            upsertCmd.Parameters["@data"].Value = Serialize(region);
            upsertCmd.Parameters["@atp"].Value = applyToParent ? 1 : 0;
            upsertCmd.Parameters["@ms"].Value = Environment.TickCount64;
            upsertCmd.ExecuteNonQuery();
        }
    }

    /// <summary>Streams every stored region to the callback. Returns the number loaded.</summary>
    public int LoadAllRegions(Action<int, int, int, LodRegion, bool> onRegion)
    {
        int count = 0;
        lock (transactionLock)
        {
            using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText = "SELECT Detail, RX, RZ, Data, ApplyToParent FROM Region2";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int level = reader.GetInt32(0);
                int rx = reader.GetInt32(1);
                int rz = reader.GetInt32(2);
                var blob = (byte[])reader[3];
                bool applyToParent = reader.GetInt32(4) != 0;

                LodRegion? region = Deserialize(blob);
                if (region == null)
                {
                    logger.Warning("[VintageHorizons] Skipping unreadable cached region L{0} {1},{2}", level, rx, rz);
                    continue;
                }

                onRegion(level, rx, rz, region, applyToParent);
                count++;
            }
        }
        return count;
    }

    static byte[] Serialize(LodRegion region)
    {
        int n = LodRegion.GridSize * LodRegion.GridSize;
        var raw = new byte[n * 4 + n * 4 + n / 8];
        Buffer.BlockCopy(region.Heights, 0, raw, 0, n * 4);
        Buffer.BlockCopy(region.Colors, 0, raw, n * 4, n * 4);
        for (int i = 0; i < n; i++)
        {
            if (region.HasData[i]) raw[n * 8 + (i >> 3)] |= (byte)(1 << (i & 7));
        }

        using var ms = new MemoryStream();
        ms.WriteByte(BlobFormatVersion);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    static LodRegion? Deserialize(byte[] blob)
    {
        if (blob.Length < 2 || blob[0] != BlobFormatVersion) return null;

        int n = LodRegion.GridSize * LodRegion.GridSize;
        var raw = new byte[n * 4 + n * 4 + n / 8];

        using (var ms = new MemoryStream(blob, 1, blob.Length - 1))
        using (var deflate = new DeflateStream(ms, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int got = deflate.Read(raw, read, raw.Length - read);
                if (got <= 0) return null;
                read += got;
            }
        }

        var region = new LodRegion();
        Buffer.BlockCopy(raw, 0, region.Heights, 0, n * 4);
        Buffer.BlockCopy(raw, n * 4, region.Colors, 0, n * 4);
        for (int i = 0; i < n; i++)
        {
            if ((raw[n * 8 + (i >> 3)] & (1 << (i & 7))) != 0)
            {
                region.HasData[i] = true;
                region.FilledSamples++;
            }
        }
        return region;
    }

    public override void Close()
    {
        upsertCmd?.Dispose();
        upsertCmd = null;
        base.Close();
    }
}
