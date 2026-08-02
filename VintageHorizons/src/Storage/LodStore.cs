using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// The SQLite cache of LOD sections, one for each world. It uses the SQLiteDBConnection of
/// the game, which is the bundled Microsoft.Data.Sqlite. Thus it needs no external
/// dependency. There is one row for each pair of a detail level and a section.
///
/// A palette stores block CODES, and not ids. An id belongs to one savegame, and it can
/// change when the game or a mod updates. Distant Horizons taught this lesson.
///
/// The ApplyToParent flag stores the queue for the mip propagation. Thus the pyramid stays
/// consistent after a crash.
/// </summary>
public class LodStore : SQLiteDBConnection
{
    const byte BlobFormatVersion = 4;

    /// <summary>Increase this when the MEANING of the stored data changes. The mod then
    /// deletes the old rows.</summary>
    const string SchemaVersion = "6"; // v6: palette colors are now UNTINTED + tint-class flags (v5: 1-block leaves)

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
        string? existing;
        using (var check = sqliteConn.CreateCommand())
        {
            check.CommandText = "SELECT Value FROM Meta WHERE Key='FormatVersion'";
            existing = check.ExecuteScalar() as string;
        }
        if (existing == SchemaVersion) return;

        // Give this message only when a cache held an older format. A new database has no
        // FormatVersion row at all. Thus the test below is also unequal on a first run.
        //
        // A message that says the mod discards the data of a user, before that user has
        // any data, is alarming and incorrect. The write occurs in both cases. Only the
        // message has a condition.
        if (existing != null)
        {
            logger.Notification(
                "[VintageHorizons] LOD cache format {0} is not ours ({1}); discarding old cached data",
                existing, SchemaVersion);
        }
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

