using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace DeathNote.Wpf.Services;

/// <summary>Only a distinctly named Windows WPF asset is eligible. Never offer a Python zip as an update.</summary>
public static class UpdateCheckService
{
    private const string Api = "https://api.github.com/repos/dmitry7256/DeathNote/releases/latest";
    private const string Releases = "https://github.com/dmitry7256/DeathNote/releases";
    // Public releases now use an installer, not a portable ZIP, so applying a
    // future version replaces the installed app and preserves LocalAppData.
    private const string InstallerAssetName = "DeathNote_Setup_win-x64.exe";
    private const string VersionedInstallerPrefix = "DeathNote_Setup_";
    private const string VersionedInstallerSuffix = "_x64.exe";

    public sealed record UpdateResult(string Description, string? DownloadUrl);

    public static async Task<UpdateResult> CheckAsync(CancellationToken ct = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeathNote-WPF/3.00");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.GetAsync(Api, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 1_048_576)
            throw new InvalidDataException("Release response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, ct);
        var release = json.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            return new("No stable release available.", null);
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            return new("Latest release uses an unsupported version tag.", null);
        Version installed = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(3, 0);
        if (version <= installed) return new("Current WPF build is up to date or newer than the stable release.", null);
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return new("No compatible Windows WPF package published.", null);
        foreach (var asset in assets.EnumerateArray())
        {
            string name=asset.GetProperty("name").GetString() ?? "";
            bool compatible=name.Equals(InstallerAssetName,StringComparison.OrdinalIgnoreCase);
            if (!compatible && name.StartsWith(VersionedInstallerPrefix,StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(VersionedInstallerSuffix,StringComparison.OrdinalIgnoreCase))
            {
                string assetVersion=name.Substring(VersionedInstallerPrefix.Length,
                    name.Length-VersionedInstallerPrefix.Length-VersionedInstallerSuffix.Length);
                compatible=Version.TryParse(assetVersion,out var parsedVersion) && parsedVersion==version;
            }
            if(!compatible)continue;
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com"
                || !uri.AbsolutePath.StartsWith("/dmitry7256/DeathNote/releases/download/", StringComparison.Ordinal))
                return new("Release package address failed validation.", null);
            return new($"WPF {tag} installer is available. Use Settings to download and install it without deleting your data.",url);
        }
        return new("The latest release has no compatible Death Note Windows installer.",null);
    }

    public static void OpenReleases() => OpenTrusted(Releases);
    public static void OpenTrusted(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/dmitry7256/DeathNote/releases", StringComparison.Ordinal))
            throw new InvalidOperationException("Untrusted release URL.");
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}
