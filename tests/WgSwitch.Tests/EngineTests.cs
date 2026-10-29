using WgSwitch.Services;

namespace WgSwitch.Tests;

public class EngineTests
{
    private const string Plain = """
        [Interface]
        PrivateKey = KEY=
        Address = 10.8.0.2/32

        [Peer]
        PublicKey = SERVER=
        AllowedIPs = 10.8.0.0/24
        Endpoint = vpn.example.com:51820
        """;

    private const string Amnezia = """
        [Interface]
        PrivateKey = KEY=
        Address = 10.19.0.2/32
        Jc = 4
        Jmin = 40
        Jmax = 70
        S1 = 52
        S2 = 91
        H1 = 1834712907
        H2 = 520983412
        H3 = 1209483711
        H4 = 873420119

        [Peer]
        PublicKey = SERVER=
        AllowedIPs = 10.19.0.0/24
        Endpoint = vpn.example.com:51821
        """;

    [Fact]
    public void Detects_engine_by_config()
    {
        using var dir = new TempDir();
        Assert.Same(WireGuardService.WireGuard, WireGuardService.EngineFor(dir.File("plain.conf", Plain)));
        Assert.Same(WireGuardService.Amnezia, WireGuardService.EngineFor(dir.File("awg.conf", Amnezia)));
        Assert.Same(WireGuardService.WireGuard, WireGuardService.EngineFor(null));
        Assert.Same(WireGuardService.WireGuard, WireGuardService.EngineFor(@"C:\nope\missing.conf"));
    }

    [Fact]
    public void Peer_section_keys_do_not_count()
    {
        // H1 в [Peer] — не параметр маскировки интерфейса
        Assert.False(ConfigFactory.IsAmneziaText("[Interface]\nPrivateKey = K=\n[Peer]\nH1 = 5\n".Split('\n')));
    }

    [Fact]
    public void Engines_have_their_own_tools_and_services()
    {
        Assert.Equal("WireGuardTunnel$", WireGuardService.WireGuard.ServicePrefix);
        Assert.Equal("AmneziaWGTunnel$", WireGuardService.Amnezia.ServicePrefix);
        Assert.Equal(("amneziawg.exe", "awg.exe"), (WireGuardService.Amnezia.Gui, WireGuardService.Amnezia.Cli));
    }

    [Fact]
    public void Phone_config_keeps_obfuscation()
    {
        var (iface, peers, prefix, psk) = ConfigFactory.PhoneParts(Amnezia.Split('\n'));
        var r = ConfigFactory.BuildPhone("lab", "10.19.0.1", System.Net.IPAddress.Parse("10.19.0.3"), "P=", "PUB=", iface, peers, prefix, psk);
        foreach (var k in new[] { "Jc = 4", "S1 = 52", "H4 = 873420119" }) Assert.Contains(k, r.Config);
        Assert.True(ConfigFactory.IsAmneziaText(r.Config.Split('\n')));
    }
}
