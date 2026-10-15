using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentMemory.McpHost.Coding;

/// <summary>One host's bounded receipt journal. SQLite owns atomic commit; the memory graph remains separate.</summary>
internal sealed class CodingWriteJournal : ICodingWriteJournal, IDisposable
{
    private const int MaximumEntryBytes = 128 * 1024;
    private readonly SqliteConnection _connection;
    private readonly FileStream _lease;
    private readonly int _retained;
    private readonly Func<CodingIdentity, bool> _authorized;

    public CodingWriteJournal(CodingJournalOptions options, string binding, Func<CodingIdentity, bool> authorized)
    {
        options.Validate();
        _retained = options.RetainedJobs;
        _authorized = authorized;
        CodingJournalDirectory.Prepare(options.Directory);
        var path = Path.Combine(options.Directory, "receipts.db");
        if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024 * 1024)
            throw new IOException("Receipt journal exceeds its storage bound.");
        var existing = File.Exists(path) && new FileInfo(path).Length > 0;
        _lease = new FileStream(Path.Combine(options.Directory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 1,
        }.ToString());
        try
        {
            _connection.Open();
            Execute("PRAGMA page_size=4096; PRAGMA journal_mode=DELETE; PRAGMA synchronous=EXTRA; PRAGMA secure_delete=ON; PRAGMA temp_store=MEMORY; PRAGMA max_page_count=16384;");
            var version = Scalar("PRAGMA user_version;");
            if (version is not (0L or 1L) || (existing && version is 0L))
                throw new IOException("Unsupported receipt journal version.");
            if (!Equals(Scalar("PRAGMA quick_check;"), "ok")) throw new IOException("Receipt journal integrity check failed.");
            using var transaction = _connection.BeginTransaction();
            Execute("CREATE TABLE IF NOT EXISTS host (id INTEGER PRIMARY KEY CHECK(id=1), binding TEXT NOT NULL); " +
                "CREATE TABLE IF NOT EXISTS receipts (sequence INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL UNIQUE, payload BLOB NOT NULL);", transaction);
            using var insert = Command("INSERT OR IGNORE INTO host(id,binding) VALUES(1,$binding);", transaction);
            insert.Parameters.AddWithValue("$binding", binding);
            insert.ExecuteNonQuery();
            if (!Equals(Scalar("SELECT binding FROM host WHERE id=1;", transaction), binding))
                throw new IOException("Journal belongs to a different owner or graph deployment.");
            Execute("PRAGMA user_version=1;", transaction);
            transaction.Commit();
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { _connection.Dispose(); _lease.Dispose(); throw; }
    }

    public bool CanResume(CodingIdentity identity) => identity.CanWrite && _authorized(identity);

    public IReadOnlyList<CodingJournalEntry> Load()
    {
        var count = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM receipts;"), System.Globalization.CultureInfo.InvariantCulture);
        if (count > _retained) throw new IOException("Receipt journal exceeds configured retention; preserve it and restore the prior limit.");
        if (Convert.ToInt64(Scalar("SELECT COALESCE(MAX(length(payload)),0) FROM receipts;"), System.Globalization.CultureInfo.InvariantCulture) > MaximumEntryBytes)
            throw new IOException("Receipt journal contains an oversized entry.");
        using var command = Command("SELECT job_id,payload FROM receipts ORDER BY sequence;");
        using var reader = command.ExecuteReader();
        var entries = new List<CodingJournalEntry>();
        while (reader.Read())
        {
            var entry = JsonSerializer.Deserialize<CodingJournalEntry>((byte[])reader[1], CodingApi.Json)
                ?? throw new IOException("Receipt journal contains an invalid entry.");
            Validate(entry);
            if (entry.Job.JobId != reader.GetString(0)) throw new IOException("Receipt journal identity mismatch.");
            entries.Add(entry);
        }
        return entries;
    }

    public void Save(CodingJournalEntry entry)
    {
        Validate(entry);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, CodingApi.Json);
        if (bytes.Length > MaximumEntryBytes) throw new IOException("Receipt journal entry exceeds its bound.");
        using var transaction = _connection.BeginTransaction();
        using var command = Command("INSERT INTO receipts(job_id,payload) VALUES($id,$payload) ON CONFLICT(job_id) DO UPDATE SET payload=excluded.payload;", transaction);
        command.Parameters.AddWithValue("$id", entry.Job.JobId);
        command.Parameters.AddWithValue("$payload", bytes);
        command.ExecuteNonQuery();
        if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM receipts;", transaction), System.Globalization.CultureInfo.InvariantCulture) > _retained)
            throw new IOException("Receipt journal retention is full.");
        transaction.Commit();
    }

    public void Remove(string jobId)
    {
        using var command = Command("DELETE FROM receipts WHERE job_id=$id;");
        command.Parameters.AddWithValue("$id", jobId);
        command.ExecuteNonQuery();
    }

    private static void Validate(CodingJournalEntry entry)
    {
        if (entry.Identity is null || entry.Job is null || !Guid.TryParseExact(entry.Job.JobId, "N", out _) ||
            entry.Hash is not { Length: 64 } || !entry.Hash.All(Uri.IsHexDigit) ||
            entry.Key is not { Length: 71 } || !entry.Key.StartsWith("coding-", StringComparison.Ordinal) ||
            entry.Key.AsSpan(7).ContainsAnyExcept("0123456789abcdef".AsSpan()) ||
            !Enum.IsDefined(entry.Job.State) || !Enum.IsDefined(entry.Job.ExtractionPath) ||
            entry.Job.Durability is not "journal")
            throw new IOException("Invalid journal receipt.");
        CodingInput.RequireText(entry.Identity.OwnerId, 256, nameof(entry.Identity.OwnerId));
        CodingInput.RequireText(entry.Identity.ProjectId, 32, nameof(entry.Identity.ProjectId));
        if (entry.Job.State is CodingJobState.Queued or CodingJobState.Running)
        {
            if (entry.Request is null || entry.Hash != entry.Request.PayloadHash() || entry.Key !=
                CodingInput.MessageId(CodingInput.SessionId(entry.Identity, entry.Request.Session), entry.Request.TurnId, "user"))
                throw new IOException("Journal request does not match its receipt.");
            entry.Request.Validate();
        }
        else if (entry.Request is not null) throw new IOException("Terminal journal receipts must not retain conversation text.");
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }
    private object? Scalar(string sql, SqliteTransaction? transaction = null)
    {
        using var command = Command(sql, transaction);
        return command.ExecuteScalar();
    }
    private void Execute(string sql, SqliteTransaction? transaction = null)
    {
        using var command = Command(sql, transaction);
        command.ExecuteNonQuery();
    }
    public void Dispose() { _connection.Dispose(); _lease.Dispose(); }
}
