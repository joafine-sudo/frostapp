using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace FrostClient;

internal static class FrostUpdater
{
    // Asset published on the GitHub release.
    private const string AssetName = "FrostClient-Setup.exe";

    // File names the installed launcher can have on disk. The old code only accepted
    // "FrostClient-Setup.exe", so the check failed for a normal "FrostClient.exe" install
    // and the updater always answered "Updates only run from the installed FrostClient.exe".
    private static readonly string[] InstalledExeNames = { "FrostClient.exe", AssetName };

    private const string ReleaseTag = "minecraft";
    private const string ReleaseApi =
        "https://api.github.com/repos/joafine-sudo/frostapp/releases/tags/minecraft";
    private const string DirectDownloadUrl =
        "https://github.com/joafine-sudo/frostapp/releases/download/minecraft/FrostClient-Setup.exe";

    private static readonly string UpdateStateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FrostClient", "update-state.json");

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FrostClient-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    // What CheckForUpdateAsync found on GitHub, handed back to the UI so it can
    // ask the person before anything is downloaded.
    public sealed class UpdateCheckResult
    {
        public bool UpdateAvailable { get; init; }
        public string? AssetUpdatedAt { get; init; }
        public long? AssetSize { get; init; }
        public string? CurrentExePath { get; init; }
        // Set when the check itself could not run (no network, not running as
        // the installed exe, GitHub unreachable, ...) so the UI can show why.
        public string? Error { get; init; }
    }

    private static UpdateCheckResult Fail(string error) =>
        new() { UpdateAvailable = false, Error = error };

