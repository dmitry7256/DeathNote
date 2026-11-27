using System.Diagnostics;
using System.Net.Http;

namespace DeathNote.Wpf.Services;

/// <summary>
/// Downloads a trusted Death Note installer to the user's temp directory and launches it.
/// The installer then replaces the installed app while keeping LocalAppData user data.
/// </summary>
public static class InstallerUpdateService
{
    public sealed record DownloadedInstaller(string FilePath, string VersionLabel);

    public static async Task<DownloadedInstaller> DownloadAsync(string url, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host != "github.com"
            || !uri.AbsolutePath.StartsWith("/dmitry7256/DeathNote/releases/download/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Update URL is not a trusted Death Note installer release.");
        }

        string fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The latest release does not provide a valid installer executable.");

        string tempDir = Path.Combine(Path.GetTempPath(), "DeathNote", "updates");
        Directory.CreateDirectory(tempDir);
        string targetPath = Path.Combine(tempDir, fileName);
        string partialPath = targetPath + ".download";
        if (File.Exists(partialPath)) File.Delete(partialPath);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeathNote-WPF-Updater/3.04");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? totalBytes = response.Content.Headers.ContentLength;

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long downloaded = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (totalBytes is > 0)
                {
                    int percent = Math.Clamp((int)((downloaded * 100L) / totalBytes.Value), 0, 100);
                    progress?.Report(percent);
                }
            }
        }

        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Move(partialPath, targetPath);
        progress?.Report(100);
        string versionLabel = fileName.Replace("DeathNote_Setup_", string.Empty).Replace("_x64.exe", string.Empty);
        return new DownloadedInstaller(targetPath, versionLabel);
    }

    public static void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Installer file was not found.", installerPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath()
        });
    }
}
