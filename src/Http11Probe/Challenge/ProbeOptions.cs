namespace Http11Probe.Challenge;

public sealed class ProbeOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Token { get; init; }
    public required long KnownPostId { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public string HostHeader => Port == 80 ? Host : $"{Host}:{Port}";
    public string Bearer => $"Bearer {Token}";
}
