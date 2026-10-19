using System.Text.Json;
using System.Text.RegularExpressions;
using Http11Probe.Response;

namespace Http11Probe.Challenge;

/// <summary>How a step's response is judged. A bad request answered with a normal
/// success response by the server is a FAIL, never a pass.</summary>
public abstract class ProbeExpectation
{
    public abstract ProbeVerdict Evaluate(HttpResponse? response, ConnectionEnding ending);
    public abstract string Describe();
}

public enum ConnectionEnding
{
    Open,
    ClosedByServer
}

public sealed class ProbeVerdict
{
    public required bool Pass { get; init; }
    public required string Reason { get; init; }

    public static ProbeVerdict Ok() => new() { Pass = true, Reason = "matched" };
    public static ProbeVerdict Fail(string reason) => new() { Pass = false, Reason = reason };
}

public sealed class ExactStatusBodyExpectation : ProbeExpectation
{
    public required int Status { get; init; }
    public required string Body { get; init; }
    public bool RequireJsonContentType { get; init; }

    public override ProbeVerdict Evaluate(HttpResponse? response, ConnectionEnding ending)
    {
        if (response is null)
            return ProbeVerdict.Fail("no complete HTTP response (connection closed without one)");
        if (response.StatusCode != Status)
            return ProbeVerdict.Fail($"wanted {Status} {Body}, got {response.StatusCode} {response.Body?.TrimEnd('\r', '\n')}");
        var body = response.Body?.TrimEnd('\r', '\n') ?? "";
        if (body != Body)
            return ProbeVerdict.Fail($"wanted {Status} {Body}, got {response.StatusCode} {body}");
        if (RequireJsonContentType)
        {
            if (!response.Headers.TryGetValue("Content-Type", out var ct))
                return ProbeVerdict.Fail($"missing Content-Type, body was {body}");
            if (!ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                return ProbeVerdict.Fail($"Content-Type '{ct}' is not application/json");
        }

        return ProbeVerdict.Ok();
    }

    public override string Describe() => $"{Status} {Body}" + (RequireJsonContentType ? " + application/json" : "");
}

public sealed class StatusBodyShapeExpectation : ProbeExpectation
{
    public required int Status { get; init; }
    public required Func<string, IReadOnlyDictionary<string, string>, string?> Validator { get; init; }
    public required string ShapeDescription { get; init; }

    public override ProbeVerdict Evaluate(HttpResponse? response, ConnectionEnding ending)
    {
        if (response is null)
            return ProbeVerdict.Fail("no complete HTTP response (connection closed without one)");
        if (response.StatusCode != Status)
            return ProbeVerdict.Fail($"wanted {Status} with {ShapeDescription}, got {response.StatusCode} {response.Body?.TrimEnd('\r', '\n')}");
        var issue = Validator(response.Body ?? "", response.Headers);
        return issue is null ? ProbeVerdict.Ok() : ProbeVerdict.Fail($"wanted {Status} with {ShapeDescription}, got {response.StatusCode} {response.Body?.TrimEnd('\r', '\n')} ({issue})");
    }

    public override string Describe() => $"{Status} with {ShapeDescription}";
}

public sealed class StatusOnlyExpectation : ProbeExpectation
{
    public required int Status { get; init; }

    public override ProbeVerdict Evaluate(HttpResponse? response, ConnectionEnding ending)
    {
        if (response is null)
            return ProbeVerdict.Fail("no complete HTTP response (connection closed without one)");
        if (response.StatusCode != Status)
            return ProbeVerdict.Fail($"wanted {Status}, got {response.StatusCode} {response.Body?.TrimEnd('\r', '\n')}");
        return ProbeVerdict.Ok();
    }

    public override string Describe() => Status.ToString();
}

/// <summary>Parse-error request: the server must close the connection. A 400 sent
/// before closing is acceptable; anything that keeps serving is not.</summary>
public sealed class ConnectionClosedExpectation : ProbeExpectation
{
    public override ProbeVerdict Evaluate(HttpResponse? response, ConnectionEnding ending)
    {
        if (ending == ConnectionEnding.ClosedByServer)
        {
            if (response is null || response.StatusCode >= 400)
                return ProbeVerdict.Ok();
            return ProbeVerdict.Fail($"connection stayed healthy: {response.StatusCode} {response.Body?.TrimEnd('\r', '\n')}");
        }
        return ProbeVerdict.Fail(response is null
            ? "no response and connection stayed open"
            : $"connection stayed open after {response.StatusCode} {response.Body?.TrimEnd('\r', '\n')}");
    }

    public override string Describe() => "connection closed (parse error)";
}

/// <summary>Pipelined request pair: the first response must be a hard 400 and the
/// connection must end closed.</summary>
public sealed class PipelineHard400Expectation : ProbeExpectation
{
    public override ProbeVerdict Evaluate(HttpResponse? response, ConnectionEnding ending)
    {
        if (response is null)
            return ProbeVerdict.Fail($"no response at all (connection {ending})");
        if (response.StatusCode != 400)
            return ProbeVerdict.Fail($"wanted a hard 400 to the pipelined pair, got {response.StatusCode} {response.Body?.TrimEnd('\r', '\n')}");
        if (ending != ConnectionEnding.ClosedByServer)
            return ProbeVerdict.Fail("responded 400 but kept serving the connection");
        return ProbeVerdict.Ok();
    }

