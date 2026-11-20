using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace FrostClient;

// ─────────────────────────────────────────────────────────────
// How updates work now
//   • Every GitHub release is tagged with a version: v1.0.12, v1.0.13, ...
//   • Each release has the Inno Setup installer attached as "FrostClient-Setup.exe".
//   • The launcher compares its own CurrentVersion with the highest release tag.
//   • To update, it downloads the installer, closes itself and runs it silently
//     over the existing install (the installer replaces the launcher, resources
//     zip, jar/json... and starts the launcher again).
//
// The old updater copied the *installer* over FrostClient.exe, which turned the
// launcher into the setup wizard, and compared the installed exe size with the
// installer size, so it never knew when it was up to date.
// ─────────────────────────────────────────────────────────────
internal static class FrostUpdater
{
    // Bump this on EVERY release, together with MyAppVersion in the .iss
    // and the git tag (v + this number).
    public const string CurrentVersion = "1.0.11";

    private const string AssetName = "FrostClient-Setup.exe";
    private const string ReleasesApi =
        "https://api.github.com/repos/joafine-sudo/frostapp/releases?per_page=30";

    private static readonly HttpClient Http = CreateApiClient();
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateApiClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FrostClient-Updater/2.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static HttpClient CreateDownloadClient()
    {
        // The installer is large; the API client's 30 s limit would cut the download.
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FrostClient-Updater/2.0");
        return client;
    }

    // What CheckForUpdateAsync found, handed back to the UI so it can ask the
    // person before anything is downloaded.
    public sealed class UpdateCheckResult
    {
        public bool UpdateAvailable { get; init; }
        public string? LatestVersion { get; init; }
        public string? DownloadUrl { get; init; }
        public long? AssetSize { get; init; }
        // Set when the check itself could not run (no network, rate limit, no
        // matching release...) so the UI can show why.
        public string? Error { get; init; }
    }

    private static UpdateCheckResult Fail(string error) =>
        new() { UpdateAvailable = false, Error = error };

    /// <summary>
    /// Looks for the highest versioned release on GitHub and reports whether it is newer
    /// than <see cref="CurrentVersion"/>. Never downloads or changes anything and never
    /// throws: problems come back as <see cref="UpdateCheckResult.Error"/>.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            CleanOldInstallers();
            return await CheckCoreAsync();
        }
        catch (TaskCanceledException) { return Fail("GitHub did not answer in time."); }
        catch (HttpRequestException ex) { return Fail("Could not reach GitHub: " + ex.Message); }
        catch (JsonException) { return Fail("GitHub sent an unexpected answer."); }
        catch (Exception ex) { return Fail("Update check failed: " + ex.Message); }
    }

    private static async Task<UpdateCheckResult> CheckCoreAsync()
    {
        if (!TryParseVersion(CurrentVersion, out var current))
            return Fail("The launcher's own version number is invalid.");

        using var response = await Http.GetAsync(ReleasesApi);

        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden
                                or System.Net.HttpStatusCode.TooManyRequests)
            return Fail("GitHub is limiting requests right now. Try again in a few minutes.");
        if (!response.IsSuccessStatusCode)
            return Fail($"GitHub returned {(int)response.StatusCode}.");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Fail("GitHub sent an unexpected answer.");

        Version? best = null;
        string? bestUrl = null;
        long? bestSize = null;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (IsTrue(release, "draft") || IsTrue(release, "prerelease")) continue;

            var tag = release.TryGetProperty("tag_name", out var tagNode) &&
                      tagNode.ValueKind == JsonValueKind.String ? tagNode.GetString() : null;
            if (!TryParseVersion(tag, out var version)) continue;   // e.g. the old "minecraft" tag

            if (!release.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array) continue;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameNode) ||
                    !string.Equals(nameNode.GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = asset.TryGetProperty("browser_download_url", out var urlNode) &&
                          urlNode.ValueKind == JsonValueKind.String ? urlNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;

                if (best == null || version > best)
                {
                    best = version;
                    bestUrl = url;
                    bestSize = asset.TryGetProperty("size", out var sizeNode) &&
                               sizeNode.TryGetInt64(out var size) ? size : null;
                }
                break;
            }
        }

        if (best == null || bestUrl == null)
            return Fail($"No release with '{AssetName}' and a version tag (like v1.0.12) was found.");

        return new UpdateCheckResult
        {
            UpdateAvailable = best > current,
            LatestVersion = best.ToString(),
            DownloadUrl = bestUrl,
            AssetSize = bestSize
        };
    }

    /// <summary>
    /// Downloads the installer found by CheckForUpdateAsync, closes the launcher and runs the
    /// installer silently over the current install. Only call this after the person agreed.
    /// </summary>
    public static async Task DownloadAndApplyUpdateAsync(UpdateCheckResult info)
    {
        if (!info.UpdateAvailable || string.IsNullOrWhiteSpace(info.DownloadUrl))
            throw new InvalidOperationException("No update is pending; check for updates first.");

        var installer = Path.Combine(Path.GetTempPath(), $"FrostClient-Setup-{info.LatestVersion}.exe");
        bool started = false;

        try
        {
            if (File.Exists(installer)) File.Delete(installer);

            using var request = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            request.Headers.Accept.ParseAdd("application/octet-stream");

            using var response = await DownloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(
                installer, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await input.CopyToAsync(output);
                await output.FlushAsync();
            }

            long length = new FileInfo(installer).Length;
            if (info.AssetSize is long expected && length != expected)
                throw new InvalidDataException("The download is incomplete. Please try again.");
            if (length < 1_000_000)
                throw new InvalidDataException("The downloaded installer is unexpectedly small.");

            // /SILENT shows only a progress bar. The .iss has a [Run] entry flagged
            // skipifnotsilent that starts the launcher again when it finishes.
            Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = false
            });
            started = true;
        }
        catch
        {
            if (!started)
                try { if (File.Exists(installer)) File.Delete(installer); } catch { }
            throw;
        }

        // Close through WPF so MainWindow.OnClosed runs (Discord presence, settings).
        // Environment.Exit is only a fallback in case a stray thread keeps the process alive.
        _ = Task.Run(async () => { await Task.Delay(4000); Environment.Exit(0); });

        var app = System.Windows.Application.Current;
        if (app != null) app.Dispatcher.Invoke(app.Shutdown);
        else Environment.Exit(0);
    }

    // ── helpers ──────────────────────────────────────────────
    private static bool IsTrue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.True;

    // "v1.0.12" / "1.0.12" -> Version(1,0,12,0). Anything else (e.g. "minecraft") is rejected.
    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Version.TryParse(text.Trim().TrimStart('v', 'V'), out var parsed)) return false;

        // Version("1.0") < Version("1.0.0") in .NET; normalise so they compare equal.
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    // Installers left in %TEMP% by earlier updates (best effort).
    private static void CleanOldInstallers()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "FrostClient-Setup-*.exe"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(6))
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }
}