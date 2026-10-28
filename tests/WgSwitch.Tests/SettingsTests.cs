using System.Text.Json;
using WgSwitch.Services;

namespace WgSwitch.Tests;

public class SettingsTests
{
    [Fact]
    public void Legacy_single_server_is_migrated()
    {
        using var dir = new TempDir();
        var conf = dir.File("wg0.conf", "x");
        dir.File("settings.json", "{ \"ConfigPath\": " + JsonSerializer.Serialize(conf) + ", \"PingHost\": \"10.8.0.1\", \"AutoConnect\": true }");

        var s = new SettingsService(dir.Path);
        s.Load();

        var server = Assert.Single(s.Current.Servers);
        Assert.Equal("wg0", server.Name);
        Assert.Equal("10.8.0.1", server.PingHost);
        Assert.True(server.AutoConnect);       // общий флаг перенесён на сервер
        Assert.Null(s.Current.ConfigPath);     // старые поля больше не пишутся
        Assert.False(s.Current.AutoConnect);
    }

    [Fact]
    public void New_settings_have_safe_defaults()
    {
        using var dir = new TempDir();
        var s = new SettingsService(dir.Path);
        s.Load();

        Assert.Empty(s.Current.Servers);
        Assert.True(s.Current.Notify);
        Assert.True(s.Current.AutoReconnect);
        Assert.False(s.Current.DisconnectOnExit);
        Assert.False(s.Current.ShowDetails);   // на главной по умолчанию только пинг и время в сети
        Assert.True(s.Current.CheckUpdates);
    }

    [Fact]
    public async Task Save_and_load_round_trip()
    {
        using var dir = new TempDir();
        var s = new SettingsService(dir.Path);
        var next = s.Current.Clone();
        next.Servers.Add(new ServerEntry { ConfigPath = dir.File("work.conf", "x"), PingHost = "10.9.0.1", AutoConnect = true });
        next.Selected = "work";
        next.DisconnectOnExit = true;
        await s.SaveAsync(next);

        var again = new SettingsService(dir.Path);
        again.Load();
        Assert.Equal("work", again.SelectedName);
        Assert.True(again.Find("WORK")!.AutoConnect);
        Assert.True(again.Current.DisconnectOnExit);
    }

    [Fact]
    public void Broken_file_falls_back_to_defaults()
    {
        using var dir = new TempDir();
        dir.File("settings.json", "{ not json");
        var s = new SettingsService(dir.Path);
        s.Load();
        Assert.Empty(s.Current.Servers);
    }
}