    public override string Describe() => "hard 400, connection closed";
}

public sealed class RegexStatusExpectation : ProbeExpectation
{
    public required int Status { get; init; }
    public required string BodyPattern { get; init; }
    public required string ShapeDescription { get; init; }

    public override ProbeVerdict Evaluate(HttpResponse? response, ConnectionEnding ending)
    {
        if (response is null)
            return ProbeVerdict.Fail("no complete HTTP response (connection closed without one)");
        if (response.StatusCode != Status)
            return ProbeVerdict.Fail($"wanted {Status} matching {ShapeDescription}, got {response.StatusCode} {response.Body?.TrimEnd('\r', '\n')}");
        var body = response.Body?.TrimEnd('\r', '\n') ?? "";
        if (!Regex.IsMatch(body, BodyPattern))
            return ProbeVerdict.Fail($"wanted {Status} matching {ShapeDescription}, got {response.StatusCode} {body}");
        return ProbeVerdict.Ok();
    }

    public override string Describe() => $"{Status} matching {ShapeDescription}";
}

/// <summary>POST /posts success: exact status-and-shape with the trimmed body echoed back.</summary>
public sealed class CreateExpectation : ProbeExpectation
{
    public required string WantedBody { get; init; }

    public override ProbeVerdict Evaluate(HttpResponse? response, ConnectionEnding ending)
    {
        if (response is null)
            return ProbeVerdict.Fail("no complete HTTP response (connection closed without one)");
        if (response.StatusCode != 201)
            return ProbeVerdict.Fail($"wanted 201 with {{\"post\":<post>}}, got {response.StatusCode} {response.Body?.TrimEnd('\r', '\n')}");
        var body = response.Body?.TrimEnd('\r', '\n') ?? "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var props = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.EnumerateObject().ToArray() : [];
            if (props.Length != 1 || props[0].Name != "post")
                return ProbeVerdict.Fail($"wanted 201 {{\"post\":<post>}}, got keys [{string.Join(",", props.Select(p => p.Name))}]");
            var issue = Shape.IsPost(props[0].Value.GetRawText());
            if (issue is not null)
                return ProbeVerdict.Fail($"wanted 201 {{\"post\":<post>}}, got {body} ({issue})");
            var post = props[0].Value;
            var postBody = post.GetProperty("body").GetString() ?? "";
            if (postBody != WantedBody)
                return ProbeVerdict.Fail($"wanted 201 with body '{WantedBody}' (trimmed), got body '{postBody}'");
            if ((post.GetProperty("like_count").GetRawText()) != "0")
                return ProbeVerdict.Fail($"wanted like_count 0 on a fresh post, got {post.GetProperty("like_count").GetRawText()}");
            return ProbeVerdict.Ok();
        }
        catch (JsonException ex)
        {
            return ProbeVerdict.Fail($"wanted 201 {{\"post\":<post>}}, got {body} ({ex.Message})");
        }
    }

    public override string Describe() => $"201 with {{\"post\":{{...body '{WantedBody}' (trimmed), like_count 0, created_at .mmmZ}}}}";
}

/// <summary>POST /posts/:id/like success: exact body (key order matters).</summary>
public sealed class LikeExpectation : ProbeExpectation
{
    public required long KnownPostId { get; init; }
    public required int Status { get; init; }
    public required bool AlreadyLiked { get; init; }

    public override ProbeVerdict Evaluate(HttpResponse? response, ConnectionEnding ending)
    {
        if (response is null)
            return ProbeVerdict.Fail("no complete HTTP response (connection closed without one)");
        var wanted = $"{{\"liked\":true,\"already_liked\":{(AlreadyLiked ? "true" : "false")},\"post_id\":{KnownPostId}}}";
        var body = response.Body?.TrimEnd('\r', '\n') ?? "";
        var described = $"wanted {Status} {wanted}, got {response.StatusCode} {body}";
        if (response.StatusCode != Status || body != wanted)
            return ProbeVerdict.Fail(described);
        return ProbeVerdict.Ok();
    }

    public override string Describe() => $"{Status} {{\"liked\":true,\"already_liked\":{(AlreadyLiked ? "true" : "false")},\"post_id\":{KnownPostId}}}";
}

public sealed class ProbeStep
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required Func<ProbeOptions, string> RequestFactory { get; init; }
    public required ProbeExpectation Expected { get; init; }
}

public sealed class ChallengeEndpoint
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<ProbeStep> Steps { get; init; }
    public (string Id, string Why)[] SkippedReasons { get; init; } = [];
}

public sealed class StepOutcome
{
    public required ProbeStep Step { get; init; }
    public required bool Pass { get; init; }
    public required string Reason { get; init; }
    public HttpResponse? Response { get; init; }
    public string? RawRequest { get; set; }
    public ConnectionEnding ConnectionEnding { get; init; }
}

public sealed class EndpointOutcome
{
    public required ChallengeEndpoint Endpoint { get; init; }
    public required List<StepOutcome> StepOutcomes { get; init; }
    public bool Passed { get; init; }
    public string? FailureStepId { get; init; }
    public string? FailureReason { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed class ChallengeRunReport
{
    public required List<EndpointOutcome> Endpoints { get; init; }
    public required TimeSpan TotalDuration { get; init; }

    public bool AllPassed => Endpoints.All(e => e.Passed);
}
