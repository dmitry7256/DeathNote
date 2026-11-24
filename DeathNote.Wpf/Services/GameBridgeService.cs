using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DeathNote.Wpf.Services;

/// <summary>
/// Mirrors player moderation data into Civ V's own ModUserData database.
/// Never creates or modifies the game database if the Lua bridge has not
/// initialized it, and never changes keys outside the Death Note namespace.
/// Do not run the legacy Python application simultaneously with this bridge.
/// </summary>
public static class GameBridgeService
{
    public sealed record PlayerState(string SteamId, string Nickname, string Note, string Marker,
        bool Favorite, bool Banned, string BanUntil, long Wins, long Losses, long Quits);

    private const string DatabaseName = "DeathNoteLobbyLocal";

    public static Task<string> SynchronizeAsync(string luaLog, IReadOnlyList<PlayerState> players,
        bool enabled, CancellationToken ct) => Task.Run(() => Synchronize(luaLog, players, enabled, ct), ct);

    private static string Synchronize(string luaLog, IReadOnlyList<PlayerState> players,
        bool enabled, CancellationToken ct)
    {
        string directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(luaLog)!, "..", "ModUserData"));
        string[] paths = [Path.Combine(directory, DatabaseName + "-1.db"),
                          Path.Combine(directory, DatabaseName + ".db")];
        var existingPaths = paths.Where(File.Exists).ToArray();
        if (existingPaths.Length == 0) return "Waiting for Civ V lobby bridge";

        Dictionary<string, string> values = new(StringComparer.Ordinal)
        { ["dn.autokick.enabled"] = enabled ? "1" : "0" };
        foreach (var p in players)
        {
            ct.ThrowIfCancellationRequested();
            if (p.SteamId.Length != 17 || !p.SteamId.All(char.IsAsciiDigit)) continue;
            var id = p.SteamId;
            var marker = p.Marker is "good" or "warn" or "bad" ? p.Marker : "";
            string text = "[COLOR_POSITIVE_TEXT]DEATH NOTE[ENDCOLOR][NEWLINE]" + SafeText(p.Nickname, 90) +
                "[NEWLINE]Wins: " + p.Wins + "  Losses: " + p.Losses + "  Quits: " + p.Quits;
            if (marker.Length != 0) text += "[NEWLINE]Rating: " + (marker switch
            {
                "good" => "Good", "warn" => "Questionable", "bad" => "Bad", _ => "Unrated"
            });
            if (p.Favorite) text += "[NEWLINE]Favorite: Yes";
            if (!string.IsNullOrWhiteSpace(p.Note)) text += "[NEWLINE]Note: " + SafeText(p.Note, 500);
            values["dn.player." + id] = text;
            bool active = p.Banned;
            string kickValue = "1";
            if (DateTimeOffset.TryParse(p.BanUntil, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var expiry))
            {
                active &= expiry > DateTimeOffset.UtcNow;
                kickValue = expiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            }
            if (active)
            {
                values["dn.kick." + id] = kickValue;
                text += "[NEWLINE]Banned: Yes";
                values["dn.player." + id] = text;
            }
        }
        string checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", values.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)))));
        values["dn.snapshot"] = checksum;

        int updated = 0;
        foreach (var file in existingPaths)
        {
            ct.ThrowIfCancellationRequested();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1
            }.ToString());
            try
            {
                connection.Open();
                using var tx = connection.BeginTransaction();
                using var ready = connection.CreateCommand();
                ready.Transaction = tx;
                ready.CommandText = "SELECT Value FROM SimpleValues WHERE Name='dn.bridge.v1'";
                if (Convert.ToString(ready.ExecuteScalar(), CultureInfo.InvariantCulture) != "1")
                {
                    tx.Rollback(); continue;
                }
                // When the snapshot is identical, avoid reading and comparing
                // thousands of SimpleValues rows on every 8-second poll.
                using (var fingerprint = connection.CreateCommand())
                {
                    fingerprint.Transaction = tx;
                    fingerprint.CommandText = "SELECT Value FROM SimpleValues WHERE Name='dn.snapshot'";
                    if (string.Equals(Convert.ToString(fingerprint.ExecuteScalar(),CultureInfo.InvariantCulture),
                        checksum,StringComparison.Ordinal))
                    {
                        tx.Rollback(); updated++; continue;
                    }
                }
                var old = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT Name, Value FROM SimpleValues WHERE Name LIKE 'dn.player.%' OR Name LIKE 'dn.marker.%' OR Name LIKE 'dn.badges.%' OR Name LIKE 'dn.kick.%' OR Name IN ('dn.autokick.enabled', 'dn.snapshot')";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) old[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
                }
                using var delete = connection.CreateCommand();
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM SimpleValues WHERE Name=$name";
                delete.Parameters.Add("$name", SqliteType.Text);
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO SimpleValues (Name,Value) VALUES ($name,$value)";
                insert.Parameters.Add("$name", SqliteType.Text);
                insert.Parameters.Add("$value", SqliteType.Text);
                foreach (var key in old.Keys.Except(values.Keys, StringComparer.Ordinal))
                {
                    delete.Parameters["$name"].Value = key; delete.ExecuteNonQuery();
                }
                foreach (var (key, value) in values)
                {
                    if (old.TryGetValue(key, out var before) && before == value) continue;
                    if (old.ContainsKey(key))
                    {
                        delete.Parameters["$name"].Value = key; delete.ExecuteNonQuery();
                    }
                    insert.Parameters["$name"].Value = key;
                    insert.Parameters["$value"].Value = value;
                    insert.ExecuteNonQuery();
                }
                tx.Commit();
                updated++;
            }
            catch (SqliteException)
            {
                // Game can hold a transient lock or have an uninitialized DB.
                // Never replace or create its database; retry next timer tick.
            }
        }
        return updated > 0 ? $"Lobby bridge connected · {updated} database(s)" : "Waiting for Civ V lobby bridge";
    }

    private static string SafeText(string value, int maxLength)
    {
        var text = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (char c in value)
        {
            if (text.Length >= maxLength) break;
            if (char.IsControl(c) || char.IsSurrogate(c)) continue;
            text.Append(c switch { '[' => '(', ']' => ')', _ => c });
        }
        return text.ToString();
    }
}
