using Http11Probe.Challenge;

namespace Http11Probe.Cli.Reporting;

public static class ConsoleReporter
{
    public static void PrintEndpointHeader(ChallengeEndpoint endpoint)
    {
        Console.WriteLine();
        Console.WriteLine($"  {endpoint.Name} — {endpoint.Description}");
        Console.WriteLine("  " + new string('─', 95));
        Console.WriteLine("  {0,-32} {1,-10} {2,-24} {3,-6} {4}", "Step", "Verdict", "Expected", "Status", "Details");
    }

    public static void PrintStep(StepOutcome outcome)
    {
        var (color, symbol) = outcome.Pass ? (ConsoleColor.Green, "PASS") : (ConsoleColor.Red, "FAIL");

        var status = outcome.Response is not null
            ? outcome.Response.StatusCode.ToString()
            : outcome.ConnectionEnding.ToString();

        var detail = outcome.Reason;
        if (detail.Length > 40)
            detail = detail[..40] + "…";

        var expected = outcome.Step.Expected.Describe();
        if (expected.Length > 24)
            expected = expected[..24];

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write("  {0,-32} {1,-10}", outcome.Step.Id, symbol);
        Console.ForegroundColor = prev;
        Console.WriteLine(" {0,-24} {1,-6} {2}", expected, status, detail);
    }

    public static void PrintRaw(StepOutcome outcome)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    request ▸ {outcome.RawRequest?.TrimEnd('\r', '\n')}");
        if (outcome.Response is not null)
            foreach (var line in outcome.Response.RawResponse?.Split('\n') ?? [])
                Console.WriteLine($"    response ◂ {line.TrimEnd('\r')}");
        else
            Console.WriteLine("    (no readable HTTP response; connection " + outcome.ConnectionEnding + ")");
        Console.ResetColor();
        Console.WriteLine();
    }

    public static void PrintEndpointTrailer(EndpointOutcome outcome)
    {
        var prev = Console.ForegroundColor;
        var (color, word) = outcome.Passed ? (ConsoleColor.Green, "PASSED") : (ConsoleColor.Red, "ABORTED (fail-fast)");
        Console.ForegroundColor = color;
        Console.Write($"  {outcome.Endpoint.Name}: {word} ");
        Console.ForegroundColor = prev;
        Console.WriteLine($"— {outcome.StepOutcomes.Count}/{outcome.Endpoint.Steps.Count} steps, {outcome.Duration.TotalMilliseconds:0} ms");
        if (outcome.FailureStepId is not null)
            Console.WriteLine($"  {outcome.Endpoint.Name}: first failing step '{outcome.FailureStepId}': {outcome.FailureReason}");
        Console.ResetColor();
    }

    public static void PrintSummary(ChallengeRunReport report)
    {
        Console.WriteLine("  " + new string('─', 80));
        Console.WriteLine();
        var prev = Console.ForegroundColor;
        Console.Write("  Verdict: ");
        Console.ForegroundColor = report.AllPassed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(report.AllPassed ? "PASS — all five endpoints green" : "FAIL — endpoints failing: " +
            string.Join(", ", report.Endpoints.Where(e => !e.Passed).Select(e => e.Endpoint.Name)));
        Console.ForegroundColor = prev;

        var steps = report.Endpoints.SelectMany(e => e.StepOutcomes).ToList();
        Console.WriteLine($"  ({steps.Count(s => s.Pass)}/{steps.Count} steps across {report.Endpoints.Count} endpoints, {report.TotalDuration.TotalSeconds:F1}s)");

        foreach (var (id, why) in ChallengeEndpointSuite.Skipped)
            Console.WriteLine($"  SKIP {id} — {why}");
        Console.WriteLine();
    }
}
