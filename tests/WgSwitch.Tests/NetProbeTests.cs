using System.Net;
using System.Net.Sockets;
using WgSwitch.Services;

namespace WgSwitch.Tests;

public class NetProbeTests
{
    [Fact]
    public async Task Open_port_is_reachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.True(await NetProbe.CanConnectAsync("127.0.0.1", port));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Closed_port_is_not()
    {
        // занять порт и сразу освободить: на нём гарантированно никто не слушает
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.False(await NetProbe.CanConnectAsync("127.0.0.1", port, 1000));
    }

    [Fact]
    public async Task Bad_host_is_not() =>
        Assert.False(await NetProbe.CanConnectAsync("", 22, 500));
}
