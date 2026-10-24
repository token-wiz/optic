using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace OPTIC.Services.Valuation;

/// <summary>
/// Storage for the liquidity and distribution review, in its own SQLite file (odata/valuation.db).
/// Migrations only ever add; nothing is dropped or rewritten. Records are owned by "system" (shared evidence,
/// written by admins) or by a user (their own receipts, statements, reviews and report versions). Deleting a record
/// marks it deleted; reviews and report versions are append-only.
/// </summary>
public sealed class ValuationStore
{
    public const string SystemOwner = "system";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _cs;
    private readonly object _write = new();

    // Each entry runs once, in order, recorded in schema_migrations.
    private static readonly (int Version, string Description, string Sql)[] Migrations =
    {
        (1, "records, reviews, report versions", @"
            CREATE TABLE IF NOT EXISTS records (
                kind TEXT NOT NULL, id TEXT NOT NULL, owner TEXT NOT NULL,
                effective_utc TEXT, json TEXT NOT NULL, sha256 TEXT,
                created_utc TEXT NOT NULL, created_by TEXT NOT NULL,
                deleted_utc TEXT, deleted_by TEXT,
                PRIMARY KEY (kind, id));
            CREATE INDEX IF NOT EXISTS ix_records_owner ON records(kind, owner, effective_utc);
            CREATE TABLE IF NOT EXISTS reviews (
                owner TEXT NOT NULL, review_id TEXT NOT NULL, version INTEGER NOT NULL,
                status TEXT NOT NULL, json TEXT NOT NULL, created_utc TEXT NOT NULL,
                PRIMARY KEY (owner, review_id, version));
            CREATE TABLE IF NOT EXISTS report_versions (
                owner TEXT NOT NULL, id TEXT NOT NULL, wallet TEXT NOT NULL, year INTEGER NOT NULL,
                review_fingerprint TEXT NOT NULL, status TEXT NOT NULL, json TEXT NOT NULL, created_utc TEXT NOT NULL,
                PRIMARY KEY (owner, id));
            CREATE TABLE IF NOT EXISTS evidence_files (
                sha256 TEXT PRIMARY KEY, name TEXT NOT NULL, bytes INTEGER NOT NULL, stored_path TEXT NOT NULL,
                source_url TEXT, collected_utc TEXT NOT NULL, collected_by TEXT NOT NULL);"),
        (2, "receipt classification history", @"
            CREATE TABLE IF NOT EXISTS classifications (
                owner TEXT NOT NULL, receipt_id TEXT NOT NULL, seq INTEGER NOT NULL,
                category TEXT NOT NULL, status TEXT NOT NULL, note TEXT, by_user TEXT NOT NULL, created_utc TEXT NOT NULL,
                PRIMARY KEY (owner, receipt_id, seq));"),
    };

    public ValuationStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        Migrate();
    }

    public string EvidenceDir => Path.Combine(Path.GetDirectoryName(new SqliteConnectionStringBuilder(_cs).DataSource)!, "valuation-evidence");

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA busy_timeout = 5000;";
        p.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        lock (_write)
        {
            using var c = Open();
            Exec(c, "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, description TEXT NOT NULL, applied_utc TEXT NOT NULL);");
            var applied = new HashSet<long>();
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT version FROM schema_migrations";
                using var r = q.ExecuteReader();
                while (r.Read()) applied.Add(r.GetInt64(0));
            }
            foreach (var (v, d, sql) in Migrations.Where(m => !applied.Contains(m.Version)).OrderBy(m => m.Version))
            {
                using var tx = c.BeginTransaction();
                Exec(c, sql, tx);
                Exec(c, "INSERT INTO schema_migrations(version, description, applied_utc) VALUES ($v, $d, $t)", tx, ("$v", v), ("$d", d), ("$t", Now()));
                tx.Commit();
            }
        }
    }

    public IReadOnlyList<int> AppliedMigrations()
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT version FROM schema_migrations ORDER BY version";
        using var r = q.ExecuteReader();
        var list = new List<int>();
        while (r.Read()) list.Add(r.GetInt32(0));
        return list;
    }

    // ------------------------------------------------------------------ permissions

    /// <summary>Users write their own records; only admins write shared ("system") evidence.</summary>
    public static bool CanWrite(string user, bool isAdmin, string owner) =>
        owner == SystemOwner ? isAdmin : string.Equals(owner, user, StringComparison.OrdinalIgnoreCase);

    /// <summary>Users read shared evidence and their own records, never another user's.</summary>
    public static bool CanRead(string user, string owner) =>
        owner == SystemOwner || string.Equals(owner, user, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ records

    public sealed record StoredRecord<T>(string Id, string Owner, T Value, DateTime CreatedUtc, string CreatedBy);

    public void Put<T>(string kind, string id, string owner, T value, string by, DateTime? effectiveUtc = null, string? sha256 = null)
    {
        lock (_write)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            // A replaced record keeps its earlier content as a deleted history row.
            Exec(c, @"UPDATE records SET id = id || '@' || $now, deleted_utc = $now, deleted_by = $by
                      WHERE kind = $k AND id = $id AND deleted_utc IS NULL", tx, ("$k", kind), ("$id", id), ("$now", Now()), ("$by", by));
            Exec(c, @"INSERT INTO records(kind, id, owner, effective_utc, json, sha256, created_utc, created_by)
                      VALUES ($k, $id, $o, $e, $j, $s, $now, $by)", tx,
                ("$k", kind), ("$id", id), ("$o", owner), ("$e", effectiveUtc?.ToString("o", CultureInfo.InvariantCulture)),
                ("$j", JsonSerializer.Serialize(value, Json)), ("$s", sha256), ("$now", Now()), ("$by", by));
            tx.Commit();
        }
    }

    public bool Delete(string kind, string id, string by)
    {
        lock (_write)
        {
            using var c = Open();
            return Exec(c, "UPDATE records SET deleted_utc = $now, deleted_by = $by WHERE kind = $k AND id = $id AND deleted_utc IS NULL",
                null, ("$k", kind), ("$id", id), ("$now", Now()), ("$by", by)) > 0;
        }
    }

    public StoredRecord<T>? Get<T>(string kind, string id)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT id, owner, json, created_utc, created_by FROM records WHERE kind = $k AND id = $id AND deleted_utc IS NULL";
        q.Parameters.AddWithValue("$k", kind);
        q.Parameters.AddWithValue("$id", id);
        using var r = q.ExecuteReader();
        return r.Read() ? Row<T>(r) : null;
    }

    /// <summary>Live records of a kind visible to <paramref name="user"/> (shared plus their own).</summary>
    public List<StoredRecord<T>> List<T>(string kind, string user)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT id, owner, json, created_utc, created_by FROM records WHERE kind = $k AND deleted_utc IS NULL AND (owner = $sys OR lower(owner) = lower($u)) ORDER BY effective_utc, created_utc";
        q.Parameters.AddWithValue("$k", kind);
        q.Parameters.AddWithValue("$sys", SystemOwner);
        q.Parameters.AddWithValue("$u", user);
        using var r = q.ExecuteReader();
        var list = new List<StoredRecord<T>>();
        while (r.Read()) list.Add(Row<T>(r));
        return list;
    }

    private static StoredRecord<T> Row<T>(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), JsonSerializer.Deserialize<T>(r.GetString(2), Json)!,
        DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetString(4));

    // ------------------------------------------------------------------ classifications

    public void AddClassification(string owner, string receiptId, RewardCategory category, ClassificationStatus status, string? note, string by)
    {
        lock (_write)
        {
            using var c = Open();
            Exec(c, @"INSERT INTO classifications(owner, receipt_id, seq, category, status, note, by_user, created_utc)
                      VALUES ($o, $r, COALESCE((SELECT MAX(seq) FROM classifications WHERE owner = $o AND receipt_id = $r), 0) + 1, $c, $s, $n, $by, $now)",
                null, ("$o", owner), ("$r", receiptId), ("$c", category.ToString()), ("$s", status.ToString()), ("$n", note), ("$by", by), ("$now", Now()));
        }
    }

    /// <summary>Latest classification per receipt for the owner.</summary>
    public Dictionary<string, (RewardCategory Category, ClassificationStatus Status, string? Note)> Classifications(string owner)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = @"SELECT receipt_id, category, status, note FROM classifications c
                          WHERE owner = $o AND seq = (SELECT MAX(seq) FROM classifications WHERE owner = c.owner AND receipt_id = c.receipt_id)";
        q.Parameters.AddWithValue("$o", owner);
        using var r = q.ExecuteReader();
        var d = new Dictionary<string, (RewardCategory, ClassificationStatus, string?)>(StringComparer.Ordinal);
        while (r.Read())
            d[r.GetString(0)] = (Enum.Parse<RewardCategory>(r.GetString(1)), Enum.Parse<ClassificationStatus>(r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3));
        return d;
    }

    // ------------------------------------------------------------------ reviews (append-only)

    public void AppendReview(string owner, ValuationReview review)
    {
        lock (_write)
        {
            using var c = Open();
            Exec(c, "INSERT INTO reviews(owner, review_id, version, status, json, created_utc) VALUES ($o, $id, $v, $s, $j, $now)", null,
                ("$o", owner), ("$id", review.ReviewId), ("$v", review.Version), ("$s", review.Status.ToString()),
                ("$j", JsonSerializer.Serialize(review, Json)), ("$now", Now()));
        }
    }

    public List<ValuationReview> Reviews(string owner)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM reviews WHERE owner = $o ORDER BY created_utc, review_id, version";
        q.Parameters.AddWithValue("$o", owner);
        using var r = q.ExecuteReader();
        var list = new List<ValuationReview>();
        while (r.Read()) list.Add(JsonSerializer.Deserialize<ValuationReview>(r.GetString(0), Json)!);
        return list;
    }

    // ------------------------------------------------------------------ report versions (append-only)

    public sealed record ReportVersionInfo(string Id, string Wallet, int Year, string ReviewFingerprint, string Status, DateTime CreatedUtc);

    public string AddReportVersion(string owner, string wallet, int year, string reviewFingerprint, string status, object report)
    {
        var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        lock (_write)
        {
            using var c = Open();
            Exec(c, "INSERT INTO report_versions(owner, id, wallet, year, review_fingerprint, status, json, created_utc) VALUES ($o, $id, $w, $y, $f, $s, $j, $now)", null,
                ("$o", owner), ("$id", id), ("$w", wallet), ("$y", year), ("$f", reviewFingerprint), ("$s", status), ("$j", JsonSerializer.Serialize(report, Json)), ("$now", Now()));
        }
        return id;
    }

    public List<ReportVersionInfo> ReportVersions(string owner, string? wallet = null)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT id, wallet, year, review_fingerprint, status, created_utc FROM report_versions WHERE owner = $o AND ($w IS NULL OR wallet = $w) ORDER BY created_utc DESC";
        q.Parameters.AddWithValue("$o", owner);
        q.Parameters.AddWithValue("$w", (object?)wallet ?? DBNull.Value);
        using var r = q.ExecuteReader();
        var list = new List<ReportVersionInfo>();
        while (r.Read())
            list.Add(new(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        return list;
    }

    public string? ReportVersionJson(string owner, string id)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM report_versions WHERE owner = $o AND id = $id";
        q.Parameters.AddWithValue("$o", owner);
        q.Parameters.AddWithValue("$id", id);
        return q.ExecuteScalar() as string;
    }

    // ------------------------------------------------------------------ evidence archive

    /// <summary>Stores an archived copy of an evidence file by content hash. The hash proves integrity, not truth.</summary>
    public string ArchiveEvidence(string name, byte[] content, string? sourceUrl, string by)
    {
        var sha = ReceiptIdentity.Sha256Hex(content);
        Directory.CreateDirectory(EvidenceDir);
        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_').ToArray());
        var path = Path.Combine(EvidenceDir, sha[..16] + "-" + (safe.Length > 80 ? safe[^80..] : safe));
        lock (_write)
        {
            if (!File.Exists(path)) File.WriteAllBytes(path, content);
            using var c = Open();
            Exec(c, "INSERT OR IGNORE INTO evidence_files(sha256, name, bytes, stored_path, source_url, collected_utc, collected_by) VALUES ($s, $n, $b, $p, $u, $now, $by)", null,
                ("$s", sha), ("$n", name), ("$b", content.LongLength), ("$p", path), ("$u", sourceUrl), ("$now", Now()), ("$by", by));
        }
        return sha;
    }

    public List<(string Sha256, string Name, long Bytes, string? SourceUrl, DateTime CollectedUtc)> EvidenceFiles()
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT sha256, name, bytes, source_url, collected_utc FROM evidence_files ORDER BY collected_utc";
        using var r = q.ExecuteReader();
        var list = new List<(string, string, long, string?, DateTime)>();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), DateTime.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        return list;
    }

    // ------------------------------------------------------------------ helpers

    private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static int Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }
}
