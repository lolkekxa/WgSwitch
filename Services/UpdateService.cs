using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace WgSwitch.Services;

/// <param name="SetupUrl">Установщик из релиза; есть только у настоящих релизов, у «dev» имя без версии.</param>
/// <param name="SumsUrl">SHA256SUMS.txt — по нему сверяем скачанный установщик.</param>
public sealed record UpdateInfo(string Version, string PageUrl, string? SetupUrl, string? SetupName, string? SumsUrl);

/// <summary>
/// Обновления из GitHub Releases: последний релиз через открытый API (без токена), сравнение версий,
/// скачивание установщика со сверкой SHA-256. Установленная копия обновляется установщиком поверх,
/// portable и lite — открывают страницу релиза.
/// </summary>
public static class UpdateService
{
    private const string Api = "https://api.github.com/repos/lolkekxa/WgSwitch/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Найденное обновление; null — нет или ещё не проверяли.</summary>
    public static UpdateInfo? Available { get; private set; }

    /// <summary>Вызывается в потоке, который вызвал CheckAsync (у нас — UI).</summary>
    public static event Action<UpdateInfo?>? AvailableChanged;

    /// <summary>Стоит ли из папки установщика: тогда обновляемся сами, иначе — ссылка на релиз.</summary>
    public static bool IsInstalled
    {
        get
        {
            var exe = Environment.ProcessPath;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WgSwitch");
            return exe is not null && exe.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WgSwitch/{SettingsService.Version}"); // без User-Agent GitHub API отвечает 403
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Проверить последний релиз. Возвращает обновление или null, если стоит последняя версия.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(Api, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub ответил {(int)response.StatusCode}");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        var page = root.GetProperty("html_url").GetString() ?? "https://github.com/lolkekxa/WgSwitch/releases/latest";

        string? setupUrl = null, setupName = null, sumsUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name.StartsWith("WgSwitch-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                (setupUrl, setupName) = (url, name);
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                sumsUrl = url;
        }

        var info = IsNewer(version, SettingsService.Version) ? new UpdateInfo(version, page, setupUrl, setupName, sumsUrl) : null;
        Available = info;
        AvailableChanged?.Invoke(info);
        return info;
    }

    /// <summary>
    /// 1.0.2 новее 1.0.1. Хвост после «-» не учитывается: сборка 1.0.1-dev.N выходит уже после релиза 1.0.1,
    /// поэтому релиз той же версии для неё не обновление.
    /// </summary>
    public static bool IsNewer(string latest, string current) =>
        TryParse(latest) is { } l && TryParse(current) is { } c && l > c;

    private static Version? TryParse(string v) =>
        Version.TryParse(v.Split('-', '+')[0], out var parsed) ? parsed : null;

    /// <summary>Скачать установщик во временную папку и сверить SHA-256 с SHA256SUMS.txt из того же релиза.</summary>
    public static async Task<string> DownloadSetupAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (update.SetupUrl is null || update.SetupName is null)
            throw new InvalidOperationException("В релизе нет установщика");

        var path = Path.Combine(Path.GetTempPath(), update.SetupName);
        using (var response = await Http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }

        if (update.SumsUrl is not null)
        {
            var sums = await Http.GetStringAsync(update.SumsUrl, ct);
            var expected = sums.Split('\n')
                .Select(l => l.Trim().TrimStart('﻿').Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(p => p.Length == 2 && p[1].Equals(update.SetupName, StringComparison.OrdinalIgnoreCase))?[0];
            await using var check = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
            if (expected is null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                check.Close();
                File.Delete(path);
                throw new InvalidOperationException("Контрольная сумма не совпала — файл не запущен");
            }
        }
        return path;
    }

    /// <summary>
    /// Запустить установщик после выхода программы: он не ставится, пока она держит свой mutex.
    /// Пауза через ping — без окон и без PowerShell. Установщик сам запустит новую версию.
    /// </summary>
    public static void RunInstallerAfterExit(string setupPath) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & \"{setupPath}\" /SILENT /SUPPRESSMSGBOXES /NORESTART")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
}
