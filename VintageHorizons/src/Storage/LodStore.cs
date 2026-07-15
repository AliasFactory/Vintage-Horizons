using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// Per-world SQLite cache for LOD regions, built on the game's own SQLiteDBConnection
/// (bundled Microsoft.Data.Sqlite — no external dependencies). One row per region;
/// heights + colors + presence bitset are packed into a single deflate-compressed blob.
/// </summary>
public class LodStore : SQLiteDBConnection
{
    const byte BlobFormatVersion = 1;

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
        using var cmd = sqliteConn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT);
            CREATE TABLE IF NOT EXISTS Region (
                RX INTEGER NOT NULL,
                RZ INTEGER NOT NULL,
                Data BLOB NOT NULL,
                ModifiedMs INTEGER NOT NULL,
                PRIMARY KEY (RX, RZ)
            );
            INSERT OR IGNORE INTO Meta (Key, Value) VALUES ('FormatVersion', '1');";
        cmd.ExecuteNonQuery();
    }

    public override void OnOpened()
    {
        base.OnOpened();

        upsertCmd = sqliteConn.CreateCommand();
        upsertCmd.CommandText = "INSERT OR REPLACE INTO Region (RX, RZ, Data, ModifiedMs) VALUES (@rx, @rz, @data, @ms)";
        upsertCmd.Parameters.Add("@rx", SqliteType.Integer);
        upsertCmd.Parameters.Add("@rz", SqliteType.Integer);
        upsertCmd.Parameters.Add("@data", SqliteType.Blob);
        upsertCmd.Parameters.Add("@ms", SqliteType.Integer);
        upsertCmd.Prepare();
    }

    public void SaveRegion(int rx, int rz, LodRegion region)
    {
        if (upsertCmd == null) return;

        lock (transactionLock)
        {
            upsertCmd.Parameters["@rx"].Value = rx;
            upsertCmd.Parameters["@rz"].Value = rz;
            upsertCmd.Parameters["@data"].Value = Serialize(region);
            upsertCmd.Parameters["@ms"].Value = Environment.TickCount64;
            upsertCmd.ExecuteNonQuery();
        }
    }

    /// <summary>Streams every stored region to the callback. Returns the number loaded.</summary>
    public int LoadAllRegions(Action<int, int, LodRegion> onRegion)
    {
        int count = 0;
        lock (transactionLock)
        {
            using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText = "SELECT RX, RZ, Data FROM Region";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int rx = reader.GetInt32(0);
                int rz = reader.GetInt32(1);
                var blob = (byte[])reader[2];

                LodRegion? region = Deserialize(blob);
                if (region == null)
                {
                    logger.Warning("[VintageHorizons] Skipping unreadable cached region {0},{1}", rx, rz);
                    continue;
                }

                onRegion(rx, rz, region);
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
