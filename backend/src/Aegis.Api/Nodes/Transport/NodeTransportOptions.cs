namespace Aegis.Api.Nodes.Transport;
public sealed class NodeTransportOptions
{
    public int HeartbeatSeconds { get; set; } = 25;
    public int TimeoutSeconds { get; set; } = 75;
    public int HelloTimeoutSeconds { get; set; } = 10;
    public int WatchdogSeconds { get; set; } = 5;
    public int PersistSeconds { get; set; } = 120;
    public int MinimumHeartbeatSeconds { get; set; } = 5;
    public int MaxConnections { get; set; } = 256;
    public const int MaxMessageBytes = 4096;
    public bool IsValid() => HeartbeatSeconds >= 1 && TimeoutSeconds >= HeartbeatSeconds * 2 &&
        HelloTimeoutSeconds >= 1 && WatchdogSeconds >= 1 && PersistSeconds >= HeartbeatSeconds &&
        MinimumHeartbeatSeconds >= 1 && MinimumHeartbeatSeconds <= HeartbeatSeconds && MaxConnections is >= 1 and <= 4096;
}
