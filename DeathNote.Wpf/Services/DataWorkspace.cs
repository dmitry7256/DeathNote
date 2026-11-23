namespace DeathNote.Wpf.Services;

public static class DataWorkspace
{
    public static string DatabaseFile => Path.Combine(App.Workspace,"DeathNote.db");
    public static string Backups => Path.Combine(App.Workspace,"backups","wpf-match-snapshots");
    public static bool Available => File.Exists(DatabaseFile);
}
