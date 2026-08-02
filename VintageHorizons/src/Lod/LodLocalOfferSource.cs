using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// The cache of the server side, which the client side of the same singleplayer world reads.
///
/// A savegame sweep can run only on the server side. Only that side can ask for a chunk
/// column that the player is far from. But the server has no texture atlas. Thus what it
/// captures is geometry with each palette color at zero. The client has the atlas, and it
/// cannot reach those columns. Each half holds exactly what the other half does not have.
///
/// Thus a swept section travels the same road as a section from a real server. It arrives
/// with no color. The mod gives it a color again from its block codes at install. Then it
/// goes into the cache of the client.
///
/// LodRemoteKeySet does not care whether a blob arrived over a socket or from the disk beside
/// it. That is the reason why this class is a reader, and not a subsystem.
///
/// CAUTION: This class is deliberately NOT a LodStore. LodStore creates tables, and it can
/// delete a row whose format version it does not know. Another pipeline has this file open
/// for writing. Two writers on one SQLite file cause damage. This class only reads, and the
/// connection string says so.
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
    /// Opens the cache of the server side, beside a cache of the client. The result is null
    /// when no such cache exists. That is the normal case, because only a singleplayer world
    /// that swept has one.
    ///
    /// This method never throws. It is an optional extra, and it must not stop a join.
    /// </summary>
    public static LodLocalOfferSource? TryOpen(string clientDbPath, ILogger logger)
    {
        string path = Path.ChangeExtension(clientDbPath, null) + "-server.db";
        if (!File.Exists(path)) return null;

        try
        {
            // The connection is read-only and shared. In singleplayer, the server side of
            // this same process has the file open, and it is probably still writing to
            // it.
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

    /// <summary>Each section that the server side holds, as packed keys.</summary>
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
    /// The stored blob of one section, or null when that section is absent. A miss is normal,
    /// and it is not an error. The sweep is probably still running, and it writes more
    /// sections.
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
            // A failure to close a read-only connection is not worth a message.
        }
    }
}
