using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WgSwitch.Services;

public sealed class ServerEntry
{
    /// <summary>Только путь. Содержимое конфига приложение не читает.</summary>
    public string ConfigPath { get; set; } = "";
    public string PingHost { get; set; } = "10.8.0.1";

    /// <summary>Имя туннеля = имя conf-файла без расширения (так его называет и сам WireGuard).</summary>
    [JsonIgnore] public string Name => SettingsService.TunnelNameOf(ConfigPath);

    public ServerEntry Clone() => (ServerEntry)MemberwiseClone();
}

public sealed class AppSettings
{
    public List<ServerEntry> Servers { get; set; } = [];
    public string? Selected { get; set; }
    public bool AutoStart { get; set; }
    public bool StartMinimized { get; set; }
    public bool AutoConnect { get; set; }

    // старый формат с одним сервером: читается при загрузке, переносится в Servers и больше не пишется
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ConfigPath { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PingHost { get; set; }

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.Servers = Servers.Select(s => s.Clone()).ToList();
        return c;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJson : JsonSerializerContext;

public sealed class SettingsService
{
    public const string AppName = "WgSwitch";
    private const string TaskName = AppName;

    /// <summary>Сюда мастер кладёт созданные конфиги: папка доступна только администраторам и SYSTEM.</summary>
    public static readonly string ConfigFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);

    private readonly string _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
    private string FilePath => Path.Combine(_dir, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public static string TunnelNameOf(string? configPath)
    {
        var name = Path.GetFileNameWithoutExtension(configPath?.Trim().Trim('"') ?? "");
        return string.IsNullOrEmpty(name) ? "wg0" : name;
    }

    public ServerEntry? Find(string? name) =>
        Current.Servers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Выбранный в окне сервер; если выбор устарел — первый из списка.</summary>
    public ServerEntry? SelectedServer => Find(Current.Selected) ?? Current.Servers.FirstOrDefault();

    public string SelectedName => SelectedServer?.Name ?? "wg0";

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Current = new(); // битый файл — работаем на умолчаниях, перезапишется при сохранении
        }

        if (Current.Servers.Count == 0 && !string.IsNullOrWhiteSpace(Current.ConfigPath) && File.Exists(Current.ConfigPath.Trim().Trim('"')))
            Current.Servers.Add(new ServerEntry { ConfigPath = Current.ConfigPath.Trim().Trim('"'), PingHost = Current.PingHost ?? "10.8.0.1" });
        Current.ConfigPath = null;
        Current.PingHost = null;
    }

    public async Task SaveAsync(AppSettings next)
    {
        bool autostartChanged = next.AutoStart != Current.AutoStart;
        Directory.CreateDirectory(_dir);
        var tmp = FilePath + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(next, SettingsJson.Default.AppSettings)).ConfigureAwait(false);
        File.Move(tmp, FilePath, overwrite: true);
        Current = next;

        if (autostartChanged)
            await SetAutoStartAsync(next.AutoStart).ConfigureAwait(false);
    }

    // ---------- автозапуск ----------
    //
    // Run-ключ реестра Windows не запускает программы с requireAdministrator (UAC их тихо блокирует при входе),
    // поэтому автозапуск — задача планировщика «при входе пользователя» с наивысшими правами: без окна UAC.

    private static async Task SetAutoStartAsync(bool enable)
    {
        if (!enable)
        {
            await Proc.RunAsync("schtasks.exe", $"/delete /tn \"{TaskName}\" /f").ConfigureAwait(false);
            return;
        }

        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>{AppName}: пульт WireGuard</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--autostart</Arguments></Exec>
              </Actions>
            </Task>
            """;

        var tmp = Path.Combine(Path.GetTempPath(), $"{AppName}-task.xml");
        await File.WriteAllTextAsync(tmp, xml, Encoding.Unicode).ConfigureAwait(false);
        try
        {
            var r = await Proc.RunAsync("schtasks.exe", $"/create /tn \"{TaskName}\" /xml \"{tmp}\" /f").ConfigureAwait(false);
            if (r.ExitCode != 0)
                throw new InvalidOperationException("Не удалось включить автозапуск: " + FirstLine(r.Error, r.Output));
        }
        finally { File.Delete(tmp); }
    }

    private static string FirstLine(params string[] texts) =>
        texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Split('\n')[0].Trim() ?? "неизвестная ошибка";
}
