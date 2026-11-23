using System.Windows;

namespace DeathNote.Wpf;

public partial class App : Application
{
    public static bool IsShuttingDown { get; private set; }
    public static bool UiBenchmarkMode { get; private set; }
    public static bool RealBenchmarkMode { get; private set; }
    public static bool GameSelfTestMode { get; private set; }
    public static bool NativeRepairSelfTestMode { get; private set; }
    public static string? GameSelfTestReportPath { get; private set; }
    public static int BenchmarkRuns { get; private set; } = 3;
    public static string? BenchmarkDatabaseFile { get; private set; }
    public static string? BenchmarkLogFile { get; private set; }
    public static string Workspace { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;
        UiBenchmarkMode = args.Contains("--ui-benchmark", StringComparer.OrdinalIgnoreCase);
        RealBenchmarkMode = args.Contains("--real-benchmark", StringComparer.OrdinalIgnoreCase);
        GameSelfTestMode = args.Contains("--game-selftest", StringComparer.OrdinalIgnoreCase);
        NativeRepairSelfTestMode = args.Contains("--native-repair-selftest", StringComparer.OrdinalIgnoreCase);
        static string? Option(string[] commandLine, string flag)
        {
            int position = Array.FindIndex(commandLine,
                x => string.Equals(x,flag,StringComparison.OrdinalIgnoreCase));
            return position >= 0 && position + 1 < commandLine.Length
                ? commandLine[position+1] : null;
        }
        if (int.TryParse(Option(args,"--runs"),out int requestedRuns))
            BenchmarkRuns=Math.Clamp(requestedRuns,1,8);
        BenchmarkDatabaseFile=Option(args,"--database");
        BenchmarkLogFile=Option(args,"--log");
        GameSelfTestReportPath=Option(args,"--report");
        int index = Array.IndexOf(args, "--workspace");
        // Installed and portable WPF builds use the same stable per-user data location.
        // The application binaries live under Program Files or LocalAppData\Programs,
        // but user SQLite data must NEVER be stored in the installation directory.
        // Explicit --workspace remains available for isolated development workspaces.
        string local=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeathNote","data");
        // Benchmark modes do not use the user workspace even when one is passed.
        // Only opt-in SQLite read-only connection accepts --database.
        Workspace = (UiBenchmarkMode || RealBenchmarkMode || GameSelfTestMode || NativeRepairSelfTestMode) ?
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeathNote.Wpf","benchmark","isolated") :
            index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : local;
        Directory.CreateDirectory(Workspace);
        base.OnStartup(e);
        DispatcherUnhandledException += (_, x) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(Workspace, "deathnote-wpf-errors.log"),
                    $"{DateTimeOffset.Now:u} {x.Exception}\n");
            }
            catch (IOException) { }
            MessageBox.Show(x.Exception.Message, "Death Note — error", MessageBoxButton.OK, MessageBoxImage.Error);
            x.Handled = true;
        };
    }

    public static void UseWorkspace(string path)
    {
        Workspace=Path.GetFullPath(path);
        Directory.CreateDirectory(Workspace);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsShuttingDown = true;
        base.OnExit(e);
    }
}