    /// <summary>
    /// Write a section that the mod serialized already.
    ///
    /// Serialize does the deflate, on the storage thread, and outside each lock. The mod
    /// holds the lock for the row write only. Thus a load on the main thread never waits for
    /// a compression.
    /// </summary>
    public void SaveBlob(int level, int sx, int sz, byte[] data, bool applyToParent)
    {
        if (upsertCmd == null) return;

        lock (transactionLock)
        {
            upsertCmd.Parameters["@detail"].Value = level;
            upsertCmd.Parameters["@sx"].Value = sx;
            upsertCmd.Parameters["@sz"].Value = sz;
            upsertCmd.Parameters["@data"].Value = data;
            upsertCmd.Parameters["@atp"].Value = applyToParent ? 1 : 0;
            upsertCmd.Parameters["@ms"].Value = Environment.TickCount64;
            upsertCmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// A map from a block code to an id.
    ///
    /// There is one map for each store, on purpose. A block id belongs to one savegame. Thus
    /// a map that two worlds share gives the ids of the earlier world.
    ///
    /// The map is concurrent, because sections deserialize on the main thread and on the
    /// storage thread.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> blockIdByCode = new();

    SqliteCommand? loadOneCmd;

    /// <summary>Load one section row. The result is null when the row is absent, or when
    /// the mod cannot read it. The mod uses this to load a section again after it removed
    /// that section from RAM.</summary>
    /// <param name="resolveBlockIds">
    /// This is false for a call from the storage thread. Then the section keeps its palette
    /// codes, and the main thread finds the ids later. Thus no thread other than the main
    /// thread reads the block registry.
    /// </param>
    public LodSection? LoadSection(int level, int sx, int sz, IWorldAccessor world, bool resolveBlockIds = true)
    {
        lock (transactionLock)
        {
            if (loadOneCmd == null)
            {
                loadOneCmd = sqliteConn.CreateCommand();
                loadOneCmd.CommandText = "SELECT Data FROM Section WHERE Detail=@detail AND SX=@sx AND SZ=@sz";
                loadOneCmd.Parameters.Add("@detail", SqliteType.Integer);
                loadOneCmd.Parameters.Add("@sx", SqliteType.Integer);
                loadOneCmd.Parameters.Add("@sz", SqliteType.Integer);
                loadOneCmd.Prepare();
            }

            loadOneCmd.Parameters["@detail"].Value = level;
            loadOneCmd.Parameters["@sx"].Value = sx;
            loadOneCmd.Parameters["@sz"].Value = sz;

            object? blob = loadOneCmd.ExecuteScalar();
            if (blob is not byte[] bytes) return null;

            LodSection? section = Deserialize(bytes, resolveBlockIds ? world : null);
            if (section == null)
            {
                // Data that the mod cannot read must not stay. It makes a later session
                // slower, and it confuses a person who examines the cache. Delete it
                // immediately. The mod captures that area again when a player explores
                // it.
                logger.Warning("[VintageHorizons] Deleting unreadable cached section L{0} {1},{2}", level, sx, sz);
                DeleteSection(level, sx, sz);
            }
            return section;
        }
    }

    /// <summary>
    /// The stored blob, without a parse.
    ///
    /// The wire format is the storage format. Thus a section that the server gives over the
    /// network is a blob read and nothing more. The server does not deserialize it and
    /// serialize it again, because the server never looks inside it.
    /// </summary>
    public byte[]? LoadBlob(int level, int sx, int sz)
    {
        lock (transactionLock)
        {
            if (loadBlobCmd == null)
            {
                loadBlobCmd = sqliteConn.CreateCommand();
                loadBlobCmd.CommandText = "SELECT Data FROM Section WHERE Detail=@detail AND SX=@sx AND SZ=@sz";
                loadBlobCmd.Parameters.Add("@detail", SqliteType.Integer);
                loadBlobCmd.Parameters.Add("@sx", SqliteType.Integer);
                loadBlobCmd.Parameters.Add("@sz", SqliteType.Integer);
                loadBlobCmd.Prepare();
            }

            loadBlobCmd.Parameters["@detail"].Value = level;
            loadBlobCmd.Parameters["@sx"].Value = sx;
            loadBlobCmd.Parameters["@sz"].Value = sz;

            return loadBlobCmd.ExecuteScalar() as byte[];
        }
    }

    SqliteCommand? loadBlobCmd;

    /// <summary>
    /// Parse a blob from a source other than this database, which is a blob from the
    /// network. This uses the same reader as the disk path. Thus a section that came over
    /// the wire is the same as a section from the local disk.
    /// </summary>
    public LodSection? DeserializeForeign(byte[] blob, IWorldAccessor? world) => Deserialize(blob, world);

    /// <summary>
    /// Complete a section that another thread deserialized, by finding the block ids of its
    /// palette.
    ///
    /// CAUTION: This method must run on the main thread, because it reads the block
    /// registry.
    /// </summary>
    /// <summary>
    /// Calculate the flags and the tint slot of a palette entry again, from the live block.
    /// The coordinator sets this delegate. It runs on the main thread only.
    /// </summary>
    public System.Func<int, (byte Flags, byte TintSlot)>? ClassifyBlock;

    void Reclassify(LodSection section, int index, int blockId)
    {
        if (ClassifyBlock == null || blockId <= 0) return;

        (byte flags, byte slot) = ClassifyBlock(blockId);
        LodPaletteEntry e = section.Palette[index];
        e.Flags = flags;
        e.TintSlot = slot;
        section.Palette[index] = e;
    }

    public void ResolvePendingPalette(LodSection section, IWorldAccessor world)
    {
        string[]? codes = section.PendingPaletteCodes;
        if (codes == null) return;

        for (int i = 0; i < codes.Length && i < section.Palette.Count; i++)
        {
            string code = codes[i];
            if (code.Length == 0) continue;

            if (!blockIdByCode.TryGetValue(code, out int blockId))
            {
                Block? block = world.GetBlock(new Vintagestory.API.Common.AssetLocation(code));
                blockId = block?.BlockId ?? 0;
                blockIdByCode[code] = blockId;
            }

            LodPaletteEntry e = section.Palette[i];
            e.BlockId = blockId;
            section.Palette[i] = e;
            Reclassify(section, i, blockId);
        }

        section.PendingPaletteCodes = null;
    }

    void DeleteSection(int level, int sx, int sz)
    {
        using var cmd = sqliteConn.CreateCommand();
        cmd.CommandText = "DELETE FROM Section WHERE Detail=@detail AND SX=@sx AND SZ=@sz";
        cmd.Parameters.AddWithValue("@detail", level);
        cmd.Parameters.AddWithValue("@sx", sx);
        cmd.Parameters.AddWithValue("@sz", sz);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Give the KEYS of the stored sections only. This parses no blob.
    ///
    /// Thus the cost at join increases with the count of the explored areas, and not with
    /// the size of the data. The mod loads the data of a section when the renderer or the
    /// pipeline first needs it.
    /// </summary>
    public int LoadAllKeys(Action<int, int, int, bool> onKey)
    {
        int count = 0;
        lock (transactionLock)
        {
            using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText = "SELECT Detail, SX, SZ, ApplyToParent FROM Section";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                onKey(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3) != 0);
                count++;
            }
        }
        return count;
    }

    /// <summary>This method is safe for more than one thread. It reads the private arrays
    /// of the snapshot only. It never reads live world state.</summary>
    public static byte[] Serialize(LodSaveSnapshot snap)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(BlobFormatVersion);

        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(deflate, Encoding.UTF8))
        {
            w.Write((ushort)snap.PaletteCodes.Length);
            for (int i = 0; i < snap.PaletteCodes.Length; i++)
            {
                w.Write(snap.PaletteCodes[i]);
                w.Write(snap.PaletteColors[i]);
                w.Write(snap.PaletteFlags[i]);
            }

            int total = LodSection.GridSize * LodSection.GridSize;
            for (int col = 0; col < total; col++)
            {
                w.Write((ushort)snap.RunCount(col));
            }
            foreach (ulong run in snap.Runs) w.Write(run);

            var capturedBits = new byte[total / 8];
            for (int i = 0; i < total; i++)
            {
                if (snap.Captured[i]) capturedBits[i >> 3] |= (byte)(1 << (i & 7));
            }
            w.Write(capturedBits);
        }

