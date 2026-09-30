using Microsoft.Data.Sqlite;

namespace SwVault.Core.State;

/// <summary>What this PC knows about one workspace file.</summary>
public sealed class FileRecord
{
    public string Path { get; set; } = "";

    /// <summary>The server version the local content came from (LFS oid, size, version number).</summary>
    public string? BaseOid { get; set; }
    public long BaseSize { get; set; }
    public int BaseVersion { get; set; }
    public string? BaseCommit { get; set; }

    /// <summary>File size and mtime right after SwVault wrote or checked in the file.</summary>
    public long? SnapSize { get; set; }
    public long? SnapMtime { get; set; }

    /// <summary>Cached content hash for a given size+mtime, so modified files are hashed once per save.</summary>
    public long? HashSize { get; set; }
    public long? HashMtime { get; set; }
    public string? HashOid { get; set; }

    /// <summary>True when the check-out lock was taken from this PC.</summary>
    public bool LockedHere { get; set; }
}

public sealed record LockRecord(string Id, string Path, string Owner, string? LockedAt, bool Mine);

public sealed record JobRecord(string Id, string Kind, string State, string Data);

public enum BlobKind
{
    Pointer = 1,
    Meta = 2,
    Raw = 3,
}

/// <summary>Local SQLite state for one vault on this PC.</summary>
public sealed class StateStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _sync = new();

    public StateStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS files (
                path TEXT PRIMARY KEY COLLATE NOCASE,
                base_oid TEXT, base_size INTEGER NOT NULL DEFAULT 0, base_version INTEGER NOT NULL DEFAULT 0, base_commit TEXT,
                snap_size INTEGER, snap_mtime INTEGER,
                hash_size INTEGER, hash_mtime INTEGER, hash_oid TEXT,
                locked_here INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS blob_cache (sha TEXT PRIMARY KEY, kind INTEGER NOT NULL, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS locks (id TEXT PRIMARY KEY, path TEXT NOT NULL COLLATE NOCASE, owner TEXT NOT NULL, locked_at TEXT, mine INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS jobs (id TEXT PRIMARY KEY, kind TEXT NOT NULL, state TEXT NOT NULL, data TEXT NOT NULL);
            """);
    }

    public FileRecord? GetFile(string path)
    {
        lock (_sync)
        {
            using var cmd = Command("SELECT * FROM files WHERE path = $p", ("$p", path));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadFile(r) : null;
        }
    }

    public IReadOnlyList<FileRecord> GetAllFiles()
    {
        lock (_sync)
        {
            using var cmd = Command("SELECT * FROM files");
            using var r = cmd.ExecuteReader();
            var list = new List<FileRecord>();
            while (r.Read()) list.Add(ReadFile(r));
            return list;
        }
    }

    public void UpsertFile(FileRecord f)
    {
        lock (_sync)
        {
            using var cmd = Command("""
                INSERT INTO files (path, base_oid, base_size, base_version, base_commit, snap_size, snap_mtime, hash_size, hash_mtime, hash_oid, locked_here)
                VALUES ($path, $bo, $bs, $bv, $bc, $ss, $sm, $hs, $hm, $ho, $lh)
                ON CONFLICT(path) DO UPDATE SET path = excluded.path, base_oid = excluded.base_oid, base_size = excluded.base_size,
                    base_version = excluded.base_version, base_commit = excluded.base_commit, snap_size = excluded.snap_size,
                    snap_mtime = excluded.snap_mtime, hash_size = excluded.hash_size, hash_mtime = excluded.hash_mtime,
                    hash_oid = excluded.hash_oid, locked_here = excluded.locked_here
                """,
                ("$path", f.Path), ("$bo", f.BaseOid), ("$bs", f.BaseSize), ("$bv", f.BaseVersion), ("$bc", f.BaseCommit),
                ("$ss", f.SnapSize), ("$sm", f.SnapMtime), ("$hs", f.HashSize), ("$hm", f.HashMtime), ("$ho", f.HashOid),
                ("$lh", f.LockedHere ? 1 : 0));
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteFile(string path)
    {
        lock (_sync)
        {
            using var cmd = Command("DELETE FROM files WHERE path = $p", ("$p", path));
            cmd.ExecuteNonQuery();
        }
    }

    public Dictionary<string, (BlobKind Kind, string Data)> LoadBlobCache()
    {
        lock (_sync)
        {
            using var cmd = Command("SELECT sha, kind, data FROM blob_cache");
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, (BlobKind, string)>(StringComparer.Ordinal);
            while (r.Read()) map[r.GetString(0)] = ((BlobKind)r.GetInt32(1), r.GetString(2));
            return map;
        }
    }

    public void PutBlobCache(IEnumerable<(string Sha, BlobKind Kind, string Data)> entries)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            foreach (var (sha, kind, data) in entries)
            {
                using var cmd = Command("INSERT OR REPLACE INTO blob_cache (sha, kind, data) VALUES ($s, $k, $d)", ("$s", sha), ("$k", (int)kind), ("$d", data));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public IReadOnlyList<LockRecord> GetLocks()
    {
        lock (_sync)
        {
            using var cmd = Command("SELECT id, path, owner, locked_at, mine FROM locks");
            using var r = cmd.ExecuteReader();
            var list = new List<LockRecord>();
            while (r.Read())
                list.Add(new LockRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4) != 0));
            return list;
        }
    }

    public void ReplaceLocks(IEnumerable<LockRecord> locks)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using (var del = Command("DELETE FROM locks")) { del.Transaction = tx; del.ExecuteNonQuery(); }
            foreach (var l in locks) InsertLock(l, tx);
            tx.Commit();
        }
    }

    public void PutLock(LockRecord l)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using (var del = Command("DELETE FROM locks WHERE path = $p OR id = $i", ("$p", l.Path), ("$i", l.Id))) { del.Transaction = tx; del.ExecuteNonQuery(); }
            InsertLock(l, tx);
            tx.Commit();
        }
    }

    public void RemoveLock(string path)
    {
        lock (_sync)
        {
            using var cmd = Command("DELETE FROM locks WHERE path = $p", ("$p", path));
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertLock(LockRecord l, SqliteTransaction tx)
    {
        using var cmd = Command("INSERT OR REPLACE INTO locks (id, path, owner, locked_at, mine) VALUES ($i, $p, $o, $a, $m)",
            ("$i", l.Id), ("$p", l.Path), ("$o", l.Owner), ("$a", l.LockedAt), ("$m", l.Mine ? 1 : 0));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public string? GetValue(string key)
    {
        lock (_sync)
        {
            using var cmd = Command("SELECT value FROM kv WHERE key = $k", ("$k", key));
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetValue(string key, string? value)
    {
        lock (_sync)
        {
            using var cmd = value == null
                ? Command("DELETE FROM kv WHERE key = $k", ("$k", key))
                : Command("INSERT OR REPLACE INTO kv (key, value) VALUES ($k, $v)", ("$k", key), ("$v", value));
            cmd.ExecuteNonQuery();
        }
    }

    public void PutJob(JobRecord job)
    {
        lock (_sync)
        {
            using var cmd = Command("INSERT OR REPLACE INTO jobs (id, kind, state, data) VALUES ($i, $k, $s, $d)",
                ("$i", job.Id), ("$k", job.Kind), ("$s", job.State), ("$d", job.Data));
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveJob(string id)
    {
        lock (_sync)
        {
            using var cmd = Command("DELETE FROM jobs WHERE id = $i", ("$i", id));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<JobRecord> GetJobs()
    {
        lock (_sync)
        {
            using var cmd = Command("SELECT id, kind, state, data FROM jobs");
            using var r = cmd.ExecuteReader();
            var list = new List<JobRecord>();
            while (r.Read()) list.Add(new JobRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            return list;
        }
    }

    private static FileRecord ReadFile(SqliteDataReader r) => new()
    {
        Path = r.GetString(r.GetOrdinal("path")),
        BaseOid = NullableString(r, "base_oid"),
        BaseSize = r.GetInt64(r.GetOrdinal("base_size")),
        BaseVersion = r.GetInt32(r.GetOrdinal("base_version")),
        BaseCommit = NullableString(r, "base_commit"),
        SnapSize = NullableLong(r, "snap_size"),
        SnapMtime = NullableLong(r, "snap_mtime"),
        HashSize = NullableLong(r, "hash_size"),
        HashMtime = NullableLong(r, "hash_mtime"),
        HashOid = NullableString(r, "hash_oid"),
        LockedHere = r.GetInt32(r.GetOrdinal("locked_here")) != 0,
    };

    private static string? NullableString(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static long? NullableLong(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    private void Execute(string sql)
    {
        lock (_sync)
        {
            using var cmd = Command(sql);
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public void Dispose() => _db.Dispose();
}
