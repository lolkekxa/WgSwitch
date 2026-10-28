using WgSwitch.Services;

namespace WgSwitch.Tests;

public class BackupTests
{
    /// <summary>Два сервера с фейковыми конфигами, настройки и копия — всё во временной папке.</summary>
    private static async Task<(TempDir Dir, string File)> Exported(string password)
    {
        var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        var next = settings.Current.Clone();
        next.Servers.Add(new ServerEntry { ConfigPath = dir.File("home.conf", "[Interface]\nPrivateKey = KEY1=\n"), PingHost = "10.8.0.1" });
        next.Servers.Add(new ServerEntry { ConfigPath = dir.File("work.conf", "[Interface]\nPrivateKey = KEY2=\n"), PingHost = "10.9.0.1" });
        next.Selected = "work";
        await settings.SaveAsync(next);

        var file = Path.Combine(dir.Path, "backup" + BackupService.Extension);
        await BackupService.ExportAsync(settings, file, password);
        return (dir, file);
    }

    [Fact]
    public async Task Round_trip_restores_everything()
    {
        var (dir, file) = await Exported("correct horse");
        using (dir)
        {
            var back = await BackupService.ReadAsync(file, "correct horse");
            Assert.Equal("work", back.Selected);
            Assert.Equal(["home", "work"], back.Servers.Select(s => s.Name));
            Assert.Equal("10.9.0.1", back.Servers[1].PingHost);
            Assert.Contains("PrivateKey = KEY2=", back.Servers[1].Config);
        }
    }

    [Fact]
    public async Task File_has_no_plaintext()
    {
        var (dir, file) = await Exported("correct horse");
        using (dir)
        {
            var raw = File.ReadAllText(file);
            Assert.DoesNotContain("KEY1", raw);
            Assert.DoesNotContain("home", raw);
            Assert.DoesNotContain("10.8.0.1", raw);
        }
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var (dir, file) = await Exported("correct horse");
        using (dir)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BackupService.ReadAsync(file, "wrong"));
            Assert.Contains("Неверный пароль", ex.Message);
        }
    }

    [Fact]
    public async Task Tampered_file_is_rejected()
    {
        var (dir, file) = await Exported("correct horse");
        using (dir)
        {
            var bytes = File.ReadAllBytes(file);
            bytes[^1] ^= 1;
            File.WriteAllBytes(file, bytes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => BackupService.ReadAsync(file, "correct horse"));
        }
    }

    [Fact]
    public async Task Foreign_file_is_rejected()
    {
        using var dir = new TempDir();
        var junk = dir.File("junk.wgsb", "hello");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BackupService.ReadAsync(junk, "x"));
        Assert.Contains("не резервная копия", ex.Message);
    }
}
