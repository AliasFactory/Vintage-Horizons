using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// The server side's cache, read by the client side of the same singleplayer world.
///
/// A savegame sweep can only run on the server side, because only it can ask for chunk
/// columns the player is nowhere near. But the server has no texture atlas, so what it
/// captures is geometry with every palette colour left at zero. The client has the atlas
/// and cannot reach the columns. Each half holds exactly what the other is missing.
///
/// So the swept sections travel the same road as sections fetched from a real server: they
/// arrive colourless, get recoloured from their block codes on install, and land in the
/// client's own cache. LodRemoteKeySet does not care whether a blob came off a socket or
/// off the disk beside it, which is why this is a reader and not a subsystem.
///
/// Deliberately NOT a LodStore. That class creates tables and can delete rows whose format
/// version it does not recognise, and pointing it at a file another pipeline has open for
/// writing is a good way to find out what SQLite does about two writers. This only ever
/// reads, and says so in the connection string.
/// </summary>
public sealed class LodLocalOfferSource : IDisposable
{
    readonly SqliteConnection conn;
    readonly ILogger logger;

    LodLocalOfferSource(SqliteConnection conn, ILogger logger)
    {
        this.conn = conn;
        this.logger = logger;
    }

    /// <summary>
    /// Opens the server-side cache beside a client cache, or returns null when there is
    /// none - which is the normal case, since only a singleplayer world that has swept has
    /// one. Never throws: this is an optional extra and must not take a join down with it.
    /// </summary>
    public static LodLocalOfferSource? TryOpen(string clientDbPath, ILogger logger)
    {
        string path = Path.ChangeExtension(clientDbPath, null) + "-server.db";
        if (!File.Exists(path)) return null;

        try
        {
            // Read-only, and shared: in singleplayer the server side of this same process
            // has the file open and is very likely still writing to it.
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
            };
            var opened = new SqliteConnection(builder.ToString());
            opened.Open();
            return new LodLocalOfferSource(opened, logger);
        }
        catch (Exception e)
        {
            logger.Warning("Could not read the server-side LOD cache at {0}: {1}", path, e.Message);
            return null;
        }
    }

    /// <summary>Every section the server side holds, as packed keys.</summary>
    public long[] Keys()
    {
        try
        {
            var keys = new List<long>();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Detail, SX, SZ FROM Section";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                keys.Add(LodWorld.SectionKey(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
            }
            return keys.ToArray();
        }
        catch (Exception e)
        {
            logger.Warning("Could not list server-side LOD sections: {0}", e.Message);
            return Array.Empty<long>();
        }
    }

    /// <summary>
    /// One section's stored blob, or null when it is not there. A miss is ordinary rather
    /// than exceptional: the sweep is very likely still running and writing more.
    /// </summary>
    public byte[]? Blob(long key)
    {
        try
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Data FROM Section WHERE Detail=@d AND SX=@x AND SZ=@z";
            cmd.Parameters.AddWithValue("@d", LodWorld.KeyLevel(key));
            cmd.Parameters.AddWithValue("@x", LodWorld.KeySx(key));
            cmd.Parameters.AddWithValue("@z", LodWorld.KeySz(key));
            return cmd.ExecuteScalar() as byte[];
        }
        catch (Exception e)
        {
            logger.Warning("Could not read a server-side LOD section: {0}", e.Message);
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            conn.Close();
            conn.Dispose();
        }
        catch (Exception)
        {
            // Closing a read-only connection is not worth reporting a failure over.
        }
    }
}
