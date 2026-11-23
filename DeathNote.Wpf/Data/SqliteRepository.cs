using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeathNote.Wpf.Models;
using DeathNote.Wpf.Services;
using Microsoft.Data.Sqlite;

namespace DeathNote.Wpf.Data;

/// <summary>
/// Native reader and conservative writer for Python Death Note v2.09 schema 3.
/// Existing databases are never created or migrated implicitly. Every write is
/// optimistic (revisions) and audited, and does not discard unknown JSON fields.
/// </summary>
public sealed class SqliteRepository
{
    private static readonly string[] PlayerFields = ["nickname","note","color","marker","autokick","wins","losses","leaves","ban_until"];
    private static readonly string[] MatchFields = ["title","started","last_at","ended","clock","map","local_nick","local_civ","result","start_turn","last_turn","note","last_offset","start_tick","last_tick","winner_id","favorite","deleted","review_pending","menu_return_at","observed_end_at","manual_edit","game_start_offset","launch_log_tick"];
    private static readonly string[] PartFields = ["nickname","civ"];
    private static readonly HashSet<string> BooleanFields = ["autokick","favorite","deleted","review_pending","manual_edit"];
    private static readonly Dictionary<string,string> Results = new(StringComparer.Ordinal)
    {
        ["Успешно"]="Completed",["Игра сломалась"]="Game crashed",["Все игроки вышли"]="All players left",
        ["Рестарт"]="Restart",["Не указано"]="Unspecified"
    };
    private readonly string _file;
    public SqliteRepository(string path) { _file=path; }

