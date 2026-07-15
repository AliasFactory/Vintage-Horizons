using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// Per-world SQLite cache for LOD sections, built on the game's own SQLiteDBConnection
/// (bundled Microsoft.Data.Sqlite — no external dependencies). One row per
/// (detail level, section). Palettes store block CODES, not ids — ids are savegame-
/// local and can shift across game/mod updates (DH's lesson). The ApplyToParent flag
/// persists the mip-propagation queue so pyramid consistency survives crashes.
/// </summary>
public class LodStore : SQLiteDBConnection
{
    const byte BlobFormatVersion = 4;

    /// <summary>Bump when stored data SEMANTICS change; old rows are purged.</summary>
    const string SchemaVersion = "4";

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
                CREATE TABLE IF NOT EXISTS Section (
                    Detail INTEGER NOT NULL,
                    SX INTEGER NOT NULL,
                    SZ INTEGER NOT NULL,
                    Data BLOB NOT NULL,
                    ApplyToParent INTEGER NOT NULL DEFAULT 0,
                    ModifiedMs INTEGER NOT NULL,
                    PRIMARY KEY (Detail, SX, SZ)
                );
                DROP TABLE IF EXISTS Region;
                DROP TABLE IF EXISTS Region2;";
            cmd.ExecuteNonQuery();
        }

        PurgeOutdatedData(sqliteConn);
    }

    void PurgeOutdatedData(SqliteConnection sqliteConn)
    {
        using (var check = sqliteConn.CreateCommand())
        {
            check.CommandText = "SELECT Value FROM Meta WHERE Key='FormatVersion'";
            if (check.ExecuteScalar() as string == SchemaVersion) return;
        }

        logger.Notification("[VintageHorizons] LOD cache semantics changed; discarding old cached data");
        using var cmd = sqliteConn.CreateCommand();
        cmd.CommandText = "DELETE FROM Section; INSERT OR REPLACE INTO Meta (Key, Value) VALUES ('FormatVersion', '" + SchemaVersion + "');";
        cmd.ExecuteNonQuery();
    }

    public override void OnOpened()
    {
        base.OnOpened();

        upsertCmd = sqliteConn.CreateCommand();
        upsertCmd.CommandText =
            "INSERT OR REPLACE INTO Section (Detail, SX, SZ, Data, ApplyToParent, ModifiedMs) " +
            "VALUES (@detail, @sx, @sz, @data, @atp, @ms)";
        upsertCmd.Parameters.Add("@detail", SqliteType.Integer);
        upsertCmd.Parameters.Add("@sx", SqliteType.Integer);
        upsertCmd.Parameters.Add("@sz", SqliteType.Integer);
        upsertCmd.Parameters.Add("@data", SqliteType.Blob);
        upsertCmd.Parameters.Add("@atp", SqliteType.Integer);
        upsertCmd.Parameters.Add("@ms", SqliteType.Integer);
        upsertCmd.Prepare();
    }

    public void SaveSection(int level, int sx, int sz, LodSection section, IWorldAccessor world, bool applyToParent)
    {
        if (upsertCmd == null) return;

        lock (transactionLock)
        {
            upsertCmd.Parameters["@detail"].Value = level;
            upsertCmd.Parameters["@sx"].Value = sx;
            upsertCmd.Parameters["@sz"].Value = sz;
            upsertCmd.Parameters["@data"].Value = Serialize(section, world);
            upsertCmd.Parameters["@atp"].Value = applyToParent ? 1 : 0;
            upsertCmd.Parameters["@ms"].Value = Environment.TickCount64;
            upsertCmd.ExecuteNonQuery();
        }
    }

    public int LoadAllSections(IWorldAccessor world, Action<int, int, int, LodSection, bool> onSection)
    {
        int count = 0;
        lock (transactionLock)
        {
            using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText = "SELECT Detail, SX, SZ, Data, ApplyToParent FROM Section";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int level = reader.GetInt32(0);
                int sx = reader.GetInt32(1);
                int sz = reader.GetInt32(2);
                var blob = (byte[])reader[3];
                bool applyToParent = reader.GetInt32(4) != 0;

                LodSection? section = Deserialize(blob, world);
                if (section == null)
                {
                    logger.Warning("[VintageHorizons] Skipping unreadable cached section L{0} {1},{2}", level, sx, sz);
                    continue;
                }

                onSection(level, sx, sz, section, applyToParent);
                count++;
            }
        }
        return count;
    }

    static byte[] Serialize(LodSection section, IWorldAccessor world)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(BlobFormatVersion);

        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(deflate, Encoding.UTF8))
        {
            w.Write((ushort)section.Palette.Count);
            foreach (LodPaletteEntry e in section.Palette)
            {
                Block? block = e.BlockId > 0 ? world.Blocks[e.BlockId] : null;
                w.Write(block?.Code?.ToShortString() ?? "");
                w.Write(e.Color);
                w.Write(e.Flags);
            }

            int total = LodSection.GridSize * LodSection.GridSize;
            for (int col = 0; col < total; col++)
            {
                w.Write((ushort)section.RunCount(col));
            }
            foreach (ulong run in section.Runs) w.Write(run);

            var capturedBits = new byte[total / 8];
            for (int i = 0; i < total; i++)
            {
                if (section.Captured[i]) capturedBits[i >> 3] |= (byte)(1 << (i & 7));
            }
            w.Write(capturedBits);
        }

        return ms.ToArray();
    }

    static LodSection? Deserialize(byte[] blob, IWorldAccessor world)
    {
        if (blob.Length < 2 || blob[0] != BlobFormatVersion) return null;

        try
        {
            using var ms = new MemoryStream(blob, 1, blob.Length - 1);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var r = new BinaryReader(deflate, Encoding.UTF8);

            var section = new LodSection();

            int paletteCount = r.ReadUInt16();
            for (int i = 0; i < paletteCount; i++)
            {
                string code = r.ReadString();
                int color = r.ReadInt32();
                byte flags = r.ReadByte();

                int blockId = 0;
                if (code.Length > 0)
                {
                    Block? block = world.GetBlock(new Vintagestory.API.Common.AssetLocation(code));
                    blockId = block?.BlockId ?? 0;
                }
                section.Palette.Add(new LodPaletteEntry { BlockId = blockId, Color = color, Flags = flags });
            }

            int total = LodSection.GridSize * LodSection.GridSize;
            var counts = new ushort[total];
            int runTotal = 0;
            for (int col = 0; col < total; col++)
            {
                counts[col] = r.ReadUInt16();
                runTotal += counts[col];
            }

            section.Runs = new ulong[runTotal];
            for (int i = 0; i < runTotal; i++) section.Runs[i] = r.ReadUInt64();

            int offset = 0;
            for (int col = 0; col < total; col++)
            {
                section.ColumnStart[col] = offset;
                offset += counts[col];
            }
            section.ColumnStart[total] = offset;

            var capturedBits = r.ReadBytes(total / 8);
            for (int i = 0; i < total; i++)
            {
                if ((capturedBits[i >> 3] & (1 << (i & 7))) != 0)
                {
                    section.Captured[i] = true;
                    section.CapturedColumns++;
                }
            }

            return section;
        }
        catch
        {
            return null;
        }
    }

    public override void Close()
    {
        upsertCmd?.Dispose();
        upsertCmd = null;
        base.Close();
    }
}
