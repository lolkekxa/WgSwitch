using System.Net;
using WgSwitch.Services;

namespace WgSwitch.Tests;

public class PhoneConfigTests
{
    private const string Pc = """
        [Interface]
        PrivateKey = PC-PRIVATE-KEY=
        Address = 10.8.0.2/32
        DNS = 1.1.1.1
        ListenPort = 51111

        [Peer]
        PublicKey = SERVER-KEY=
        PresharedKey = PSK=
        AllowedIPs = 10.8.0.0/24
        Endpoint = vpn.example.com:51820
        PersistentKeepalive = 25
        """;

    private static ConfigFactory.PhoneResult Build()
    {
        var (iface, peers, prefix, psk) = ConfigFactory.PhoneParts(Pc.Split('\n'));
        return ConfigFactory.BuildPhone("home", "10.8.0.1", IPAddress.Parse("10.8.0.3"), "PHONE-PRIVATE=", "PHONE-PUBLIC=", iface, peers, prefix, psk);
    }

    [Fact]
    public void Pc_key_does_not_leak()
    {
        var r = Build();
        Assert.DoesNotContain("PC-PRIVATE-KEY", r.Config);
        Assert.DoesNotContain("ListenPort", r.Config);
        Assert.Contains("PrivateKey = PHONE-PRIVATE=", r.Config);
    }

    [Fact]
    public void Peer_and_dns_are_copied()
    {
        var r = Build();
        Assert.StartsWith("# WgSwitch name=home ping=10.8.0.1\n[Interface]\n", r.Config);
        Assert.Contains("Address = 10.8.0.3/32", r.Config);
        Assert.Contains("DNS = 1.1.1.1", r.Config);
        Assert.Contains("\n\n[Peer]\nPublicKey = SERVER-KEY=\nPresharedKey = PSK=\nAllowedIPs = 10.8.0.0/24\nEndpoint = vpn.example.com:51820", r.Config);
    }

    [Fact]
    public void Server_block_has_phone_key_and_psk()
    {
        var r = Build();
        Assert.Equal("[Peer]\n# home · телефон\nPublicKey = PHONE-PUBLIC=\nPresharedKey = PSK=\nAllowedIPs = 10.8.0.3/32", r.PeerBlock);
    }

    [Fact]
    public void Suggests_next_address()
    {
        using var dir = new TempDir();
        Assert.Equal("10.8.0.3", ConfigFactory.SuggestPhoneIp(dir.File("pc.conf", Pc)));
    }
}
