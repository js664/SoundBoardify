using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace VRSoundboard;

public sealed class Storage
{
    public string Root { get; }
    public string SoundsPath => Path.Combine(Root, "sounds");
    public string CachePath => Path.Combine(Root, "cache");
    public string ImagesPath => Path.Combine(Root, "images");
    public string LogsPath => Path.Combine(Root, "logs");
    private string DbPath => Path.Combine(Root, "database", "library.db");
    public Storage(string? localAppDataPath = null)
    {
        var localAppData = localAppDataPath ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(localAppData, "SimplySound");
        // Keep existing libraries intact across the Soundboardify → SimplySound rebrand.
        foreach (var legacy in new[] { Path.Combine(localAppData, "Soundboardify"), Path.Combine(localAppData, "VRSoundboard") })
        {
            if (!Directory.Exists(legacy)) continue;
            try { MigrateLegacyDataRoot(current, legacy); }
            catch (IOException) { if (!Directory.Exists(current)) current = legacy; }
            catch (UnauthorizedAccessException) { if (!Directory.Exists(current)) current = legacy; }
        }
        Root = current;
    }
    private static void MigrateLegacyDataRoot(string current, string legacy)
    {
        if (!Directory.Exists(current)) { Directory.Move(legacy, current); return; }
        var movedLegacy = new List<(string Source, string Destination)>();
        var backedUp = new List<(string Original, string Backup)>();
        try
        {
            foreach (var name in new[] { "database", "sounds", "cache", "images", "logs" })
            {
                var source = Path.Combine(legacy, name);
                if (!Directory.Exists(source)) continue;
                var destination = Path.Combine(current, name);
                if (Directory.Exists(destination) && !(name == "database" ? IsDatabaseUninitialized(destination) : IsDirectoryEmpty(destination))) continue;
                if (Directory.Exists(destination))
                {
                    var backup = destination + ".empty-before-migration";
                    if (Directory.Exists(backup)) backup += "-" + Guid.NewGuid().ToString("N");
                    Directory.Move(destination, backup);
                    backedUp.Add((destination, backup));
                }
                Directory.Move(source, destination);
                movedLegacy.Add((source, destination));
            }
            if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
        }
        catch
        {
            foreach (var move in movedLegacy.AsEnumerable().Reverse())
                if (Directory.Exists(move.Destination) && !Directory.Exists(move.Source)) Directory.Move(move.Destination, move.Source);
            foreach (var backup in backedUp.AsEnumerable().Reverse())
                if (Directory.Exists(backup.Backup) && !Directory.Exists(backup.Original)) Directory.Move(backup.Backup, backup.Original);
            throw;
        }
    }
    private static bool IsDirectoryEmpty(string path) => !Directory.EnumerateFileSystemEntries(path).Any();
    private static bool IsDatabaseUninitialized(string directory)
    {
        var path = Path.Combine(directory, "library.db");
        if (!File.Exists(path)) return IsDirectoryEmpty(directory);
        try
        {
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('sounds','settings')";
            var hasSounds = false;
            var hasSettings = false;
            using (var tables = cmd.ExecuteReader())
                while (tables.Read()) { if (tables.GetString(0) == "sounds") hasSounds = true; if (tables.GetString(0) == "settings") hasSettings = true; }
            if (!hasSounds || !hasSettings) return false;
            cmd.CommandText = "SELECT COUNT(*) FROM sounds";
            if (Convert.ToInt64(cmd.ExecuteScalar()) != 0) return false;
            cmd.CommandText = "SELECT json FROM settings WHERE key='app'";
            var settingsJson = cmd.ExecuteScalar() as string;
            if (settingsJson is null) return true;
            return HasDefaultSettings(JsonSerializer.Deserialize<AppSettings>(settingsJson) ?? new());
        }
        catch (SqliteException) { return false; }
        catch (JsonException) { return false; }
    }
    private static bool HasDefaultSettings(AppSettings settings)
    {
        var defaults = new AppSettings();
        return settings.WebEnabled == defaults.WebEnabled && settings.Port == defaults.Port && settings.LanAccess == defaults.LanAccess &&
            !settings.TailscaleAccess && settings.EndpointId is null && settings.MonitorEndpointId is null && settings.ButtonDensity == defaults.ButtonDensity &&
            settings.ReconnectAudio == defaults.ReconnectAudio && settings.MasterVolume == defaults.MasterVolume && settings.MicOutputGain == defaults.MicOutputGain &&
            !settings.UseVirtualMicHeadroom && settings.OutputHeadroomPreferenceVersion <= 1 && settings.MonitorLocally && settings.LocalMonitorPreferenceVersion <= 1 && !settings.StartWithWindows && !settings.StartMinimized &&
            !settings.MinimizeToTray && settings.Theme == defaults.Theme && settings.PairingToken is null && settings.MaxUploadBytes == defaults.MaxUploadBytes;
    }
    private SqliteConnection Connect()
    {
        var db = new SqliteConnection($"Data Source={DbPath}");
        db.Open();
        return db;
    }
    public void Initialize()
    {
        foreach (var path in new[] { SoundsPath, CachePath, ImagesPath, LogsPath, Path.GetDirectoryName(DbPath)! }) Directory.CreateDirectory(path);
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS sounds(id TEXT PRIMARY KEY, sort_order INTEGER NOT NULL, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, json TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    public List<Sound> LoadSounds()
    {
        using var db = Connect(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM sounds ORDER BY sort_order";
        using var rows = cmd.ExecuteReader();
        var list = new List<Sound>();
        while (rows.Read()) { var s = JsonSerializer.Deserialize<Sound>(rows.GetString(0)); if (s != null) list.Add(s); }
        return list;
    }
    public void Save(Sound sound)
    {
        using var db = Connect(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sounds(id,sort_order,json) VALUES($id,$order,$json) ON CONFLICT(id) DO UPDATE SET sort_order=$order,json=$json";
        cmd.Parameters.AddWithValue("$id", sound.Id.ToString()); cmd.Parameters.AddWithValue("$order", sound.SortOrder); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(sound)); cmd.ExecuteNonQuery();
    }
    public void SaveSoundsOrder(IReadOnlyList<Sound> sounds)
    {
        using var db = Connect();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO sounds(id,sort_order,json) VALUES($id,$order,$json) ON CONFLICT(id) DO UPDATE SET sort_order=$order,json=$json";
        var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        var orderParameter = command.Parameters.Add("$order", SqliteType.Integer);
        var jsonParameter = command.Parameters.Add("$json", SqliteType.Text);
        foreach (var sound in sounds)
        {
            idParameter.Value = sound.Id.ToString();
            orderParameter.Value = sound.SortOrder;
            jsonParameter.Value = JsonSerializer.Serialize(sound);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void Delete(Guid id)
    {
        using var db = Connect(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM sounds WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.ExecuteNonQuery();
    }
    public void DeleteSoundsAndSaveOrder(IReadOnlyCollection<Guid> ids, IReadOnlyList<Sound> remaining)
    {
        using var db = Connect();
        using var transaction = db.BeginTransaction();
        using (var delete = db.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM sounds WHERE id=$id";
            var idParameter = delete.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in ids)
            {
                idParameter.Value = id.ToString();
                delete.ExecuteNonQuery();
            }
        }
        using (var save = db.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = "INSERT INTO sounds(id,sort_order,json) VALUES($id,$order,$json) ON CONFLICT(id) DO UPDATE SET sort_order=$order,json=$json";
            var idParameter = save.Parameters.Add("$id", SqliteType.Text);
            var orderParameter = save.Parameters.Add("$order", SqliteType.Integer);
            var jsonParameter = save.Parameters.Add("$json", SqliteType.Text);
            foreach (var sound in remaining)
            {
                idParameter.Value = sound.Id.ToString();
                orderParameter.Value = sound.SortOrder;
                jsonParameter.Value = JsonSerializer.Serialize(sound);
                save.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }
    public AppSettings LoadSettings()
    {
        using var db = Connect(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM settings WHERE key='app'";
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<AppSettings>(json) ?? new() : new();
    }
    public void Save(AppSettings settings)
    {
        using var db = Connect(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO settings(key,json) VALUES('app',$json) ON CONFLICT(key) DO UPDATE SET json=$json";
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings)); cmd.ExecuteNonQuery();
    }
}