    /// <summary>
    /// Looks up the latest "minecraft" release on GitHub and reports whether it differs
    /// from what is currently installed. Never downloads or changes anything on disk,
    /// and never throws: network problems come back as <see cref="UpdateCheckResult.Error"/>.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            return await CheckCoreAsync();
        }
        catch (TaskCanceledException) { return Fail("GitHub did not answer in time."); }
        catch (HttpRequestException ex) { return Fail("Could not reach GitHub: " + ex.Message); }
        catch (JsonException) { return Fail("GitHub sent an unexpected answer."); }
        catch (Exception ex) { return Fail("Update check failed: " + ex.Message); }
    }

    private static async Task<UpdateCheckResult> CheckCoreAsync()
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            return Fail("Could not locate the running executable.");

        // Running from Visual Studio/dotnet instead of the installed launcher.
        var exeName = Path.GetFileName(currentExe);
        if (Array.FindIndex(InstalledExeNames,
                n => string.Equals(n, exeName, StringComparison.OrdinalIgnoreCase)) < 0)
            return Fail("Updates only run from the installed FrostClient.exe.");

        using var response = await Http.GetAsync(ReleaseApi, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            return Fail($"GitHub returned {(int)response.StatusCode}.");

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var release = await JsonDocument.ParseAsync(stream);
        var root = release.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            return Fail("The latest release is still a draft.");
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True)
            return Fail("The latest release is a pre-release.");

        var tag = root.TryGetProperty("tag_name", out var tagNode) && tagNode.ValueKind == JsonValueKind.String
            ? tagNode.GetString() : null;
        if (!string.Equals(tag, ReleaseTag, StringComparison.OrdinalIgnoreCase))
            return Fail("No matching release was found.");

        string? assetUpdatedAt = null;
        long? remoteSize = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameNode) ||
                    !string.Equals(nameNode.GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (asset.TryGetProperty("updated_at", out var updatedNode) &&
                    updatedNode.ValueKind == JsonValueKind.String)
                    assetUpdatedAt = updatedNode.GetString();
                if (asset.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var size))
                    remoteSize = size;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(assetUpdatedAt))
            return Fail($"Release has no '{AssetName}' asset.");

        var state = await ReadUpdateStateAsync();
        var currentSize = new FileInfo(currentExe).Length;

        // Once this exact GitHub asset has been installed, no download is needed.
        var upToDate = string.Equals(state.AssetUpdatedAt, assetUpdatedAt, StringComparison.Ordinal) &&
                       (!remoteSize.HasValue || currentSize == remoteSize.Value);

        return new UpdateCheckResult
        {
            UpdateAvailable = !upToDate,
            AssetUpdatedAt = assetUpdatedAt,
            AssetSize = remoteSize,
            CurrentExePath = currentExe
        };
    }

    /// <summary>
    /// Downloads the asset CheckForUpdateAsync found, replaces the running executable
    /// with it and relaunches. Only call this after the person chose to download.
    /// </summary>
    public static Task DownloadAndApplyUpdateAsync(UpdateCheckResult info)
    {
        if (!info.UpdateAvailable || info.CurrentExePath == null || info.AssetUpdatedAt == null)
            throw new InvalidOperationException("No update is pending; call CheckForUpdateAsync first.");

        return DownloadAndReplaceAsync(info.CurrentExePath, DirectDownloadUrl, new UpdateState
        {
            AssetUpdatedAt = info.AssetUpdatedAt,
            AssetSize = info.AssetSize
        });
    }

    private sealed class UpdateState
    {
        public string AssetUpdatedAt { get; set; } = "";
        public long? AssetSize { get; set; }
    }

    private static async Task<UpdateState> ReadUpdateStateAsync()
    {
        try
        {
            if (!File.Exists(UpdateStateFile)) return new UpdateState();
            var json = await File.ReadAllTextAsync(UpdateStateFile);
            return JsonSerializer.Deserialize<UpdateState>(json) ?? new UpdateState();
        }
        catch { return new UpdateState(); }
    }

    private static async Task DownloadAndReplaceAsync(string currentExe, string downloadUrl, UpdateState newState)
    {
        var id = Guid.NewGuid().ToString("N");
        var tempExe = Path.Combine(Path.GetTempPath(), $"FrostClient-update-{id}.exe");
        var stateTmp = Path.Combine(Path.GetTempPath(), $"FrostClient-update-state-{id}.json");

        try
        {
            // The client's default Accept header is for the GitHub API; the binary
            // download needs its own.
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("application/octet-stream");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(
                tempExe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, useAsync: true))
            {
                await input.CopyToAsync(output);
                await output.FlushAsync();
            }

            // A tiny response is almost certainly an error page, not the launcher.
            if (new FileInfo(tempExe).Length < 100_000)
                throw new InvalidDataException("The downloaded launcher is unexpectedly small.");

            // The state file is only moved into place by the script AFTER the exe was
            // really replaced. Before, it was written first, so a failed copy left the
            // launcher believing it was up to date.
            Directory.CreateDirectory(Path.GetDirectoryName(UpdateStateFile)!);
            await File.WriteAllTextAsync(stateTmp,
                JsonSerializer.Serialize(newState, new JsonSerializerOptions { WriteIndented = true }));

            var script = Path.Combine(Path.GetTempPath(), $"FrostClient-update-{id}.cmd");
            var pid = Environment.ProcessId;

            var scriptText = $"""
@echo off
setlocal
set "PID={pid}"
set "TEMP_EXE={EscapeForCmd(tempExe)}"
set "TARGET_EXE={EscapeForCmd(currentExe)}"
set "STATE_TMP={EscapeForCmd(stateTmp)}"
set "STATE_FILE={EscapeForCmd(UpdateStateFile)}"
set /a WAITS=0

:WAIT_FOR_LAUNCHER
tasklist /FI "PID eq %PID%" 2>NUL | find "%PID%" >NUL
if errorlevel 1 goto COPY_START
set /a WAITS+=1
if %WAITS% GEQ 60 goto COPY_START
timeout /t 1 /nobreak >NUL
goto WAIT_FOR_LAUNCHER

:COPY_START
set /a TRIES=0
:COPY
copy /Y "%TEMP_EXE%" "%TARGET_EXE%" >NUL 2>&1
if not errorlevel 1 goto COPIED
set /a TRIES+=1
if %TRIES% GEQ 15 goto FAILED
timeout /t 1 /nobreak >NUL
goto COPY

:COPIED
move /Y "%STATE_TMP%" "%STATE_FILE%" >NUL 2>&1
del /Q "%TEMP_EXE%" >NUL 2>&1
start "" "%TARGET_EXE%"
goto CLEANUP

:FAILED
del /Q "%TEMP_EXE%" >NUL 2>&1
del /Q "%STATE_TMP%" >NUL 2>&1

:CLEANUP
del /Q "%~f0" >NUL 2>&1
""";

            await File.WriteAllTextAsync(script, scriptText);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"\" /b \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            // Close through WPF so MainWindow.OnClosed runs (Discord presence, settings).
            // Environment.Exit is only a fallback in case a stray thread keeps the process alive.
            _ = Task.Run(async () => { await Task.Delay(4000); Environment.Exit(0); });

            var app = System.Windows.Application.Current;
            if (app != null) app.Dispatcher.Invoke(app.Shutdown);
            else Environment.Exit(0);
        }
        catch
        {
            try { if (File.Exists(tempExe)) File.Delete(tempExe); } catch { }
            try { if (File.Exists(stateTmp)) File.Delete(stateTmp); } catch { }
            throw;
        }
    }

    private static string EscapeForCmd(string value) =>
        value.Replace("%", "%%").Replace("\"", "\"\"");
}