    private SqliteConnection Open(bool write=false)
    {
        if (!File.Exists(_file)) throw new FileNotFoundException("DeathNote.db was not found. Select the old app data folder.",_file);
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource=_file,Mode=write?SqliteOpenMode.ReadWrite:SqliteOpenMode.ReadOnly,
            Pooling=true,DefaultTimeout=5,Cache=SqliteCacheMode.Private
        }.ToString());
        connection.Open();
        using var check=connection.CreateCommand();
        check.CommandText="PRAGMA user_version";
        if (Convert.ToInt32(check.ExecuteScalar(),CultureInfo.InvariantCulture)!=3)
            throw new InvalidDataException("Only Death Note SQLite schema v3 is supported. The original database was not changed.");
        if (write)
        {
            using var pragmas=connection.CreateCommand();
            pragmas.CommandText="PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000";
            pragmas.ExecuteNonQuery();
        }
        return connection;
    }

    public Task<bool> CheckIntegrityAsync(CancellationToken ct=default) => Task.Run(() =>
    {
        using var c=Open();
        var result=Scalar(c,null,"PRAGMA quick_check");
        if (!string.Equals(Convert.ToString(result,CultureInfo.InvariantCulture),"ok",StringComparison.Ordinal)) return false;
        using var cmd=Command(c,null,"PRAGMA foreign_key_check");
        using var r=cmd.ExecuteReader();
        return !r.Read();
    },ct);

    public Task<(long Players,long Games,long Preferences)> ReadRevisionsAsync(CancellationToken ct=default) => Task.Run(() =>
    {
        using var c=Open();
        return (Revision(c,"players"),Revision(c,"games"),Revision(c,"preferences"));
    },ct);

    public Task<DatabaseSnapshot> ReadSnapshotAsync(CancellationToken ct=default,long cachedPlayers=-1,long cachedGames=-1) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var c=Open();
        using var tx=c.BeginTransaction(deferred:true);
        long pr=Revision(c,"players",tx), gr=Revision(c,"games",tx), pref=Revision(c,"preferences",tx);
        var preferences=ReadPreferences(c,tx);
        var favorites=new HashSet<string>(StringComparer.Ordinal);
        if (preferences["favorites"] is JsonArray fav)
            foreach(var n in fav) if (n is not null) favorites.Add(n.ToString());
        var players=pr==cachedPlayers ? Array.Empty<PlayerRecord>() : ReadPlayers(c,tx,favorites,ct);
        var matches=gr==cachedGames ? Array.Empty<MatchRecord>() : ReadMatches(c,tx,ct);
        tx.Commit();
        return new DatabaseSnapshot(pr,gr,pref,players,matches,favorites,
            Str(preferences,"local_nick"),Bool(preferences,"review_notifications",true),
            Bool(preferences,"autokick_enabled",false));
    },ct);

    public Task<MatchRecord?> ReadMatchDetailsAsync(string id,CancellationToken ct=default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var c=Open();
        using var tx=c.BeginTransaction(deferred:true);
        var json=LoadFullMatch(c,tx,id);
        if (json is null) return null;
        var result=ModelMatch(json);
        // Python keeps the local host separately from match_players.
        result.Participants = 1;
        result.ParticipantNames = result.LocalParticipantLabel;
        result.WinnerName = WinnerName(result, []);
        if (json["players"] is JsonObject items)
        {
            var participants=items.Select(pair => new MatchParticipant(pair.Key,Str(pair.Value as JsonObject,"nickname"),Str(pair.Value as JsonObject,"civ"))).ToList();
            result.PlayerDetails=participants;
            result.Participants=participants.Count + 1;
            result.ParticipantNames=string.Join(", ",new[]{result.LocalParticipantLabel}.Concat(participants.Select(x=>x.Nickname)));
            result.WinnerName=WinnerName(result,participants);
        }
        tx.Commit();
        return result;
    },ct);

    /// <summary>
    /// The nickname dialog loads complete history on demand. Reading dates for
    /// every player during startup would waste work and keep the UI waiting.
    /// No changes are made to the original nickname tables.
    /// </summary>
    public Task<IReadOnlyList<NicknameHistoryEntry>> ReadNicknameHistoryAsync(
        string steamId, string currentNickname, CancellationToken ct=default) => Task.Run<IReadOnlyList<NicknameHistoryEntry>>(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var c=Open();
        using var tx=c.BeginTransaction(deferred:true);
        var entries=new List<NicknameHistoryEntry>();
        using (var cmd=Command(c,tx,"""
            SELECT n.nickname, d.stamp
              FROM nickname_history AS n
              LEFT JOIN nickname_dates AS d
                ON d.steamid=n.steamid AND d.nickname=n.nickname
             WHERE n.steamid=$sid
             ORDER BY n.position
            """,("$sid",steamId)))
        using (var reader=cmd.ExecuteReader())
        {
            while(reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                string nick=reader.GetString(0);
                string raw=reader.IsDBNull(1)?"":reader.GetString(1);
                DateTimeOffset? sortDate=null;
                string display="Unknown";
                if(DateTimeOffset.TryParse(raw,CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,out var parsed))
                {
                    sortDate=parsed;
                    display=parsed.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss",CultureInfo.InvariantCulture);
                }
                else if(!string.IsNullOrWhiteSpace(raw))display=raw;
                entries.Add(new NicknameHistoryEntry(entries.Count+1,nick,display,sortDate,
                    string.Equals(nick,currentNickname,StringComparison.Ordinal)));
            }
        }
        tx.Commit();
        return entries;
    },ct);

    // Keep the legacy Task API for callers that do not need the revision.
    public async Task SavePlayerAsync(PlayerRecord player,long expectedRevision,CancellationToken ct=default)
    {
        await SavePlayerWithRevisionAsync(player,expectedRevision,ct);
    }

    /// <summary>
    /// Return the committed revision from the same transaction. This lets the
    /// ViewModel update one bound row instead of re-reading thousands of records
    /// after every Note/autoban/counter edit. Other-process writes are still
    /// picked up by the normal revision polling.
    /// </summary>
    public Task<long> SavePlayerWithRevisionAsync(PlayerRecord player,long expectedRevision,CancellationToken ct=default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!long.TryParse(player.SteamId,out _) || player.SteamId.Length!=17) throw new ArgumentException("Invalid Steam ID.");
        if (!new[]{"","good","warn","bad"}.Contains(player.Marker)) throw new ArgumentException("Invalid rating.");
        if (player.Wins<0 || player.Losses<0 || player.Leaves<0) throw new ArgumentException("Counters must be non-negative.");
        using var c=Open(write:true);using var tx=c.BeginTransaction();
        VerifyRevision(c,tx,"players",expectedRevision);
        var old=LoadPlayer(c,tx,player.SteamId) ?? throw new KeyNotFoundException("The player no longer exists.");
        var next=(JsonObject)old.DeepClone();
        next["note"]=player.Note;next["marker"]=player.Marker;
        next["autokick"]=player.Autokick;
        bool banChanged = Bool(old,"autokick") != player.Autokick;
        if (banChanged && player.Autokick)
        {
            next["ban_at"] = DateTimeOffset.UtcNow.ToString("o",CultureInfo.InvariantCulture);
            next["ban_until"] = null; // WPF ban button means permanent until explicitly removed.
        }
        next["wins"]=player.Wins;next["losses"]=player.Losses;next["leaves"]=player.Leaves;
        if (JsonNode.DeepEquals(old,next)) {tx.Commit();return expectedRevision;}
        const string sql="""
          UPDATE players SET note=$note, marker=$marker, autokick=$autokick,
            ban_until=$ban_until, wins=$wins, losses=$losses, leaves=$leaves,
            present=(present | $bits), extra=$extra WHERE steamid=$id
          """;
        var extras=LoadExtra(c,tx,"players","steamid",player.SteamId);
        foreach (var key in new[]{"note","marker","autokick","ban_until","wins","losses","leaves"}) extras.Remove(key);
        if (banChanged && player.Autokick) extras["ban_at"] = next["ban_at"]?.DeepClone();
        Execute(c,tx,sql,
            ("$note",player.Note),("$marker",player.Marker),("$autokick",player.Autokick?1:0),
            ("$ban_until",banChanged && player.Autokick ? null : old["ban_until"]?.ToString()),("$wins",player.Wins),
            ("$losses",player.Losses),("$leaves",player.Leaves),
            ("$bits",Bit("note",PlayerFields)|Bit("marker",PlayerFields)|Bit("autokick",PlayerFields)|Bit("ban_until",PlayerFields)|Bit("wins",PlayerFields)|Bit("losses",PlayerFields)|Bit("leaves",PlayerFields)),
            ("$extra",extras.ToJsonString()),("$id",player.SteamId));
        Audit(c,tx,"players",player.SteamId,old,next);
        Increment(c,tx,"players");
        long newRevision=Revision(c,"players",tx);
        tx.Commit();
        return newRevision;
    },ct);

    public Task SaveFavoriteAsync(string id,bool favorite,long expectedRevision,CancellationToken ct=default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var c=Open(write:true);using var tx=c.BeginTransaction();
        VerifyRevision(c,tx,"preferences",expectedRevision);
        var old=ReadPreferences(c,tx);
        var next=(JsonObject)old.DeepClone();
        var favorites=next["favorites"] is JsonArray arr
            ? arr.Select(x=>x?.ToString()??"").Where(x=>x.Length>0).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (favorite) favorites.Add(id); else favorites.Remove(id);
        next["favorites"]=new JsonArray(favorites.Order(StringComparer.Ordinal).Select(x=>JsonValue.Create(x)).ToArray());
        if (JsonNode.DeepEquals(old,next)) {tx.Commit();return;}
        SetPreference(c,tx,"favorites",next["favorites"]!.ToJsonString());
        Audit(c,tx,"preferences","settings",old,next);
        Increment(c,tx,"preferences");tx.Commit();
    },ct);

    public Task SaveLocalNicknameAsync(string nickname,long expectedRevision,CancellationToken ct=default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (nickname.Length>80 || nickname.Contains('\n') || nickname.Contains('\r')) throw new ArgumentException("Invalid nickname.");
        using var c=Open(write:true);using var tx=c.BeginTransaction();
        VerifyRevision(c,tx,"preferences",expectedRevision);
        var old=ReadPreferences(c,tx);var next=(JsonObject)old.DeepClone();next["local_nick"]=nickname;
        if (JsonNode.DeepEquals(old,next)) {tx.Commit();return;}
        SetPreference(c,tx,"local_nick",JsonSerializer.Serialize(nickname));
        Audit(c,tx,"preferences","settings",old,next);
        Increment(c,tx,"preferences");tx.Commit();
    },ct);

    public Task SaveAutoKickEnabledAsync(bool enabled,long expectedRevision,CancellationToken ct=default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var c=Open(write:true);
        using var tx=c.BeginTransaction();
        VerifyRevision(c,tx,"preferences",expectedRevision);
        var old=ReadPreferences(c,tx);
        var next=(JsonObject)old.DeepClone();
        next["autokick_enabled"]=enabled;
        if (JsonNode.DeepEquals(old,next)) { tx.Commit(); return; }
        SetPreference(c,tx,"autokick_enabled",enabled?"true":"false");
        Audit(c,tx,"preferences","settings",old,next);
        Increment(c,tx,"preferences");
        tx.Commit();
    },ct);

    public Task SaveMatchAsync(string id,MatchPatch patch,long expectedRevision,CancellationToken ct=default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var c=Open(write:true);using var tx=c.BeginTransaction();
        VerifyRevision(c,tx,"games",expectedRevision);
        var old=LoadFullMatch(c,tx,id) ?? throw new KeyNotFoundException("The match was deleted or moved.");
        if(patch.Deleted && !Bool(old,"deleted") &&
           string.IsNullOrEmpty(Str(old,"ended")) && string.IsNullOrEmpty(Str(old,"menu_return_at")) && string.IsNullOrEmpty(Str(old,"observed_end_at")))
            throw new InvalidOperationException("An unfinished match cannot be deleted.");
        if(patch.WinnerId!="__unset__" && patch.WinnerId!="" && patch.WinnerId!="__local__" &&
           (old["players"] is not JsonObject allPlayers || !allPlayers.ContainsKey(patch.WinnerId)))
            throw new ArgumentException("The winner must be a match participant.");
        var next=(JsonObject)old.DeepClone();
        // All writable fields are explicit. No arbitrary SQL or rewrite of participants.
        next["title"]=patch.Title; next["note"]=patch.Note;
        next["result"]=patch.Result;next["winner_id"]=patch.WinnerId=="__unset__"?null:patch.WinnerId;
        next["favorite"]=patch.Favorite;next["review_pending"]=patch.Deleted?false:patch.ReviewPending;
        next["deleted"]=patch.Deleted;
        List<string> changedMetaFields=[];
        bool changeMetadata=false;
        if(patch.UpdateMetadata)
        {
            if(patch.Title.Length>250 || (patch.Map?.Length??0)>250)throw new ArgumentException("Title and map cannot exceed 250 characters.");
            if(patch.StartTurn<0 || patch.LastTurn<0 || (patch.StartTurn.HasValue && patch.LastTurn.HasValue && patch.LastTurn<patch.StartTurn))
                throw new ArgumentException("Invalid turn numbers.");
            if(!DateTimeOffset.TryParse(patch.Started,out var start) || !DateTimeOffset.TryParse(patch.LastAt,out var last) || last<start)
                throw new ArgumentException("Invalid dates: the last record cannot precede the start.");
            if(DateTimeOffset.TryParse(Str(old,"ended"),out var ended) && ended<start)
                throw new ArgumentException("The start cannot be later than the end.");
            next["map"]=patch.Map??"";
            next["started"]=patch.Started==Str(old,"started")?patch.Started:start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss+00:00",CultureInfo.InvariantCulture);
            next["last_at"]=patch.LastAt==Str(old,"last_at")?patch.LastAt:last.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss+00:00",CultureInfo.InvariantCulture);
            next["start_turn"]=patch.StartTurn.HasValue?JsonValue.Create(patch.StartTurn.Value):null;
            next["last_turn"]=patch.LastTurn.HasValue?JsonValue.Create(patch.LastTurn.Value):null;
            foreach(var key in new[]{"map","started","last_at","start_turn","last_turn"})
                if(!JsonNode.DeepEquals(old[key],next[key]))changedMetaFields.Add(key);
            changeMetadata=changedMetaFields.Count>0;
        }
        bool markManual=(patch.Deleted && !Bool(old,"deleted")) || changeMetadata;
        if(markManual)next["manual_edit"]=true;
        if (JsonNode.DeepEquals(old,next)) {tx.Commit();return;}
        // A lightweight, complete record snapshot is written BEFORE the transaction commits.
        // If it fails, the original data is not modified.
        SnapshotMatch(id,old,patch.Deleted && !Bool(old,"deleted")?"delete":"edit");
        var extra=LoadExtra(c,tx,"matches","id",id);
        string[] keys=["title","note","result","winner_id","favorite","review_pending","deleted"];
        long bits=0;foreach (var key in keys) {extra.Remove(key);bits|=Bit(key,MatchFields);}
        foreach(var key in changedMetaFields)
            {extra.Remove(key);bits|=Bit(key,MatchFields);}
        if(markManual)
        {
            extra.Remove("manual_edit");
            bits|=Bit("manual_edit",MatchFields);
        }
        Execute(c,tx,"""
          UPDATE matches SET title=$title,note=$note,result=$result,winner_id=$winner,
            favorite=$favorite,review_pending=$pending,deleted=$deleted,
            manual_edit=CASE WHEN $set_manual=1 THEN 1 ELSE manual_edit END,
            map=CASE WHEN $change_map=1 THEN $map ELSE map END,
            started=CASE WHEN $change_started=1 THEN $started ELSE started END,
            last_at=CASE WHEN $change_lastat=1 THEN $lastat ELSE last_at END,
            start_turn=CASE WHEN $change_startturn=1 THEN $startturn ELSE start_turn END,
            last_turn=CASE WHEN $change_lastturn=1 THEN $lastturn ELSE last_turn END,
            present=(present | $bits),extra=$extra WHERE id=$id
          """,("$title",patch.Title),("$note",patch.Note),("$result",patch.Result),
          ("$winner",patch.WinnerId=="__unset__"?null:patch.WinnerId),("$favorite",patch.Favorite?1:0),
          ("$pending",patch.Deleted?0:patch.ReviewPending?1:0),("$deleted",patch.Deleted?1:0),
          ("$set_manual",markManual?1:0),
          ("$change_map",changedMetaFields.Contains("map")?1:0),
          ("$change_started",changedMetaFields.Contains("started")?1:0),
          ("$change_lastat",changedMetaFields.Contains("last_at")?1:0),
          ("$change_startturn",changedMetaFields.Contains("start_turn")?1:0),
          ("$change_lastturn",changedMetaFields.Contains("last_turn")?1:0),
          ("$map",patch.Map),("$started",Str(next,"started")),("$lastat",Str(next,"last_at")),
          ("$startturn",patch.StartTurn),("$lastturn",patch.LastTurn),
          ("$bits",bits),("$extra",extra.ToJsonString()),("$id",id));
        // Result/winner/delete/metadata affect derived statistics. Note/title/
        // favorites/review-only edits do not, so don't invalidate their cache.
        if(changeMetadata || patch.Deleted!=Bool(old,"deleted") ||
           patch.Result!=Str(old,"result") ||
           (patch.WinnerId=="__unset__"?null:patch.WinnerId)!=(old["winner_id"]?.ToString()))
            RefreshStatistics(c,tx,id,next);
        Audit(c,tx,"games",id,old,next);
        Increment(c,tx,"games");tx.Commit();
    },ct);

    /// <summary>
    /// Persist complete Lua rosters and match observations in one short transaction.
    /// Existing notes, bans, ratings, counters and manually edited match fields
    /// are never replaced. Log replay is idempotent via stable match IDs/offsets.
    /// </summary>
    public Task<GameImportResult> ImportGameEventsAsync(GameImportBatch batch,
        CancellationToken ct=default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        bool changedPlayers=false, changedMatches=false;
        var newlyCompleted=new List<string>();
        using var c=Open(write:true);
        using var tx=c.BeginTransaction();
        foreach(var observation in batch.Players)
        {
            ct.ThrowIfCancellationRequested();
            string sid=observation.SteamId, nickname=observation.Nickname;
            if(sid.Length!=17 || !sid.All(char.IsAsciiDigit) ||
               string.IsNullOrWhiteSpace(nickname) || nickname.Length>300)continue;
            var old=LoadPlayer(c,tx,sid);
            if(old is null)
            {
                var values=new Dictionary<string,object?>(StringComparer.Ordinal)
                {
                    ["nickname"]=nickname,["note"]="",["color"]="",["marker"]="",
                    ["autokick"]=0,["wins"]=0,["losses"]=0,["leaves"]=0,
                    ["ban_until"]=null
                };
                string cols=string.Join(",",PlayerFields);
                string placeholders=string.Join(",",PlayerFields.Select((_,i)=>"$v"+i));
                using(var command=Command(c,tx,
                    $"INSERT INTO players(steamid,position,{cols},present,extra,folded) " +
                    $"VALUES ($id,(SELECT COALESCE(MAX(position),-1)+1 FROM players),{placeholders},$bits,$extra,$folded)",
                    ("$id",sid),("$bits",(1L<<PlayerFields.Length)-1),
                    ("$extra","{\"nickname_history\":[],\"nickname_dates\":{}}"),
                    ("$folded",nickname.ToLowerInvariant())))
                {
                    for(int i=0;i<PlayerFields.Length;i++)
                        command.Parameters.AddWithValue("$v"+i,values[PlayerFields[i]]??DBNull.Value);
                    command.ExecuteNonQuery();
                }
                changedPlayers=true;
            }
            else if(observation.Live && Str(old,"nickname")!=nickname)
            {
                Execute(c,tx,"UPDATE players SET nickname=$nick,folded=$folded,present=(present | $bits) WHERE steamid=$sid",
                    ("$nick",nickname),("$folded",nickname.ToLowerInvariant()),
                    ("$bits",Bit("nickname",PlayerFields)),("$sid",sid));
                var next=(JsonObject)old.DeepClone();next["nickname"]=nickname;
                Audit(c,tx,"players",sid,old,next);
                changedPlayers=true;
            }
            // This is an encounter history, not merely a set of unique names.
            string? lastName=Convert.ToString(Scalar(c,tx,
                "SELECT nickname FROM nickname_history WHERE steamid=$sid ORDER BY position DESC LIMIT 1",
                ("$sid",sid)),CultureInfo.InvariantCulture);
            if((observation.Live || old is null) && lastName!=nickname)
            {
                Execute(c,tx,"""
                    INSERT INTO nickname_history(steamid,position,nickname,folded)
                    VALUES ($sid,(SELECT COALESCE(MAX(position),-1)+1 FROM nickname_history WHERE steamid=$sid),$nick,$folded)
                    """,("$sid",sid),("$nick",nickname),("$folded",nickname.ToLowerInvariant()));
                changedPlayers=true;
            }
            if(observation.Live || old is null) Execute(c,tx,"""
                INSERT OR IGNORE INTO nickname_dates(steamid,nickname,stamp)
                VALUES ($sid,$nick,$stamp)
                """,("$sid",sid),("$nick",nickname),("$stamp",observation.SeenAt));
            if(old is null || (observation.Live && (Str(old,"nickname")!=nickname || lastName!=nickname)))
            {
                // Keep Python's trigram full-text index in sync with the canonical tables.
                using var names=Command(c,tx,
                    "SELECT folded FROM nickname_history WHERE steamid=$sid ORDER BY position",("$sid",sid));
                var parts=new List<string>{nickname.ToLowerInvariant(),sid};
                using(var r=names.ExecuteReader())while(r.Read())parts.Add(r.GetString(0));
                Execute(c,tx,"""
                    INSERT INTO search_docs(steamid,terms) VALUES ($sid,$terms)
                    ON CONFLICT(steamid) DO UPDATE SET terms=excluded.terms
                    """,("$sid",sid),("$terms",string.Join("\n",parts).Replace("\0"," ")));
            }
        }
        foreach(var observation in batch.Matches)
        {
            ct.ThrowIfCancellationRequested();
            if(observation.Id.Length!=64 || !observation.Id.All(Uri.IsHexDigit))continue;
            var old=LoadFullMatch(c,tx,observation.Id);
            if(old is not null && (Bool(old,"manual_edit") ||
               !string.IsNullOrEmpty(Str(old,"ended")) ||
               !string.IsNullOrEmpty(Str(old,"menu_return_at")) ||
               !string.IsNullOrEmpty(Str(old,"observed_end_at"))))continue;
            bool newMatch=old is null;
            if(!newMatch && Number(old!,"last_offset")>=observation.LastOffset)continue;
            bool completed=observation.Ended is not null;
            bool review=completed && observation.NotifyReview;
            if(newMatch)
            {
                var values=new Dictionary<string,object?>(StringComparer.Ordinal)
                {
                    ["title"]=observation.Title,["started"]=observation.Started,["last_at"]=observation.LastAt,
                    ["ended"]=observation.Ended,["clock"]=observation.Clock,
                    ["map"]=observation.Map,["local_nick"]=observation.LocalNick,
                    ["local_civ"]=observation.LocalCiv,["result"]="Не указано",
                    ["start_turn"]=observation.FirstTurn,["last_turn"]=observation.LastTurn,
                    ["note"]="",["last_offset"]=observation.LastOffset,
                    ["start_tick"]=null,["last_tick"]=null,["winner_id"]=null,
                    ["favorite"]=0,["deleted"]=0,["review_pending"]=review?1:0,
                    ["menu_return_at"]=null,["observed_end_at"]=null,["manual_edit"]=0,
                    ["game_start_offset"]=null,["launch_log_tick"]=null
                };
                string cols=string.Join(",",MatchFields.Select(x=>"\""+x+"\""));
                string placeholders=string.Join(",",MatchFields.Select((_,i)=>"$v"+i));
                using(var command=Command(c,tx,
                    $"INSERT INTO matches(id,position,{cols},present,extra,folded) "+
                    $"VALUES ($id,(SELECT COALESCE(MAX(position),-1)+1 FROM matches),{placeholders},$bits,$extra,'')",
                    ("$id",observation.Id),("$bits",(1L<<MatchFields.Length)-1),
                    ("$extra","{\"dn_source\":\"civ5_lua\"}")))
                {
                    for(int i=0;i<MatchFields.Length;i++)
                        command.Parameters.AddWithValue("$v"+i,values[MatchFields[i]]??DBNull.Value);
                    command.ExecuteNonQuery();
                }
                changedMatches=true;
            }
            else
            {
                Execute(c,tx,"""
                    UPDATE matches SET
                      last_at=$last,ended=$ended,map=$map,local_civ=$civ,
                      title=CASE WHEN COALESCE(TRIM(title),'')='' AND $title<>''
                                 THEN $title ELSE title END,
                      start_turn=COALESCE(start_turn,$first),last_turn=$turn,
                      last_offset=$offset,
                      present=(present | $bits)
                    WHERE id=$id
                    """,("$last",observation.LastAt),("$ended",observation.Ended),
                    ("$map",observation.Map),("$civ",observation.LocalCiv),
                    ("$title",observation.Title),
                    ("$first",observation.FirstTurn),("$turn",observation.LastTurn),
                    ("$offset",observation.LastOffset),
                    ("$bits",Bit("title",MatchFields)|Bit("last_at",MatchFields)|Bit("ended",MatchFields)|
                        Bit("map",MatchFields)|Bit("local_civ",MatchFields)|
                        Bit("start_turn",MatchFields)|Bit("last_turn",MatchFields)|
                        Bit("last_offset",MatchFields)),("$id",observation.Id));
                changedMatches=true;
            }
            if(review)
            {
                Execute(c,tx,"UPDATE matches SET review_pending=1, present=(present | $bits) WHERE id=$id",
                    ("$bits",Bit("review_pending",MatchFields)),("$id",observation.Id));
                newlyCompleted.Add(observation.Id);
            }
            foreach(var participant in observation.Participants)
            {
                if(participant.SteamId.Length!=17 || !participant.SteamId.All(char.IsAsciiDigit))continue;
                Execute(c,tx,"""
                    INSERT INTO match_players(match_id,steamid,position,nickname,civ,present,extra)
                    VALUES ($id,$sid,(SELECT COALESCE(MAX(position),-1)+1 FROM match_players WHERE match_id=$id),$nick,$civ,3,'{}')
                    ON CONFLICT(match_id,steamid) DO UPDATE SET nickname=excluded.nickname,civ=excluded.civ,
                        present=(match_players.present | 3)
                    """,("$id",observation.Id),("$sid",participant.SteamId),
                    ("$nick",participant.Nickname),("$civ",participant.Civilization));
            }
            // Indexes and aggregated statistics are maintained in this transaction,
            // just like the Python schema-v3 writer. Pending matches are not counted.
            var persisted=LoadFullMatch(c,tx,observation.Id);
            if(persisted is not null)RefreshStatistics(c,tx,observation.Id,persisted);
        }
        if(changedPlayers)Increment(c,tx,"players");
        if(changedMatches)Increment(c,tx,"games");
        tx.Commit();
        return new GameImportResult(changedPlayers,changedMatches,newlyCompleted);
    },ct);

    public Task CreateBackupAsync(string destination,CancellationToken ct=default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) throw new IOException("A backup file already exists.");
        // SQLite online backup API includes committed WAL pages and is safe with readers.
        using var source=Open();
        using var target=new SqliteConnection(new SqliteConnectionStringBuilder
        {DataSource=destination,Mode=SqliteOpenMode.ReadWriteCreate}.ToString());
        target.Open();
        try {source.BackupDatabase(target);}
        catch {target.Close();File.Delete(destination);throw;}
    },ct);

    /// <summary>SQLite online restore. The source must be an independently validated schema-v3 database.</summary>
    public Task RestoreFromBackupAsync(string sourceFile,CancellationToken ct=default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var source=new SqliteRepository(sourceFile).Open();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var destination=Open(write:true);
        source.BackupDatabase(destination);
        ct.ThrowIfCancellationRequested();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    },ct);

    private static IReadOnlyList<PlayerRecord> ReadPlayers(SqliteConnection c,SqliteTransaction tx,HashSet<string> fav,CancellationToken ct)
    {
        List<PlayerRecord> players=[];
        using(var cmd=Command(c,tx,"SELECT * FROM players ORDER BY position"))
        using(var r=cmd.ExecuteReader())while(r.Read())
        {
            ct.ThrowIfCancellationRequested();
            var record=Decode(r,"steamid",PlayerFields);
            var player=new PlayerRecord{SteamId=Str(record,"steamid"),Nickname=Str(record,"nickname"),
                Note=Str(record,"note"),Marker=Str(record,"marker"),Wins=Number(record,"wins"),
                Losses=Number(record,"losses"),Leaves=Number(record,"leaves"),
                Autokick=Bool(record,"autokick"),BanUntil=Str(record,"ban_until"),
                Favorite=fav.Contains(Str(record,"steamid"))};
            players.Add(player);
        }
        var names=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        using(var cmd=Command(c,tx,"SELECT steamid,nickname FROM nickname_history ORDER BY steamid,position"))
        using(var r=cmd.ExecuteReader())while(r.Read())
        {
            string id=r.GetString(0);
            if (!names.TryGetValue(id,out var values))names[id]=values=[];
            values.Add(r.GetString(1));
        }
        foreach(var p in players)if(names.TryGetValue(p.SteamId,out var values))p.PreviousNames=string.Join(", ",values.Where(x=>x!=p.Nickname));
        return players;
    }

    private static IReadOnlyList<MatchRecord> ReadMatches(SqliteConnection c,SqliteTransaction tx,CancellationToken ct)
    {
        List<MatchRecord> matches=[];
        using(var cmd=Command(c,tx,"SELECT * FROM matches ORDER BY started DESC, position DESC"))
        using(var r=cmd.ExecuteReader())while(r.Read())
        {
            ct.ThrowIfCancellationRequested();
            var record=Decode(r,"id",MatchFields);
            matches.Add(ModelMatch(record));
        }
        var byId=matches.ToDictionary(x=>x.Id,StringComparer.Ordinal);
        var participants=new Dictionary<string,List<MatchParticipant>>(StringComparer.Ordinal);
        using(var cmd=Command(c,tx,"SELECT match_id,steamid,nickname,civ FROM match_players ORDER BY match_id,position"))
        using(var r=cmd.ExecuteReader())while(r.Read())
        {
            ct.ThrowIfCancellationRequested();
            string id=r.GetString(0);if(!byId.ContainsKey(id))continue;
            if(!participants.TryGetValue(id,out var list))participants[id]=list=[];
            list.Add(new MatchParticipant(r.GetString(1),r.IsDBNull(2)?"":r.GetString(2),r.IsDBNull(3)?"":r.GetString(3)));
        }
        foreach(var m in matches)
        {
            // Host is always a participant. match_players contains only opponents.
            if(!participants.TryGetValue(m.Id,out var people))people=[];
            m.PlayerDetails=people;
            m.Participants=people.Count+1;
            m.ParticipantNames=string.Join(", ",new[]{m.LocalParticipantLabel}.Concat(people.Select(x=>x.Nickname)));
            m.WinnerName=WinnerName(m,people);
            // Do not retain complete event arrays for every match.
        }
        return matches;
    }

    private static MatchRecord ModelMatch(JsonObject row) => new()
    {
        Id=Str(row,"id"),Title=Str(row,"title"),Result=Str(row,"result","Не указано"),
        WinnerId=row["winner_id"] is null?"__unset__":Str(row,"winner_id"),Note=Str(row,"note"),Favorite=Bool(row,"favorite"),
        Deleted=Bool(row,"deleted"),ReviewPending=Bool(row,"review_pending"),
        Started=Str(row,"started"),LastAt=Str(row,"last_at"),
        StartTurn=row["start_turn"] is null?null:Number(row,"start_turn"),
        LastTurn=row["last_turn"] is null?null:Number(row,"last_turn"),Ended=Str(row,"ended"),
        Map=Str(row,"map"),LocalCiv=Str(row,"local_civ"),LocalNick=Str(row,"local_nick"),
        Completed= !string.IsNullOrEmpty(Str(row,"ended")) || !string.IsNullOrEmpty(Str(row,"menu_return_at")) || !string.IsNullOrEmpty(Str(row,"observed_end_at"))
    };

    private static string WinnerName(MatchRecord m,IReadOnlyList<MatchParticipant> people)
    {
        if(m.WinnerId=="__local__")return string.IsNullOrWhiteSpace(m.LocalNick)?"You":m.LocalNick;
        if(m.WinnerId=="__unset__")return "Not selected";
        if(m.WinnerId=="")return "No winner";
        return people.FirstOrDefault(x=>x.SteamId==m.WinnerId)?.Nickname??"Not selected";
    }

    private static JsonObject Decode(SqliteDataReader r,string key,string[] fields)
    {
        JsonObject o;
        try{o=JsonNode.Parse(r.GetString(r.GetOrdinal("extra"))) as JsonObject ?? new();}
        catch(JsonException e){throw new InvalidDataException($"Invalid JSON in the extra field of {key}.",e);}
        long present=r.GetInt64(r.GetOrdinal("present"));
        for(int i=0;i<fields.Length;i++)if((present & (1L<<i))!=0)
        {
            int index=r.GetOrdinal(fields[i]);object value=r.GetValue(index);
            o[fields[i]]=value is DBNull?null:BooleanFields.Contains(fields[i])?JsonValue.Create(Convert.ToInt64(value,CultureInfo.InvariantCulture)!=0):JsonSerializer.SerializeToNode(value);
        }
        o[key]=r.GetString(r.GetOrdinal(key));
        return o;
    }

    private static JsonObject? LoadPlayer(SqliteConnection c,SqliteTransaction tx,string sid)
    {
        JsonObject? result;
        using(var cmd=Command(c,tx,"SELECT * FROM players WHERE steamid=$sid",("$sid",sid)))
        using(var r=cmd.ExecuteReader())result=r.Read()?Decode(r,"steamid",PlayerFields):null;
        if(result is null)return null;
        JsonArray names=new();JsonObject dates=new();
        using(var cmd=Command(c,tx,"SELECT nickname FROM nickname_history WHERE steamid=$sid ORDER BY position",("$sid",sid)))
        using(var r=cmd.ExecuteReader())while(r.Read())names.Add(r.GetString(0));
        using(var cmd=Command(c,tx,"SELECT nickname,stamp FROM nickname_dates WHERE steamid=$sid",("$sid",sid)))
        using(var r=cmd.ExecuteReader())while(r.Read())dates[r.GetString(0)]=r.GetString(1);
        // Maintain original key presence if Python's RelationalSchema wrote explicit empty arrays.
        if(result.ContainsKey("nickname_history")||names.Count>0)result["nickname_history"]=names;
        if(result.ContainsKey("nickname_dates")||dates.Count>0)result["nickname_dates"]=dates;
        return result;
    }

    private static JsonObject? LoadFullMatch(SqliteConnection c,SqliteTransaction tx,string id)
    {
        JsonObject? record;
        using(var cmd=Command(c,tx,"SELECT * FROM matches WHERE id=$id",("$id",id)))
        using(var r=cmd.ExecuteReader())record=r.Read()?Decode(r,"id",MatchFields):null;
        if(record is null)return null;
        var people=new JsonObject();
        using(var cmd=Command(c,tx,"SELECT * FROM match_players WHERE match_id=$id ORDER BY position",("$id",id)))
        using(var r=cmd.ExecuteReader())while(r.Read())
        {
            var p=Decode(r,"steamid",PartFields);
            string sid=Str(p,"steamid");p.Remove("steamid");
            people[sid]=p;
        }
        using(var cmd=Command(c,tx,"SELECT steamid,kind,payload FROM match_events WHERE match_id=$id ORDER BY steamid,kind,position",("$id",id)))
        using(var r=cmd.ExecuteReader())while(r.Read())
        {
            string sid=r.GetString(0),kind=r.GetString(1);
            if(people[sid] is not JsonObject p)continue;
            if(p[kind] is not JsonArray events)p[kind]=events=new JsonArray();
            events.Add(JsonNode.Parse(r.GetString(2)));
        }
        if(record.ContainsKey("players")||people.Count>0)record["players"]=people;
        return record;
    }

    private static JsonObject ReadPreferences(SqliteConnection c,SqliteTransaction tx)
    {
        var prefs=new JsonObject();
        using var cmd=Command(c,tx,"SELECT key,value FROM settings ORDER BY position");using var r=cmd.ExecuteReader();
        while(r.Read())prefs[r.GetString(0)]=JsonNode.Parse(r.GetString(1));
        return prefs;
    }

    private static JsonObject LoadExtra(SqliteConnection c,SqliteTransaction tx,string table,string key,string id)
    {
        // Both identifiers are from hard-coded allowlists in this repository.
        var text=Convert.ToString(Scalar(c,tx,$"SELECT extra FROM {table} WHERE {key}=$id",("$id",id)),CultureInfo.InvariantCulture);
        return JsonNode.Parse(text??"{}") as JsonObject ?? new();
    }

    private static void SetPreference(SqliteConnection c,SqliteTransaction tx,string name,string json)
    {
        Execute(c,tx,"INSERT OR IGNORE INTO settings_record(key,position) VALUES ('settings',0)");
        Execute(c,tx,"""
          INSERT INTO settings(key,position,value) VALUES ($key,(SELECT COALESCE(MAX(position),-1)+1 FROM settings),$val)
          ON CONFLICT(key) DO UPDATE SET value=excluded.value
          """,("$key",name),("$val",json));
    }

    private static void VerifyRevision(SqliteConnection c,SqliteTransaction tx,string col,long expected)
    {
        if(Revision(c,col,tx)!=expected)
            throw new InvalidOperationException("The data changed in another window or process. Refresh before saving.");
    }
    private static long Revision(SqliteConnection c,string name,SqliteTransaction? tx=null) => Convert.ToInt64(Scalar(c,tx,"SELECT revision FROM revisions WHERE collection=$name",("$name",name)) ?? -1L,CultureInfo.InvariantCulture);
    private static void Increment(SqliteConnection c,SqliteTransaction tx,string collection)
    {
        Execute(c,tx,"UPDATE revisions SET revision=revision+1 WHERE collection=$name",("$name",collection));
        Execute(c,tx,"DELETE FROM audit WHERE seq <= (SELECT COALESCE(MAX(seq),0)-1000 FROM audit)");
    }
    private static void Audit(SqliteConnection c,SqliteTransaction tx,string col,string key,JsonObject old,JsonObject next)
    {
        string before=old.ToJsonString(),after=next.ToJsonString();
        if(before.Length>262144 || after.Length>262144)return;
        Execute(c,tx,"INSERT INTO audit(collection,key,old,new,at) VALUES ($col,$key,$old,$new,$at)",
            ("$col",col),("$key",key),("$old",before),("$new",after),("$at",DateTimeOffset.UtcNow.ToString("O")));
    }
    private static long Bit(string field,string[] fields) => 1L<<Array.IndexOf(fields,field);
    private static string Str(JsonObject? j,string key,string fallback="") => j is null || j[key] is null?fallback:j[key]!.ToString();
    private static bool Bool(JsonObject? j,string key,bool fallback=false)
    {
        if(j is null || j[key] is null)return fallback;
        return bool.TryParse(j[key]!.ToString(),out var result)?result:j[key]!.ToString()=="1";
    }
    private static long Number(JsonObject? j,string key) => long.TryParse(Str(j,key),NumberStyles.Integer,CultureInfo.InvariantCulture,out long v)?v:0;
    private static SqliteCommand Command(SqliteConnection c,SqliteTransaction? tx,string sql,params (string Key,object? Value)[] args)
    {
        var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.Transaction=tx;
        foreach(var (key,value) in args)cmd.Parameters.AddWithValue(key,value??DBNull.Value);
        return cmd;
    }
    private static object? Scalar(SqliteConnection c,SqliteTransaction? tx,string sql,params (string Key,object? Value)[] args)
    {using var cmd=Command(c,tx,sql,args);return cmd.ExecuteScalar();}
    private static void Execute(SqliteConnection c,SqliteTransaction? tx,string sql,params (string Key,object? Value)[] args)
    {using var cmd=Command(c,tx,sql,args);cmd.ExecuteNonQuery();}

    private static void SnapshotMatch(string id,JsonObject before,string operation)
    {
        // Compatible with Python v2.09: MatchHistory.restore_match_snapshot().
        string dir=Path.Combine(App.Workspace,"backups","before-match-"+operation);
        Directory.CreateDirectory(dir);
        string fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(id)))[..16];
        string name=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff",CultureInfo.InvariantCulture)+"-"+fingerprint+".json";
        string target=Path.Combine(dir,name),temp=target+".tmp";
        var envelope=new JsonObject
        {
            ["format"]="deathnote-match-undo-v1",["operation"]=operation,
            ["match_id"]=id,["saved_at"]=DateTimeOffset.UtcNow.ToString("O"),
            ["record"]=before.DeepClone()
        };
        try
        {
            File.WriteAllText(temp,envelope.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
            File.Move(temp,target);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }

    private static string MapName(string? key)
    {
        string last=(key??"").Replace('\\','/').Split('/').Last();
        int dot=last.LastIndexOf('.');if(dot>=0)last=last[..dot];
        last=string.Join(" ",last.Replace('_',' ').Split(' ',StringSplitOptions.RemoveEmptyEntries));
        return last.ToLowerInvariant() switch {"pangaea" or "пангея" => "Pangaea","continents" or "континенты" => "Continents",
            "archipelago" or "архипелаг" => "Archipelago","fractal" or "фрактал"=>"Fractal",
            "west vs east" or "запад против востока"=>"West vs East",_=>string.IsNullOrWhiteSpace(last)?"Unknown":last};
    }
    private static string CivName(string name) => CivilizationNames.Format(name);
    private static long? Elapsed(JsonObject m)
    {
        try
        {
            if(m["start_turn"] is null && string.IsNullOrEmpty(Str(m,"ended")))return null;
            if(Str(m,"clock")=="game" || m["save_metadata"] is not null)
            {
                if(DateTimeOffset.TryParse(Str(m,"last_at"),out var last) && DateTimeOffset.TryParse(Str(m,"started"),out var start))
                    return Math.Max(0,(long)(last-start).TotalSeconds);
            }
            else if(double.TryParse(Str(m,"start_tick"),CultureInfo.InvariantCulture,out var a) &&
                    double.TryParse(Str(m,"last_tick"),CultureInfo.InvariantCulture,out var b) && b>=a)return (long)(b-a);
        }
        catch (OverflowException) { }
        return null;
    }

    private static void CountDelta(SqliteConnection c,SqliteTransaction tx,string kind,string key,int delta)
    {
        Execute(c,tx,"""
          INSERT INTO stat_counts(kind,key,n) VALUES ($kind,$key,$n)
          ON CONFLICT(kind,key) DO UPDATE SET n=n+excluded.n
          """,("$kind",kind),("$key",key),("$n",delta));
        if(delta<0)Execute(c,tx,"DELETE FROM stat_counts WHERE kind=$kind AND key=$key AND n=0",("$kind",kind),("$key",key));
    }
    private static void RefreshStatistics(SqliteConnection c,SqliteTransaction tx,string id,JsonObject record)
    {
        var old=Command(c,tx,"SELECT map_key,civ,month,result,decided,win,elapsed FROM stat_facts WHERE id=$id",("$id",id));
        string map="",civ="",month="",result="";long decided=0,win=0;long?elapsed=null;
        bool present;
        using(var r=old.ExecuteReader())
        {
            present=r.Read();
            if(present)
            {
                map=r.IsDBNull(0)?"":r.GetString(0);civ=r.IsDBNull(1)?"":r.GetString(1);
                month=r.IsDBNull(2)?"":r.GetString(2);result=r.IsDBNull(3)?"":r.GetString(3);
                decided=r.GetInt64(4);win=r.GetInt64(5);elapsed=r.IsDBNull(6)?null:r.GetInt64(6);
            }
        }
        old.Dispose();
        if(present)
        {
            CountDelta(c,tx,"maps",map,-1);if(civ.Length>0)CountDelta(c,tx,"civs",civ,-1);
            CountDelta(c,tx,"months",month,-1);CountDelta(c,tx,"results",result,-1);
            List<string> oldPeople=[];
            using(var cmd=Command(c,tx,"SELECT sid FROM stat_people WHERE id=$id",("$id",id)))
            using(var r=cmd.ExecuteReader())while(r.Read())oldPeople.Add(r.GetString(0));
            foreach(var sid in oldPeople)CountDelta(c,tx,"opponents",sid,-1);
            Execute(c,tx,"UPDATE stat_total SET total=total-1,decided=decided-$d,wins=wins-$w,known=known-$k,seconds=seconds-$s WHERE id=1",
                ("$d",decided),("$w",win),("$k",elapsed.HasValue?1:0),("$s",elapsed??0));
            Execute(c,tx,"DELETE FROM stat_facts WHERE id=$id",("$id",id));
        }
        Execute(c,tx,"DELETE FROM stat_invalid WHERE id=$id",("$id",id));
        if(Bool(record,"deleted") || !(Str(record,"ended").Length>0 || Str(record,"menu_return_at").Length>0 || Str(record,"observed_end_at").Length>0))return;
        if(!DateTimeOffset.TryParse(Str(record,"started"),out var started))
        {
            Execute(c,tx,"INSERT INTO stat_invalid(id) VALUES ($id)",("$id",id));return;
        }
        string label=MapName(Str(record,"map"));map=label.ToLowerInvariant();
        civ=CivName(Str(record,"local_civ"));month=started.LocalDateTime.ToString("yyyy-MM",CultureInfo.InvariantCulture);
        // MatchHistory.result_of(): reject unrecognized legacy result codes.
        var rawResult=Str(record,"result","Не указано");
        result=Results.TryGetValue(rawResult,out var translated)?translated
            :Results.Values.Contains(rawResult,StringComparer.Ordinal)?rawResult:"Unspecified";
        var participants=record["players"] as JsonObject??new();
        string winner=Str(record,"winner_id");decided=winner=="__local__" || participants.ContainsKey(winner)?1:0;win=winner=="__local__"?1:0;
        elapsed=Elapsed(record);
        long pos=Convert.ToInt64(Scalar(c,tx,"SELECT position FROM matches WHERE id=$id",("$id",id)),CultureInfo.InvariantCulture);
        Execute(c,tx,"""
          INSERT INTO stat_facts(id,started,pos,map_key,map_label,civ,month,result,decided,win,elapsed)
          VALUES ($id,$started,$pos,$map,$label,$civ,$month,$result,$decided,$win,$elapsed)
          """,("$id",id),("$started",Str(record,"started")),("$pos",pos),("$map",map),
          ("$label",label),("$civ",civ),("$month",month),("$result",result),
          ("$decided",decided),("$win",win),("$elapsed",elapsed));
        int index=0;
        foreach(var (sid,part) in participants)
        {
            Execute(c,tx,"INSERT INTO stat_people(id,sid,nick,started,pos,part_pos) VALUES ($id,$sid,$nick,$started,$pos,$part_pos)",
                ("$id",id),("$sid",sid),("$nick",Str(part as JsonObject,"nickname")),
                ("$started",Str(record,"started")),("$pos",pos),("$part_pos",index++));
            CountDelta(c,tx,"opponents",sid,1);
        }
        CountDelta(c,tx,"maps",map,1);if(civ.Length>0)CountDelta(c,tx,"civs",civ,1);
        CountDelta(c,tx,"months",month,1);CountDelta(c,tx,"results",result,1);
        Execute(c,tx,"UPDATE stat_total SET total=total+1,decided=decided+$d,wins=wins+$w,known=known+$k,seconds=seconds+$s WHERE id=1",
            ("$d",decided),("$w",win),("$k",elapsed.HasValue?1:0),("$s",elapsed??0));
    }
}

public sealed record MatchPatch(string Title,string Note,string Result,string WinnerId,bool Favorite,bool ReviewPending,bool Deleted,
    bool UpdateMetadata=false,string? Map=null,string? Started=null,string? LastAt=null,long? StartTurn=null,long? LastTurn=null);
