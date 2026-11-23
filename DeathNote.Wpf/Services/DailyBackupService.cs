using DeathNote.Wpf.Data;

namespace DeathNote.Wpf.Services;

public sealed class DailyBackupService
{
    private readonly SqliteRepository _repository;
    public DailyBackupService(SqliteRepository repository) { _repository=repository; }
    public async Task EnsureTodayAsync(CancellationToken ct)
    {
        var dir=Path.Combine(App.Workspace,"backups","sqlite");
        Directory.CreateDirectory(dir);
        var date=DateTime.Now.ToString("yyyyMMdd");
        if(Directory.GetFiles(dir,"wpf-daily-"+date+"-*.db").Length>0)return;
        var destination=Path.Combine(dir,"wpf-daily-"+date+"-"+Guid.NewGuid().ToString("N")[..8]+".db");
        await _repository.CreateBackupAsync(destination,ct);
        // Only prune backups created by this version, leaving legacy backups intact.
        var files=Directory.GetFiles(dir,"wpf-daily-*.db").OrderByDescending(x=>x,StringComparer.Ordinal).ToList();
        foreach(var old in files.Skip(14))try{File.Delete(old);}catch(IOException){}
    }
}
