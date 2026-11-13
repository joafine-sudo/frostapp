using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace FrostClient;

internal static class FrostUpdater
{
    private const string AssetName = "FrostClient.exe";
    private const string ReleaseTag = "minecraft";
    private const string ReleaseApi =
        "https://api.github.com/repos/joafine-sudo/frostapp/releases/tags/minecraft";
    private const string DirectDownloadUrl =
        "https://github.com/joafine-sudo/frostapp/releases/download/minecraft/FrostClient.exe";
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

    public static async Task CheckAndUpdateAsync()
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            return;

        // Do not replace the executable while running from Visual Studio/dotnet.
        if (!string.Equals(Path.GetFileName(currentExe), AssetName, StringComparison.OrdinalIgnoreCase))
            return;

        using var response = await Http.GetAsync(ReleaseApi, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            return;

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var release = await JsonDocument.ParseAsync(stream);
        var root = release.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            return;
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
            return;

        var tag = root.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() : null;
        if (!string.Equals(tag, ReleaseTag, StringComparison.OrdinalIgnoreCase))
            return;

        string? assetUpdatedAt = null;
        long? remoteSize = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameNode) ||
                    !string.Equals(nameNode.GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (asset.TryGetProperty("updated_at", out var updatedNode))
                    assetUpdatedAt = updatedNode.GetString();
                if (asset.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var size))
                    remoteSize = size;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(assetUpdatedAt))
            return;

        var state = await ReadUpdateStateAsync();
        var currentSize = new FileInfo(currentExe).Length;

        // Once this exact GitHub asset has been installed, no download is needed.
        if (string.Equals(state.AssetUpdatedAt, assetUpdatedAt, StringComparison.Ordinal) &&
            (!remoteSize.HasValue || currentSize == remoteSize.Value))
            return;

        await DownloadAndReplaceAsync(currentExe, DirectDownloadUrl, new UpdateState
        {
            AssetUpdatedAt = assetUpdatedAt,
            AssetSize = remoteSize
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

    private static async Task WriteUpdateStateAsync(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UpdateStateFile)!);
            await File.WriteAllTextAsync(
                UpdateStateFile,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static async Task DownloadAndReplaceAsync(string currentExe, string downloadUrl, UpdateState newState)
    {
        var tempExe = Path.Combine(
            Path.GetTempPath(),
            $"FrostClient-update-{Guid.NewGuid():N}.exe");

        try
        {
            using var response = await Http.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(
                tempExe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                useAsync: true))
            {
                await input.CopyToAsync(output);
                await output.FlushAsync();
            }

            // A tiny response is almost certainly an error page, not the launcher.
            if (new FileInfo(tempExe).Length < 100_000)
                throw new InvalidDataException("The downloaded launcher is unexpectedly small.");

            // Only mark the asset as installed after the download itself succeeded.
            await WriteUpdateStateAsync(newState);

            var script = Path.Combine(
                Path.GetTempPath(),
                $"FrostClient-update-{Guid.NewGuid():N}.cmd");

            var pid = Environment.ProcessId;
            var scriptText = $"""
@echo off
setlocal
set "PID={pid}"
set "TEMP_EXE={EscapeForCmd(tempExe)}"
set "TARGET_EXE={EscapeForCmd(currentExe)}"

:WAIT_FOR_LAUNCHER
tasklist /FI "PID eq %PID%" 2>NUL | find "%PID%" >NUL
if not errorlevel 1 (
    timeout /t 1 /nobreak >NUL
    goto WAIT_FOR_LAUNCHER
)

timeout /t 1 /nobreak >NUL
copy /Y "%TEMP_EXE%" "%TARGET_EXE%" >NUL
if errorlevel 1 (
    del /Q "%TEMP_EXE%" >NUL 2>&1
    del /Q "%~f0" >NUL 2>&1
    exit /b 1
)

del /Q "%TEMP_EXE%" >NUL 2>&1
start "" "%TARGET_EXE%"
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

            // The updater waits for this process to exit before replacing the EXE.
            await Task.Delay(250);
            Environment.Exit(0);
        }
        catch
        {
            try { if (File.Exists(tempExe)) File.Delete(tempExe); } catch { }
            throw;
        }
    }

    private static string EscapeForCmd(string value) =>
        value.Replace("%", "%%").Replace("\"", "\"\"");
}
