using WgSwitch.Services;

namespace WgSwitch.Tests;

public class ConfigFactoryTests
{
    private const string Key = "3mYqT0B1XkI8o7mPqZbVq9kH2s3Q4r5T6u7V8w9X0yA=";

    private static (ConfigFactory.Request? Req, string? Err) Validate(string endpoint, string key = Key, string ip = "10.8.0.2",
        string name = "home", string[]? names = null, string[]? ips = null) =>
        ConfigFactory.Validate(name, endpoint, key, ip, false, names ?? [], ips ?? []);

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4:51820")]
    [InlineData("vpn.example.com", "vpn.example.com:51820")]
    [InlineData("vpn.example.com:443", "vpn.example.com:443")]
    [InlineData("[2001:db8::1]", "[2001:db8::1]:51820")]
    [InlineData("  1.2.3.4  ", "1.2.3.4:51820")]
    public void Endpoint_gets_default_port(string input, string expected) =>
        Assert.Equal(expected, Validate(input).Req?.Endpoint);

    [Theory]
    [InlineData("")]
    [InlineData("http://1.2.3.4")]
    [InlineData("1.2.3.4:99999")]
    [InlineData("1.2.3.4:abc")]
    [InlineData("a:b:c")]
    public void Bad_endpoint_is_rejected(string input) => Assert.Null(Validate(input).Req);

    [Theory]
    [InlineData("short")]
    [InlineData("3mYqT0B1XkI8o7mPqZbVq9kH2s3Q4r5T6u7V8w9X0yA")]   // без «=»
    [InlineData("!!!!T0B1XkI8o7mPqZbVq9kH2s3Q4r5T6u7V8w9X0yA=")]  // не base64
    public void Bad_server_key_is_rejected(string key) => Assert.Null(Validate("1.2.3.4", key).Req);

    [Theory]
    [InlineData("10.8.0.1")]   // адрес сервера
    [InlineData("10.8.0.0")]
    [InlineData("10.8.0.255")]
    [InlineData("abc")]
    [InlineData("fd00::2")]
    public void Bad_client_ip_is_rejected(string ip) => Assert.Null(Validate("1.2.3.4", ip: ip).Req);

    [Fact]
    public void Taken_name_is_rejected_case_insensitive() =>
        Assert.Null(Validate("1.2.3.4", name: "HOME", names: ["home"]).Req);

    [Fact]
    public void Taken_subnet_is_rejected_with_suggestion()
    {
        var (req, err) = Validate("1.2.3.4", ip: "10.8.0.5", ips: ["10.8.0.1"]);
        Assert.Null(req);
        Assert.Contains("10.9.0.2", err);
    }

    [Theory]
    [InlineData("wg0")]
    [InlineData("my_server.1")]
    [InlineData("a=b+c-d")]
    public void Valid_tunnel_names(string name) => Assert.True(WireGuardService.IsValidName(name));

    [Theory]
    [InlineData("")]
    [InlineData("с кириллицей")]
    [InlineData("with space")]
    [InlineData("123456789012345678901234567890123")] // 33 символа
    public void Invalid_tunnel_names(string name) => Assert.False(WireGuardService.IsValidName(name));

    [Fact]
    public void Suggests_next_free_name() =>
        Assert.Equal("wg2", ConfigFactory.SuggestName(["wg0", "WG1", "home"]));

    [Fact]
    public void Suggests_next_free_subnet() =>
        Assert.Equal("10.10.0.2", ConfigFactory.SuggestClientIp(["10.8.0.1", "10.9.0.1"]));
}
