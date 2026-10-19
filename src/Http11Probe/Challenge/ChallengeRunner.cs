using System.Text;
using System.Text.RegularExpressions;
using Http11Probe.Client;
using Http11Probe.Response;

namespace Http11Probe.Challenge;

public sealed class ChallengeRunner
{
    private readonly ProbeOptions _options;

    public ChallengeRunner(ProbeOptions options) => _options = options;

    /// <summary>Runs all endpoints in order. Bad steps come first, the good step last,
    /// and the first failure aborts that endpoint's remaining steps. Every endpoint is
    /// still attempted.</summary>
    public async Task<ChallengeRunReport> RunAsync(
        IReadOnlyList<ChallengeEndpoint> endpoints,
        Action<ChallengeEndpoint>? onEndpointStart = null,
        Action<StepOutcome>? onStep = null)
    {
        var outcomes = new List<EndpointOutcome>();
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var endpoint in endpoints)
        {
            onEndpointStart?.Invoke(endpoint);
            outcomes.Add(await RunEndpointAsync(endpoint, onStep));
        }

        totalSw.Stop();
        return new ChallengeRunReport { Endpoints = outcomes, TotalDuration = totalSw.Elapsed };
    }

    private async Task<EndpointOutcome> RunEndpointAsync(ChallengeEndpoint endpoint, Action<StepOutcome>? onStep)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcomes = new List<StepOutcome>();

        foreach (var step in endpoint.Steps)
        {
            var outcome = await RunStepAsync(step);
            outcome.RawRequest = outcome.RawRequest ?? step.RequestFactory(_options);
            outcomes.Add(outcome);
            onStep?.Invoke(outcome);

            if (!outcome.Pass)
            {
                // Fail-fast: this endpoint stops here; later endpoints still run.
                sw.Stop();
                return new EndpointOutcome
                {
                    Endpoint = endpoint,
                    StepOutcomes = outcomes,
                    Passed = false,
                    FailureStepId = step.Id,
                    FailureReason = outcome.Reason,
                    Duration = sw.Elapsed
                };
            }
        }

        sw.Stop();
        return new EndpointOutcome
        {
            Endpoint = endpoint,
            StepOutcomes = outcomes,
            Passed = true,
            Duration = sw.Elapsed
        };
    }

    private async Task<StepOutcome> RunStepAsync(ProbeStep step)
    {
        var request = Encoding.UTF8.GetBytes(step.RequestFactory(_options).Replace("{h}", _options.HostHeader));
        var (response, ending, error) = await SendAsync(request);
        if (error is not null)
            return new StepOutcome { Step = step, Pass = false, Reason = error, ConnectionEnding = ending };
        var verdict = step.Expected.Evaluate(response, ending);
        return new StepOutcome
        {
            Step = step,
            Pass = verdict.Pass,
            Reason = verdict.Reason,
            Response = response,
            ConnectionEnding = ending
        };
    }

    private async Task<(HttpResponse? Response, ConnectionEnding Ending, string? Error)> SendAsync(byte[] request)
    {
        await using var client = new RawTcpClient(_options.ConnectTimeout, _options.ReadTimeout);
        var connectState = await client.ConnectAsync(_options.Host, _options.Port);
        if (connectState != ConnectionState.Open)
            return (null, ConnectionEnding.ClosedByServer, $"failed to connect to {_options.Host}:{_options.Port}: {connectState}");
        try
        {
            await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            return (null, ConnectionEnding.ClosedByServer, $"send failed: {ex.Message}");
        }
        var (data, length, readState, _) = await client.ReadResponseAsync();
        var response = ResponseParser.TryParse(data.AsSpan(), length);

        var ending = readState != ConnectionState.Open
            ? ConnectionEnding.ClosedByServer
            : await EndpointEndingAsync(client);

        return (response, ending, null);
    }

    private static async Task<ConnectionEnding> EndpointEndingAsync(RawTcpClient client)
    {
        var state = client.CheckConnectionState();
        if (state != ConnectionState.Open)
            return ConnectionEnding.ClosedByServer;
        state = await client.WaitForCloseAsync(6, 50);
        return state == ConnectionState.Open ? ConnectionEnding.Open : ConnectionEnding.ClosedByServer;
    }
}
