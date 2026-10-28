using WgSwitch.Services;

namespace WgSwitch.Tests;

public class FullTunnelTests
{
    private const string Wizard =
        "[Interface]\nPrivateKey = FAKE=\nAddress = 10.9.0.2/32\n\n[Peer]\nPublicKey = SRV=\nAllowedIPs = 10.9.0.0/24\nEndpoint = 1.2.3.4:51820\nPersistentKeepalive = 25\n";

    private static string Norm(string s) => s.Replace("\r", "");

    [Fact]
    public void On_then_off_returns_identical_file()
    {
        using var dir = new TempDir();
        var p = dir.File("wg.conf", Wizard);

        Assert.False(ConfigFactory.IsFullTunnel(p));
        ConfigFactory.SetFullTunnel(p, true);
        Assert.True(ConfigFactory.IsFullTunnel(p));
        var on = Norm(File.ReadAllText(p));
        Assert.Contains("AllowedIPs = 0.0.0.0/0, ::/0", on);
        Assert.Contains("DNS = 1.1.1.1, 1.0.0.1", on);

        ConfigFactory.SetFullTunnel(p, false);
        Assert.Equal(Wizard, Norm(File.ReadAllText(p)));
    }

    [Fact]
    public void Own_dns_is_kept()
    {
        using var dir = new TempDir();
        var conf = "[Interface]\nPrivateKey = FAKE=\nAddress = 10.8.0.5/24, fd00::5/64\nDNS = 10.8.0.1\n\n[Peer]\nPublicKey = SRV=\nAllowedIPs = 10.8.0.0/24\n";
        var p = dir.File("wg.conf", conf);

        ConfigFactory.SetFullTunnel(p, true);
        Assert.DoesNotContain("1.1.1.1", File.ReadAllText(p));
        ConfigFactory.SetFullTunnel(p, false);
        Assert.Equal(conf, Norm(File.ReadAllText(p)));
    }

    [Fact]
    public void Turning_off_without_address_fails_and_keeps_file()
    {
        using var dir = new TempDir();
        var conf = "[Interface]\nPrivateKey = FAKE=\n\n[Peer]\nPublicKey = SRV=\nAllowedIPs = 0.0.0.0/0\n";
        var p = dir.File("wg.conf", conf);

        Assert.Throws<InvalidOperationException>(() => ConfigFactory.SetFullTunnel(p, false));
        Assert.Equal(conf, Norm(File.ReadAllText(p)));
    }

    [Fact]
    public void Missing_file_is_unknown() =>
        Assert.Null(ConfigFactory.IsFullTunnel(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".conf")));
}
