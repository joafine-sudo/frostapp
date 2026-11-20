using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace FrostClient;

public sealed class ModHit
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string DownloadsText { get; init; } = "";
}

public sealed record InstalledMod(string FileName);

// Cliente mínimo da API do Modrinth (gratuita, sem chave): https://docs.modrinth.com/api/
// O Modrinth exige um User-Agent que identifique a app.
internal static class Modrinth
{
    private const string Api = "https://api.modrinth.com/v2";
    private const string ManifestName = ".frost-mods.json";   // projectId -> ficheiro instalado

    // Keep this client independent from FrostUpdater: accessing CurrentVersion here
    // could trigger another type initializer and prevent Modrinth from loading at all.
    private static readonly HttpClient Http = Create();
    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FrostClient/1.0.0");
        return c;
    }

    // Quilt carrega mods Fabric; os outros loaders só aceitam o seu próprio tipo.
    private static string[] LoadersFor(string loader) =>
        loader == "quilt" ? new[] { "quilt", "fabric" } : new[] { loader };

    public static async Task<List<ModHit>> SearchAsync(string query, string mc, string loader)
    {
        var facets = new List<string[]>
        {
            new[] { "project_type:mod" },
            new[] { "versions:" + mc },
            LoadersFor(loader).Select(l => "categories:" + l).ToArray()
        };
        var url = $"{Api}/search?query={Uri.EscapeDataString(query)}&limit=25" +
                  $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";

        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var list = new List<ModHit>();
        foreach (var h in doc.RootElement.GetProperty("hits").EnumerateArray())
        {
            long dl = h.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var n) ? n : 0;
            list.Add(new ModHit
            {
                ProjectId = h.GetProperty("project_id").GetString() ?? "",
                Title = h.GetProperty("title").GetString() ?? "",
                Description = h.GetProperty("description").GetString() ?? "",
                DownloadsText = $"{dl:N0} downloads"
            });
        }
        return list;
    }

    private sealed record Resolved(string FileName, string Url, string? Sha512, List<string> Deps);

    private static async Task<Resolved?> ResolveAsync(string projectId, string mc, string loader)
    {
        var url = $"{Api}/project/{Uri.EscapeDataString(projectId)}/version" +
                  $"?loaders={Uri.EscapeDataString(JsonSerializer.Serialize(LoadersFor(loader)))}" +
                  $"&game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mc }))}";

        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var versions = doc.RootElement.EnumerateArray().ToList();     // mais recentes primeiro
        if (versions.Count == 0) return null;

        var v = versions.FirstOrDefault(x => x.GetProperty("version_type").GetString() == "release");
        if (v.ValueKind == JsonValueKind.Undefined) v = versions[0];

        var files = v.GetProperty("files").EnumerateArray().ToList();
        if (files.Count == 0) return null;

        var f = files.FirstOrDefault(x =>
            x.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True);
        if (f.ValueKind == JsonValueKind.Undefined) f = files[0];

        string? sha = f.TryGetProperty("hashes", out var hs) && hs.TryGetProperty("sha512", out var s) ? s.GetString() : null;

        var deps = new List<string>();
        if (v.TryGetProperty("dependencies", out var ds))
            foreach (var dep in ds.EnumerateArray())
                if (dep.GetProperty("dependency_type").GetString() == "required" &&
                    dep.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.String)
                    deps.Add(pid.GetString()!);

        return new Resolved(f.GetProperty("filename").GetString() ?? "mod.jar",
                            f.GetProperty("url").GetString() ?? "", sha, deps);
    }

    /// <summary>Instala o mod e as dependências obrigatórias. Devolve os ficheiros novos.</summary>
    public static async Task<List<string>> InstallAsync(string projectId, string mc, string loader,
        string modsDir, Action<string> status, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();
        var installed = new List<string>();
        if (!visited.Add(projectId) || visited.Count > 40) return installed;

        Directory.CreateDirectory(modsDir);
        var manifest = LoadManifest(modsDir);

        if (manifest.TryGetValue(projectId, out var existing) && File.Exists(Path.Combine(modsDir, existing)))
            return installed;                                          // já instalado

        status("Resolving " + projectId + "...");
        var r = await ResolveAsync(projectId, mc, loader)
                ?? throw new InvalidOperationException($"Sem versão compatível com {loader} {mc}.");

        // dependências primeiro
        foreach (var dep in r.Deps)
            installed.AddRange(await InstallAsync(dep, mc, loader, modsDir, status, visited));

        var name = Path.GetFileName(r.FileName);                      // nunca confiar no nome vindo da rede
        if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            !r.Url.StartsWith("https://cdn.modrinth.com/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ficheiro inesperado vindo do Modrinth.");

        status("Downloading " + name + "...");
        var target = Path.Combine(modsDir, name);
        var tmp = target + ".part";
        try
        {
            using (var resp = await Http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var input = await resp.Content.ReadAsStreamAsync();
                await using var output = File.Create(tmp);
                await input.CopyToAsync(output);
            }

            if (r.Sha512 != null)
            {
                await using var fs = File.OpenRead(tmp);
                var hash = Convert.ToHexString(await SHA512.HashDataAsync(fs));
                if (!hash.Equals(r.Sha512, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Hash inválido: download corrompido.");
            }
            File.Move(tmp, target, overwrite: true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }

        // A dependency may have updated the manifest while this install was in progress.
        // Reload it so saving the parent does not erase dependency entries.
        manifest = LoadManifest(modsDir);
        manifest[projectId] = name;
        SaveManifest(modsDir, manifest);
        installed.Add(name);
        return installed;
    }

    public static List<InstalledMod> ListInstalled(string modsDir) =>
        Directory.Exists(modsDir)
            ? Directory.EnumerateFiles(modsDir, "*.jar").Select(f => new InstalledMod(Path.GetFileName(f)))
                       .OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<InstalledMod>();

    public static void Remove(string modsDir, string fileName)
    {
        fileName = Path.GetFileName(fileName);
        var path = Path.Combine(modsDir, fileName);
        if (File.Exists(path)) File.Delete(path);

        var manifest = LoadManifest(modsDir);
        foreach (var k in manifest.Where(kv => kv.Value == fileName).Select(kv => kv.Key).ToList())
            manifest.Remove(k);
        SaveManifest(modsDir, manifest);
    }

    private static Dictionary<string, string> LoadManifest(string modsDir)
    {
        try
        {
            var p = Path.Combine(modsDir, ManifestName);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p)) ?? new();
        }
        catch { }
        return new();
    }

    private static void SaveManifest(string modsDir, Dictionary<string, string> m)
    {
        var path = Path.Combine(modsDir, ManifestName);
        var tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(m));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}
