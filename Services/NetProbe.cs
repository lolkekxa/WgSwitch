using System.Net.Sockets;

namespace WgSwitch.Services;

/// <summary>Открыт ли TCP-порт: SSH или панель сервера, через туннель. Только соединение, без данных.</summary>
public static class NetProbe
{
    public static async Task<bool> CanConnectAsync(string host, int port, int timeoutMs = 1500)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }
}
