namespace WgSwitch.Models;

public enum TunnelState
{
    Off,
    Connecting,
    Connected,
    NoResponse,
}

/// <summary>Снимок состояния туннеля для окна и трея.</summary>
public sealed record TunnelStatus(
    TunnelState State,
    string TunnelName,
    long? PingMs = null,
    DateTime? ConnectedSince = null,
    DateTime? LastHandshake = null,
    long? RxBytes = null,
    long? TxBytes = null,
    string? Error = null,
    bool WireGuardMissing = false)
{
    public static TunnelStatus Initial(string name) => new(TunnelState.Off, name);
}
