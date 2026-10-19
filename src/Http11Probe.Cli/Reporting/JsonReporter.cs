using System.Text.Json;
using System.Text.Json.Serialization;
using Http11Probe.Challenge;

namespace Http11Probe.Cli.Reporting;

public static class JsonReporter
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Generate(ChallengeRunReport report)
    {
        var output = new
        {
            summary = new
            {
                profile = report.Profile,
                allPassed = report.AllPassed,
                endpoints = report.Endpoints.Count,
                steps = report.Endpoints.Sum(e => e.StepOutcomes.Count),
                passed = report.Endpoints.SelectMany(e => e.StepOutcomes).Count(s => s.Pass),
                durationMs = report.TotalDuration.TotalMilliseconds,
                skipped = ChallengeEndpointSuite.Skipped.Select(s => new { s.Id, s.Why })
            },
            endpoints = report.Endpoints.Select(e => new
            {
                e.Endpoint.Name,
                e.Endpoint.Description,
                passed = e.Passed,
                failureStepId = e.FailureStepId,
                failureReason = e.FailureReason,
                durationMs = e.Duration.TotalMilliseconds,
                steps = e.StepOutcomes.Select(s => new
                {
                    id = s.Step.Id,
                    description = s.Step.Description,
                    verdict = s.Pass ? "pass" : "fail",
                    expected = s.Step.Expected.Describe(),
                    reason = s.Reason,
                    connectionState = s.ConnectionEnding.ToString(),
                    statusCode = s.Response?.StatusCode,
                    body = s.Response?.Body,
                    rawRequest = s.RawRequest
                })
            })
        };

        return JsonSerializer.Serialize(output, s_options);
    }
}
