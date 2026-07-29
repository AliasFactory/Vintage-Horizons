using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// Per-world SQLite cache for LOD sections, built on the game's own SQLiteDBConnection
/// (bundled Microsoft.Data.Sqlite - no external dependencies). One row per
/// (detail level, section). Palettes store block CODES, not ids - ids are savegame-
/// local and can shift across game/mod updates (DH's lesson). The ApplyToParent flag
/// persists the mip-propagation queue so pyramid consistency survives crashes.
/// </summary>
public class LodStore : SQLiteDBConnection
{
    const byte BlobFormatVersion = 4;

    /// <summary>Bump when stored data SEMANTICS change; old rows are purged.</summary>
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

        // Only say this when a cache really did hold an older format. A brand new
        // database has no FormatVersion row at all, so the check below is unequal on a
        // first-ever run too - and announcing that we are discarding someone's data
        // before they have any is alarming and untrue. The write still happens either
        // way; it is the claim that is conditional.
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
    /// Write an already-serialized section. Deflate happens in Serialize (outside any
    /// lock, on the storage thread); the lock is held only for the row write, so a
    /// main-thread demand load never waits behind compression.
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
    /// Block code -> id. Per-store on purpose: block ids are savegame-local, so a
    /// cache shared between worlds would resolve the previous world's ids. Concurrent
    /// because sections deserialize on both the main and storage threads.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> blockIdByCode = new();

    SqliteCommand? loadOneCmd;

    /// <summary>Load a single section row, or null if absent/unreadable. Used for demand reload after RAM eviction.</summary>
    /// <param name="resolveBlockIds">
    /// False when called from the storage thread: palette codes are kept on the
    /// section and resolved later on the main thread, so the block registry is never
    /// read off-thread.
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
                // Unreadable data must never linger to slow down or confuse future
                // sessions - delete on sight; the area recaptures on exploration.
                logger.Warning("[VintageHorizons] Deleting unreadable cached section L{0} {1},{2}", level, sx, sz);
                DeleteSection(level, sx, sz);
            }
            return section;
        }
    }

    /// <summary>
    /// The stored blob, unparsed. The wire format is the storage format, so serving a
    /// section over the network is a blob read and nothing else - no deserialize and
    /// re-serialize round trip on the server, which never needs to look inside.
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
    /// Parse a blob that did not come from this database - i.e. one off the network.
    /// Same reader as the disk path, so a section that survives the wire is
    /// indistinguishable from one that was stored locally.
    /// </summary>
    public LodSection? DeserializeForeign(byte[] blob, IWorldAccessor? world) => Deserialize(blob, world);

    /// <summary>
    /// Finish a section that was deserialized off-thread by resolving its palette
    /// block ids. MUST run on the main thread - it reads the block registry.
    /// </summary>
    /// <summary>
    /// Recompute a palette entry's flags and tint slot from the live block. Set by the
    /// coordinator; runs on the main thread only.
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
    /// Enumerate stored section KEYS only - no blob parsing. Join-time cost stays
    /// proportional to explored area count, not data size; section data itself is
    /// demand-loaded when the renderer or pipeline first needs it.
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

    /// <summary>Thread-safe: reads only the snapshot's private arrays, never live world state.</summary>
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

    /// <param name="world">Null to defer block-id resolution to the main thread.</param>
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
                    // Cached: a session reloads the same few hundred codes across
                    // thousands of sections, each miss costing an AssetLocation parse
                    // plus a registry lookup.
                    Block? block = world!.GetBlock(new Vintagestory.API.Common.AssetLocation(code));
                    blockId = block?.BlockId ?? 0;
                    blockIdByCode[code] = blockId;
                }
                section.Palette.Add(new LodPaletteEntry { BlockId = blockId, Color = color, Flags = flags });

                // Stored flags predate per-species tint slots (and can go stale if a game
                // update moves a block to a different colour map), so the live block is
                // the authority whenever we can resolve it here.
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
