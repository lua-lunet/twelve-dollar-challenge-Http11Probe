namespace Http11Probe.Challenge;

/// <summary>Judgement lens for the transport fuzz steps. <c>generic</c> holds every
/// server to the original probe's RFC allow-lists; <c>subset</c> scores the lunet
/// transport subset's exact answers (hard 501 for TE: chunked, hard 400 for
/// HTTP/1.0 and pipelining).</summary>
public static class ProbeProfile
{
    public const string Generic = "generic";
    public const string Subset = "subset";
}

public sealed class ProbeOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Token { get; init; }
    public required long KnownPostId { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public string Profile { get; init; } = ProbeProfile.Generic;

    public bool IsSubset => Profile == ProbeProfile.Subset;

    public string HostHeader => Port == 80 ? Host : $"{Host}:{Port}";
    public string Bearer => $"Bearer {Token}";
}
