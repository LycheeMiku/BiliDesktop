using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BiliDesktop;

public record BackupRule(long Id, long AccountId, long FolderId, string FolderTitle, string Directory, int IntervalMinutes, bool Enabled, long NextRun, string Status);
public enum KeywordMatchMode { Any, All }
public record KeywordRule(long Id, string Keyword, long SinceUnix, string Directory, int IntervalMinutes, bool Enabled, long NextRun, string Status, IReadOnlyList<string>? Terms = null, KeywordMatchMode MatchMode = KeywordMatchMode.Any, int IntervalSeconds = 0, int QueryDelayMs = 250)
{
    public string Since => DateTimeOffset.FromUnixTimeSeconds(SinceUnix).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public IReadOnlyList<string> Keywords => Terms ?? [Keyword];
    public string KeywordsDisplay => string.Join(" / ", Keywords);
    public string ModeDisplay => MatchMode == KeywordMatchMode.All ? "必须全部" : "满足任意";
    public int EffectiveIntervalSeconds => IntervalSeconds > 0 ? IntervalSeconds : checked(IntervalMinutes * 60);
    public string IntervalDisplay { get { var value = TimeSpan.FromSeconds(EffectiveIntervalSeconds); return $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}"; } }
    public string RateDisplay => QueryDelayMs > 250 ? "更低" : "软件默认";
}

public sealed class BackupStore
{
    private readonly string connectionString;
    static BackupStore() => SQLitePCL.Batteries_V2.Init();
    public BackupStore(string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDesktop", "backups.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS backup_rules (
                id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL, folder_id INTEGER NOT NULL,
                folder_title TEXT NOT NULL, directory TEXT NOT NULL, interval_minutes INTEGER NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1, next_run INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT '等待检查', UNIQUE(account_id, folder_id, directory));
            CREATE TABLE IF NOT EXISTS backup_completed (
                rule_id INTEGER NOT NULL REFERENCES backup_rules(id) ON DELETE CASCADE,
                bvid TEXT NOT NULL, cid INTEGER NOT NULL, path TEXT NOT NULL,
                PRIMARY KEY(rule_id,bvid,cid));
            CREATE TABLE IF NOT EXISTS keyword_rules (
                id INTEGER PRIMARY KEY, keyword TEXT NOT NULL, since_unix INTEGER NOT NULL, directory TEXT NOT NULL,
                interval_minutes INTEGER NOT NULL, enabled INTEGER NOT NULL DEFAULT 1, next_run INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT '等待检查', UNIQUE(keyword,since_unix,directory));
            CREATE TABLE IF NOT EXISTS keyword_completed (
                rule_id INTEGER NOT NULL REFERENCES keyword_rules(id) ON DELETE CASCADE,
                bvid TEXT NOT NULL, cid INTEGER NOT NULL, path TEXT NOT NULL, PRIMARY KEY(rule_id,bvid,cid));
            """;
        cmd.ExecuteNonQuery();
        using var migration = db.BeginTransaction();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = db.CreateCommand())
        {
            info.Transaction = migration; info.CommandText = "PRAGMA table_info(keyword_rules)";
            using var reader = info.ExecuteReader(); while (reader.Read()) columns.Add(reader.GetString(1));
        }
        foreach (var (name, definition) in new[] { ("interval_seconds", "INTEGER NOT NULL DEFAULT 0"), ("keywords_json", "TEXT"), ("match_mode", "TEXT NOT NULL DEFAULT 'any'"), ("query_delay_ms", "INTEGER NOT NULL DEFAULT 250") })
        {
            if (columns.Contains(name)) continue;
            using var alter = db.CreateCommand(); alter.Transaction = migration; alter.CommandText = $"ALTER TABLE keyword_rules ADD COLUMN {name} {definition}"; alter.ExecuteNonQuery();
        }
        migration.Commit();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }

    public IReadOnlyList<BackupRule> List()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,account_id,folder_id,folder_title,directory,interval_minutes,enabled,next_run,status FROM backup_rules ORDER BY id";
        using var reader = cmd.ExecuteReader(); var result = new List<BackupRule>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt64(6) != 0, reader.GetInt64(7), reader.GetString(8)));
        return result;
    }
    public void Add(long account, FavoriteFolder folder, string directory, int minutes)
    {
        if (account <= 0 || folder.Id <= 0 || minutes is < 5 or > 10080 || !Directory.Exists(directory)) throw new ArgumentException("账号、目录或检查间隔无效。");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO backup_rules(account_id,folder_id,folder_title,directory,interval_minutes) VALUES($account,$folder,$title,$dir,$interval)";
        cmd.Parameters.AddWithValue("$account", account); cmd.Parameters.AddWithValue("$folder", folder.Id); cmd.Parameters.AddWithValue("$title", folder.Title); cmd.Parameters.AddWithValue("$dir", path); cmd.Parameters.AddWithValue("$interval", minutes); cmd.ExecuteNonQuery();
    }
    public int AddMany(long account, IReadOnlyList<FavoriteFolder> folders, string directory, int minutes)
    {
        if (account <= 0 || folders.Count == 0 || folders.Any(f => f.Id <= 0 || f.Title is null) || minutes is < 5 or > 10080 || !Directory.Exists(directory)) throw new ArgumentException("账号、目录或检查间隔无效。");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        using var db = Open(); using var transaction = db.BeginTransaction(); using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO backup_rules(account_id,folder_id,folder_title,directory,interval_minutes) VALUES($account,$folder,$title,$dir,$interval) ON CONFLICT(account_id,folder_id,directory) DO NOTHING";
        cmd.Parameters.AddWithValue("$account", account); var id = cmd.Parameters.Add("$folder", SqliteType.Integer); var title = cmd.Parameters.Add("$title", SqliteType.Text);
        cmd.Parameters.AddWithValue("$dir", path); cmd.Parameters.AddWithValue("$interval", minutes);
        var added = 0;
        foreach (var folder in folders.DistinctBy(f => f.Id)) { id.Value = folder.Id; title.Value = folder.Title; added += cmd.ExecuteNonQuery(); }
        transaction.Commit();
        return added;
    }
    public void SetEnabled(long id, bool enabled)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE backup_rules SET enabled=$enabled,next_run=CASE WHEN $enabled=1 THEN 0 ELSE next_run END,status=CASE WHEN $enabled=1 THEN '等待检查' ELSE '已暂停' END WHERE id=$id";
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public void Remove(long id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM backup_rules WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public void RunNow(long id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE backup_rules SET next_run=0 WHERE id=$id AND enabled=1"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public bool Enabled(long id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT enabled FROM backup_rules WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 1;
    }
    public void SetStatus(long id, string status, long? nextRun = null)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE backup_rules SET status=$status, next_run=COALESCE($next,next_run) WHERE id=$id AND enabled=1";
        cmd.Parameters.AddWithValue("$status", status); cmd.Parameters.AddWithValue("$next", (object?)nextRun ?? DBNull.Value); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public bool IsCompleted(long id, string bvid, long cid)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT path FROM backup_completed WHERE rule_id=$id AND bvid=$bvid AND cid=$cid";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$bvid", bvid); cmd.Parameters.AddWithValue("$cid", cid);
        return cmd.ExecuteScalar() is string saved && File.Exists(saved);
    }
    public void MarkCompleted(long id, string bvid, long cid, string path)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO backup_completed(rule_id,bvid,cid,path) SELECT $id,$bvid,$cid,$path WHERE EXISTS(SELECT 1 FROM backup_rules WHERE id=$id) ON CONFLICT(rule_id,bvid,cid) DO UPDATE SET path=excluded.path";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$bvid", bvid); cmd.Parameters.AddWithValue("$cid", cid); cmd.Parameters.AddWithValue("$path", path); cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<KeywordRule> ListKeywords()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,keyword,since_unix,directory,interval_minutes,enabled,next_run,status,interval_seconds,keywords_json,match_mode,query_delay_ms FROM keyword_rules ORDER BY id";
        using var reader = cmd.ExecuteReader(); var rules = new List<KeywordRule>();
        while (reader.Read())
        {
            var keyword = reader.GetString(1);
            var terms = reader.IsDBNull(9) ? new[] { keyword } : JsonSerializer.Deserialize<string[]>(reader.GetString(9)) ?? throw new InvalidDataException("关键词规则数据无效。");
            if (terms.Length is < 1 or > 20 || terms.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200 || x.Any(char.IsControl))) throw new InvalidDataException("关键词规则数据无效。");
            var mode = reader.GetString(10) switch { "any" => KeywordMatchMode.Any, "all" => KeywordMatchMode.All, _ => throw new InvalidDataException("关键词规则匹配方式无效。") };
            var seconds = reader.GetInt32(8); var delay = reader.GetInt32(11);
            if (seconds is < 0 or > 604800 || seconds is > 0 and < 30 || reader.GetInt32(4) is < 1 or > 10080 || delay is not (250 or 1500)) throw new InvalidDataException("关键词规则间隔无效。");
            rules.Add(new(reader.GetInt64(0), keyword, reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt64(5) != 0, reader.GetInt64(6), reader.GetString(7), terms, mode, seconds, delay));
        }
        return rules;
    }
    public void AddKeyword(string keyword, long sinceUnix, string directory, int minutes)
    {
        if (minutes is < 5 or > 10080) throw new ArgumentException("关键词、起始时间、目录或检查间隔无效。");
        AddKeyword([keyword], sinceUnix, directory, checked(minutes * 60), KeywordMatchMode.Any, 250);
    }
    public void AddKeyword(IReadOnlyList<string> keywords, long sinceUnix, string directory, int seconds, KeywordMatchMode mode, int queryDelayMs)
    {
        if (keywords is null || keywords.Count is < 1 or > 20 || sinceUnix <= 0 || seconds is < 30 or > 604800 || !Enum.IsDefined(mode) || queryDelayMs is not (250 or 1500) || !Directory.Exists(directory)) throw new ArgumentException("关键词、起始时间、目录或检查间隔无效。");
        var terms = keywords.Select(x => x?.Trim() ?? "").ToArray();
        if (terms.Any(x => x.Length is < 1 or > 200 || x.Any(char.IsControl)) || terms.Distinct(StringComparer.OrdinalIgnoreCase).Count() != terms.Length) throw new ArgumentException("请输入不重复的关键词，每项最多 200 个字符。");
        var json = JsonSerializer.Serialize(terms);
        var key = terms.Length == 1 ? terms[0] : "组合:" + mode + ":" + JsonSerializer.Serialize(terms.Select(x => x.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO keyword_rules(keyword,since_unix,directory,interval_minutes,interval_seconds,keywords_json,match_mode,query_delay_ms) VALUES($keyword,$since,$dir,$minutes,$seconds,$terms,$mode,$delay)";
        cmd.Parameters.AddWithValue("$keyword", key); cmd.Parameters.AddWithValue("$since", sinceUnix); cmd.Parameters.AddWithValue("$dir", Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))); cmd.Parameters.AddWithValue("$minutes", Math.Max(1, (seconds + 59) / 60)); cmd.Parameters.AddWithValue("$seconds", seconds); cmd.Parameters.AddWithValue("$terms", json); cmd.Parameters.AddWithValue("$mode", mode == KeywordMatchMode.All ? "all" : "any"); cmd.Parameters.AddWithValue("$delay", queryDelayMs); cmd.ExecuteNonQuery();
    }
    public void SetKeywordEnabled(long id, bool enabled)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE keyword_rules SET enabled=$enabled,next_run=CASE WHEN $enabled=1 THEN 0 ELSE next_run END,status=CASE WHEN $enabled=1 THEN '等待检查' ELSE '已暂停' END WHERE id=$id";
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public void RemoveKeyword(long id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM keyword_rules WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public void RunKeywordNow(long id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE keyword_rules SET next_run=0 WHERE id=$id AND enabled=1"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public bool KeywordEnabled(long id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT enabled FROM keyword_rules WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 1;
    }
    public void SetKeywordStatus(long id, string status, long? nextRun = null)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE keyword_rules SET status=$status,next_run=COALESCE($next,next_run) WHERE id=$id AND enabled=1";
        cmd.Parameters.AddWithValue("$status", status); cmd.Parameters.AddWithValue("$next", (object?)nextRun ?? DBNull.Value); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public bool KeywordCompleted(long id, string bvid, long cid)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT path FROM keyword_completed WHERE rule_id=$id AND bvid=$bvid AND cid=$cid";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$bvid", bvid); cmd.Parameters.AddWithValue("$cid", cid); return cmd.ExecuteScalar() is string path && File.Exists(path);
    }
    public void MarkKeywordCompleted(long id, string bvid, long cid, string path)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO keyword_completed(rule_id,bvid,cid,path) SELECT $id,$bvid,$cid,$path WHERE EXISTS(SELECT 1 FROM keyword_rules WHERE id=$id) ON CONFLICT(rule_id,bvid,cid) DO UPDATE SET path=excluded.path";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$bvid", bvid); cmd.Parameters.AddWithValue("$cid", cid); cmd.Parameters.AddWithValue("$path", path); cmd.ExecuteNonQuery();
    }
}
