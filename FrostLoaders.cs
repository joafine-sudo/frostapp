using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CmlLib.Core;

namespace FrostClient;

public enum LoaderKind { Vanilla, Fabric, Quilt, Forge, NeoForge }

// Instala mod loaders por cima de uma versão vanilla.
//  • Fabric / Quilt: o meta-API deles devolve um version.json pronto (com inheritsFrom);
//    escrevemos em versions/<id>/<id>.json e o CmlLib trata do resto no InstallAsync.
//  • Forge / NeoForge: precisam de processors (patching), por isso usamos os pacotes
//    API de Forge e NeoForge fornecida pelo pacote CmlLib.Core.Installer.Forge.
internal static class FrostLoaders
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FrostClient-Launcher/1.0");
        return c;
    }

    /// <summary>Instala o loader e devolve o id da versão criada (nome da pasta em versions/).</summary>
    public static Task<string> InstallAsync(LoaderKind kind, string mcVersion, MinecraftLauncher launcher, string gameDir)
        => kind switch
        {
            LoaderKind.Fabric   => InstallFabricLikeAsync("https://meta.fabricmc.net/v2/versions/loader", mcVersion, gameDir, requireStableFlag: true),
            LoaderKind.Quilt    => InstallFabricLikeAsync("https://meta.quiltmc.org/v3/versions/loader",  mcVersion, gameDir, requireStableFlag: false),
            LoaderKind.Forge    => InstallForgeAsync(mcVersion, launcher),
            LoaderKind.NeoForge => InstallNeoForgeAsync(mcVersion, launcher),
            _                   => Task.FromResult(mcVersion)
        };

    private static async Task<string> InstallFabricLikeAsync(string baseUrl, string mc, string gameDir, bool requireStableFlag)
    {
        // 1) escolher o loader mais recente e estável para esta versão de MC
        var listJson = await Http.GetStringAsync($"{baseUrl}/{Uri.EscapeDataString(mc)}");
        using var list = JsonDocument.Parse(listJson);
        if (list.RootElement.ValueKind != JsonValueKind.Array || list.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException($"Este loader não suporta Minecraft {mc}.");

        string? loaderVersion = null;
        foreach (var item in list.RootElement.EnumerateArray())
        {
            var loader = item.GetProperty("loader");
            var ver = loader.GetProperty("version").GetString();
            if (string.IsNullOrEmpty(ver)) continue;

            bool stable = requireStableFlag
                ? loader.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True
                : !ver.Contains('-');          // Quilt não tem flag: ignora -beta/-rc
            if (stable) { loaderVersion = ver; break; }
        }
        loaderVersion ??= list.RootElement[0].GetProperty("loader").GetProperty("version").GetString();
        if (loaderVersion == null) throw new InvalidOperationException("Não encontrei nenhuma versão do loader.");

        // 2) descarregar o version.json já montado
        var profileUrl = $"{baseUrl}/{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
        var profileJson = await Http.GetStringAsync(profileUrl);
        using var profile = JsonDocument.Parse(profileJson);
        var id = profile.RootElement.GetProperty("id").GetString()
                 ?? throw new InvalidDataException("O profile do loader não tem 'id'.");

        // proteção: o id vira nome de pasta
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains(".."))
            throw new InvalidDataException("Id de versão inválido.");

        var dir = Path.Combine(gameDir, "versions", id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, id + ".json"), profileJson);
        return id;
    }

    // ── Forge / NeoForge ─────────────────────────────────────
    // ATENÇÃO: usam os pacotes NuGet; confirma os nomes exatos no README de cada um
    // (a API mudou entre versões). Em geral é só isto:
    private static async Task<string> InstallForgeAsync(string mc, MinecraftLauncher launcher)
    {
        var forge = new CmlLib.Core.Installer.Forge.ForgeInstaller(launcher);
        return await forge.Install(
            mc,
            new CmlLib.Core.Installer.Forge.ForgeInstallOptions());
    }

    private static async Task<string> InstallNeoForgeAsync(string mc, MinecraftLauncher launcher)
    {
        var neo = new CmlLib.Core.Installer.NeoForge.NeoForgeInstaller(launcher);
        return await neo.Install(
            mc,
            new CmlLib.Core.Installer.Forge.ForgeInstallOptions());
    }
}