        return ms.ToArray();
    }

    /// <param name="world">Give null to leave the block ids for the main thread.</param>
    LodSection? Deserialize(byte[] blob, IWorldAccessor? world)
    {
        if (blob.Length < 2 || blob[0] != BlobFormatVersion) return null;

        try
        {
            using var ms = new MemoryStream(blob, 1, blob.Length - 1);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var r = new BinaryReader(deflate, Encoding.UTF8);

            var section = new LodSection();

            int paletteCount = r.ReadUInt16();
            string[]? deferredCodes = world == null ? new string[paletteCount] : null;

            for (int i = 0; i < paletteCount; i++)
            {
                string code = r.ReadString();
                int color = r.ReadInt32();
                byte flags = r.ReadByte();

                int blockId = 0;
                if (deferredCodes != null)
                {
                    deferredCodes[i] = code;
                }
                else if (code.Length > 0 && !blockIdByCode.TryGetValue(code, out blockId))
                {
                    // The result is cached. One session loads the same few hundred codes
                    // again, across thousands of sections. Each miss costs an AssetLocation
                    // parse and a registry lookup.
                    Block? block = world!.GetBlock(new Vintagestory.API.Common.AssetLocation(code));
                    blockId = block?.BlockId ?? 0;
                    blockIdByCode[code] = blockId;
                }
                section.Palette.Add(new LodPaletteEntry { BlockId = blockId, Color = color, Flags = flags });

                // The stored flags are older than the tint slots for each species. A game
                // update can also move a block to a different color map, and then those
                // flags become incorrect. Thus the live block is the authority, whenever the
                // mod can find it here.
                if (deferredCodes == null) Reclassify(section, section.Palette.Count - 1, blockId);
            }

            section.PendingPaletteCodes = deferredCodes;

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
        loadOneCmd?.Dispose();
        loadOneCmd = null;
        base.Close();
    }
}
