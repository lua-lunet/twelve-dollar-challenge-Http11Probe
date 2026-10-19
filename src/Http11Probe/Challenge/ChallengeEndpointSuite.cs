using System.Text;
using System.Text.Json;

namespace Http11Probe.Challenge;

/// <summary>Five-endpoint fuzzer for the twelve-dollar challenge contract (SPEC.md +
/// test/test.sh). The probe is db-blind: it explores only with one --token and one
/// --known-post-id, and assumes a fresh seed database (the like steps assert
/// first-then-repeat behaviour on that id).</summary>
public static class ChallengeEndpointSuite
{
    /// <summary>Official cases needing JWT_SECRET to re-sign tokens — the probe only
    /// carries one valid token, so these are uncovered: SKIP, never invent rules.</summary>
    public static readonly (string Id, string Why)[] Skipped =
    [
        ("posts.expired-token", "needs JWT_SECRET to sign an expired token"),
        ("posts.sub-not-integer", "needs JWT_SECRET to re-sign a token with sub=abc"),
        ("posts.no-username", "needs JWT_SECRET to re-sign a token without username")
    ];

    public static IReadOnlyList<ChallengeEndpoint> GetEndpoints(ProbeOptions o) =>
    [
        Health(o),
        Feed(o),
        PostsRead(o),
        PostsCreate(o),
        PostsLike(o)
    ];

    // ── request builders ─────────────────────────────────────────────────────────

    private static string Req(string method, string target, string? auth, string? body, string version = "1.1")
    {
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(target).Append(" HTTP/").Append(version).Append("\r\nHost: {h}\r\n");
        if (auth is not null) sb.Append("Authorization: ").Append(auth).Append("\r\n");
        if (body is not null)
            sb.Append("Content-Type: application/json\r\nContent-Length: ")
              .Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n");
        sb.Append("\r\n");
        if (body is not null) sb.Append(body);
        return sb.ToString();
    }

    // Raw UTF-8 in and out: the challenge server round-trips literal \uXXXX escapes as error
    // bytes, so bodies must carry the real codepoints (like test/test.sh does).
    private static readonly JsonSerializerOptions s_rawUtf8 = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string JsonBody(object obj) => JsonSerializer.Serialize(obj, s_rawUtf8);

    private static string TeChunked(string method, string target, string auth) =>
        $"{method} {target} HTTP/1.1\r\nHost: {{h}}\r\nAuthorization: {auth}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";

    // Valid single-chunk request (generic profile): one chunk carrying a complete
    // JSON body, then the zero terminator. A recipient MUST decode this (RFC 9112 §7.1).
    private static string ChunkedCreate(string auth)
    {
        var body = JsonBody(new { body = "chunky" });
        var hex = $"{Encoding.UTF8.GetByteCount(body):X2}";
        return $"POST /posts HTTP/1.1\r\nHost: {{h}}\r\nAuthorization: {auth}\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n{hex}\r\n{body}\r\n0\r\n\r\n";
    }

    // A bare non-HTTP line with a CRLF terminator: not a request line at all.
    private static string ParseError => "GARBAGE\r\n\r\n";

    private static string Pipelined(ProbeOptions o, string target) =>
        $"GET {target} HTTP/1.1\r\nHost: {o.HostHeader}\r\n\r\nGET {target} HTTP/1.1\r\nHost: {o.HostHeader}\r\n\r\n";

    // ── reachable-path fuzz steps (same four transport cases, retargeted) ──────
    // generic profile: the original probe's RFC allow-lists — a valid chunked body
    // MUST be served (2xx), a plain HTTP/1.0 request MUST be served (2xx) and closes,
    // a pipelined GET pair is answered 2xx + 2xx, a parse error closes.
    // subset profile: the lunet transport subset's exact answers.

    private static ProbeStep FuzzTe(ProbeOptions o, string id, string method, string target, bool withAuth) => o.IsSubset
        ? new ProbeStep
        {
            Id = id,
            Description = $"{method} {target} with Transfer-Encoding: chunked is rejected (TE never served)",
            RequestFactory = opts => TeChunked(method, target, opts.Bearer),
            Expected = new StatusOnlyExpectation { Status = 501 }
        }
        : new ProbeStep
        {
            Id = id,
            Description = "valid single-chunk body on POST /posts is served (RFC 9112 §7.1: chunked MUST be decoded)",
            RequestFactory = opts => ChunkedCreate(opts.Bearer),
            Expected = new AcceptedExpectation()
        };

    private static ProbeStep FuzzHttp10(ProbeOptions o, string id, string method, string target, bool withAuth) => o.IsSubset
        ? new ProbeStep
        {
            Id = id,
            Description = $"{method} {target} spoken over HTTP/1.0 is rejected",
            RequestFactory = opts => Req(method, target, withAuth ? opts.Bearer : null, null, version: "1.0"),
            Expected = new StatusOnlyExpectation { Status = 400 }
        }
        : new ProbeStep
        {
            Id = id,
            Description = method == "POST"
                ? "POST /posts with a body over plain HTTP/1.0 is served and the connection closes"
                : $"{method} {target} over plain HTTP/1.0 is served and the connection closes",
            RequestFactory = opts => method == "POST"
                ? Req("POST", "/posts", opts.Bearer, JsonBody(new { body = "http10" }), version: "1.0")
                : Req(method, target, withAuth ? opts.Bearer : null, null, version: "1.0"),
            Expected = new Http10CloseExpectation()
        };

    private static ProbeStep FuzzParseError(string id) => new()
    {
        Id = id,
        Description = "malformed request line closes the connection",
        RequestFactory = _ => ParseError,
        Expected = new ConnectionClosedExpectation()
    };

    private static ProbeStep FuzzPipeline(ProbeOptions o, string id, string subsetTarget, string genericTarget) => new()
    {
        Id = id,
        Description = o.IsSubset
            ? $"pipelined GET pair on {subsetTarget} gets a hard 400, connection closed"
            : $"pipelined GET pair on {genericTarget} is answered 2xx + 2xx (RFC 9112 §9.3)",
        RequestFactory = opts => Pipelined(opts, o.IsSubset ? subsetTarget : genericTarget),
        Expected = o.IsSubset ? new PipelineHard400Expectation() : new PipelinedPairExpectation()
    };

    private static ProbeStep IdValidation(string id, string method, string target, string action) => new()
    {
        Id = id,
        Description = $"{method} {target} -> {action}",
        RequestFactory = opts => Req(method, target, null, null),
        Expected = new ExactStatusBodyExpectation { Status = 400, Body = "{\"error\":\"invalid post id\"}" }
    };

    // ── endpoints ────────────────────────────────────────────────────────────────

    private static ChallengeEndpoint Health(ProbeOptions o) => new()
    {
        Name = "health",
        Description = "GET /health",
        Steps =
        [
            FuzzTe(o, "health.te-chunked", "GET", "/feed", withAuth: true),
            FuzzHttp10(o, "health.http10", "GET", "/health", withAuth: true),
            FuzzParseError("health.parse-error"),
            FuzzPipeline(o, "health.pipeline", "/health", "/health"),
            new ProbeStep
            {
                Id = "health.ok",
                Description = "GET /health is healthy (exact body shape, uptime_s integer)",
                RequestFactory = opts => Req("GET", "/health", null, null),
                Expected = new RegexStatusExpectation
                {
                    Status = 200,
                    BodyPattern = "^\\{\"status\":\"ok\",\"db\":\"ok\",\"uptime_s\":[0-9]+\\}$",
                    ShapeDescription = "{\"status\":\"ok\",\"db\":\"ok\",\"uptime_s\":<int>}"
                }
            }
        ]
    };

    private static ChallengeEndpoint Feed(ProbeOptions o) => new()
    {
        Name = "feed",
        Description = "GET /feed (db-blind: shape and count, not golden bytes)",
        Steps =
        [
            FuzzTe(o, "feed.te-chunked", "GET", "/feed", withAuth: true),
            FuzzHttp10(o, "feed.http10", "GET", "/feed", withAuth: true),
            FuzzParseError("feed.parse-error"),
            FuzzPipeline(o, "feed.pipeline", "/feed", "/feed"),
            new ProbeStep
            {
                Id = "feed.ok",
                Description = "GET /feed returns 20 posts, application/json, post key order",
                RequestFactory = opts => Req("GET", "/feed", null, null),
                Expected = new StatusBodyShapeExpectation
                {
                    Status = 200,
                    ShapeDescription = "{\"posts\":[<post> × 20]}, application/json",
                    Validator = (body, headers) =>
                        headers.TryGetValue("Content-Type", out var ct) && ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                            ? Shape.IsFeed(body)
                            : $"Content-Type '{ct}' is not application/json"
                }
            }
        ]
    };

    private static ChallengeEndpoint PostsRead(ProbeOptions o) => new()
    {
        Name = "posts-read",
        Description = $"GET /posts/:id (id validation against known id {o.KnownPostId})",
        Steps =
        [
            IdValidation("posts-read.invalid-id-0", "GET", "/posts/0", "invalid id 0"),
            IdValidation("posts-read.invalid-id--1", "GET", "/posts/-1", "invalid id -1"),
            IdValidation("posts-read.invalid-id-abc", "GET", "/posts/abc", "invalid id abc"),
            IdValidation("posts-read.invalid-id-1.5", "GET", "/posts/1.5", "invalid id 1.5"),
            new ProbeStep
            {
                Id = "posts-read.unknown-post",
                Description = "GET /posts/999999999 -> 404 post not found",
                RequestFactory = opts => Req("GET", "/posts/999999999", opts.Bearer, null),
                Expected = new ExactStatusBodyExpectation { Status = 404, Body = "{\"error\":\"post not found\"}" }
            },
            FuzzTe(o, "posts-read.te-chunked", "GET", $"/posts/{o.KnownPostId}", withAuth: true),
            FuzzHttp10(o, "posts-read.http10", "GET", $"/posts/{o.KnownPostId}", withAuth: false),
            FuzzParseError("posts-read.parse-error"),
            FuzzPipeline(o, "posts-read.pipeline", $"/posts/{o.KnownPostId}", $"/posts/{o.KnownPostId}"),
            new ProbeStep
            {
                Id = "posts-read.ok",
                Description = $"GET /posts/{o.KnownPostId} returns the post object (key order, created_at .mmmZ)",
                RequestFactory = opts => Req("GET", $"/posts/{o.KnownPostId}", null, null),
                Expected = new StatusBodyShapeExpectation
                {
                    Status = 200,
                    ShapeDescription = "{\"post\":{id,body,created_at,author,like_count}}, created_at .mmmZ",
                    Validator = (body, _) =>
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                                return "root is not a JSON object";
                            var props = doc.RootElement.EnumerateObject().ToArray();
                            if (props.Length != 1 || props[0].Name != "post")
                                return $"top-level keys are [{string.Join(",", props.Select(p => p.Name))}], wanted [post]";
                            return Shape.IsPost(props[0].Value.GetRawText());
                        }
                        catch (JsonException ex)
                        {
                            return $"body is not JSON ({ex.Message})";
                        }
                    }
                }
            }
        ]
    };

    private static ChallengeEndpoint PostsCreate(ProbeOptions o) => new()
    {
        Name = "posts-create",
        Description = "POST /posts (auth cases first, then body validation, then the good create)",
        SkippedReasons = Skipped
            .Where(s => s.Id.StartsWith("posts."))
            .Select(s => s)
            .ToArray(),
        Steps =
        [
            new ProbeStep
            {
                Id = "posts.no-token",
                Description = "POST /posts without a token -> 401 missing bearer token",
                RequestFactory = opts => Req("POST", "/posts", null, JsonBody(new { body = "x" })),
                Expected = new ExactStatusBodyExpectation { Status = 401, Body = "{\"error\":\"missing bearer token\"}" }
            },
            new ProbeStep
            {
                Id = "posts.basic-auth",
                Description = "Authorization: Basic abc -> 401 missing bearer token",
                RequestFactory = opts => Req("POST", "/posts", "Basic abc", JsonBody(new { body = "x" })),
                Expected = new ExactStatusBodyExpectation { Status = 401, Body = "{\"error\":\"missing bearer token\"}" }
            },
            new ProbeStep
            {
                Id = "posts.garbage-token",
                Description = "Bearer abc -> 401 invalid or expired token",
                RequestFactory = opts => Req("POST", "/posts", "Bearer abc", JsonBody(new { body = "x" })),
                Expected = new ExactStatusBodyExpectation { Status = 401, Body = "{\"error\":\"invalid or expired token\"}" }
            },
            new ProbeStep
            {
                Id = "posts.bad-signature",
                Description = "tampered last signature byte -> 401 invalid or expired token",
                RequestFactory = opts => Req("POST", "/posts", "Bearer " + TamperSignature(opts.Token), JsonBody(new { body = "x" })),
                Expected = new ExactStatusBodyExpectation { Status = 401, Body = "{\"error\":\"invalid or expired token\"}" }
            },
            new ProbeStep
            {
                Id = "posts.alg-none",
                Description = "alg=none header swapped in -> 401 invalid or expired token",
                RequestFactory = opts => Req("POST", "/posts", "Bearer " + AlgNone(opts.Token), JsonBody(new { body = "x" })),
                Expected = new ExactStatusBodyExpectation { Status = 401, Body = "{\"error\":\"invalid or expired token\"}" }
            },
            new ProbeStep
            {
                Id = "posts.body-empty",
                Description = "{\"body\":\"\"} -> 400 body is required",
                RequestFactory = opts => Req("POST", "/posts", opts.Bearer, JsonBody(new { body = "" })),
                Expected = new ExactStatusBodyExpectation { Status = 400, Body = "{\"error\":\"body is required\"}" }
            },
            new ProbeStep
            {
                Id = "posts.body-whitespace",
                Description = "{\"body\":\"   \"} -> 400 body is required",
                RequestFactory = opts => Req("POST", "/posts", opts.Bearer, JsonBody(new { body = "   " })),
                Expected = new ExactStatusBodyExpectation { Status = 400, Body = "{\"error\":\"body is required\"}" }
            },
            new ProbeStep
            {
                Id = "posts.body-numeric",
                Description = "{\"body\":123} -> 400 body is required",
                RequestFactory = opts => Req("POST", "/posts", opts.Bearer, JsonBody(new { body = 123 })),
                Expected = new ExactStatusBodyExpectation { Status = 400, Body = "{\"error\":\"body is required\"}" }
            },
            new ProbeStep
            {
                Id = "posts.body-missing",
                Description = "{} -> 400 body is required",
                RequestFactory = opts => Req("POST", "/posts", opts.Bearer, "{}"),
                Expected = new ExactStatusBodyExpectation { Status = 400, Body = "{\"error\":\"body is required\"}" }
            },
            new ProbeStep
            {
                Id = "posts.malformed-json",
                Description = "'{bad json' -> 400 malformed JSON body",
                RequestFactory = opts => Req("POST", "/posts", opts.Bearer, "{bad json"),
                Expected = new ExactStatusBodyExpectation { Status = 400, Body = "{\"error\":\"malformed JSON body\"}" }
            },
            new ProbeStep
            {
                Id = "posts.body-too-long",
                Description = "501-char body -> 400 body must be at most 500 characters",
                RequestFactory = opts => Req("POST", "/posts", opts.Bearer, JsonBody(new { body = new string('a', 501) })),
                Expected = new ExactStatusBodyExpectation { Status = 400, Body = "{\"error\":\"body must be at most 500 characters\"}" }
            },
            FuzzTe(o, "posts.te-chunked", "POST", "/posts", withAuth: true),
            FuzzHttp10(o, "posts.http10", "POST", "/posts", withAuth: true),
            FuzzParseError("posts.parse-error"),
            FuzzPipeline(o, "posts.pipeline", "/feed", "/feed"),
            new ProbeStep
            {
                Id = "posts.ok",
                Description = "padded body -> 201, trimmed post object (like_count 0, created_at .mmmZ)",
                RequestFactory = opts => Req("POST", "/posts", opts.Bearer, JsonBody(new { body = "  probe post probe ✓  " })),
                Expected = new CreateExpectation { WantedBody = "probe post probe ✓" }
            }
        ]
    };

    private static ChallengeEndpoint PostsLike(ProbeOptions o) => new()
    {
        Name = "posts-like",
        Description = $"POST /posts/:id/like (auth-before-id cases, then first/repeat like on id {o.KnownPostId}; assumes a fresh database)",
        Steps =
        [
            new ProbeStep
            {
                Id = "like.no-token-with-bad-id",
                Description = "POST /posts/abc/like without a token -> 401 (auth checked before id)",
                RequestFactory = _ => Req("POST", "/posts/abc/like", null, null),
                Expected = new ExactStatusBodyExpectation { Status = 401, Body = "{\"error\":\"missing bearer token\"}" }
            },
            new ProbeStep
            {
                Id = "like.basic-auth-with-bad-id",
                Description = "POST /posts/abc/like with Basic auth -> 401 missing bearer token",
                RequestFactory = _ => Req("POST", "/posts/abc/like", "Basic abc", null),
                Expected = new ExactStatusBodyExpectation { Status = 401, Body = "{\"error\":\"missing bearer token\"}" }
            },
            new ProbeStep
            {
                Id = "like.bad-id-with-token",
                Description = "POST /posts/abc/like with a valid token -> 400 invalid post id",
                RequestFactory = opts => Req("POST", "/posts/abc/like", opts.Bearer, null),
                Expected = new ExactStatusBodyExpectation { Status = 400, Body = "{\"error\":\"invalid post id\"}" }
            },
            new ProbeStep
            {
                Id = "like.unknown-post",
                Description = "POST /posts/999999999/like -> 404 post not found",
                RequestFactory = opts => Req("POST", "/posts/999999999/like", opts.Bearer, null),
                Expected = new ExactStatusBodyExpectation { Status = 404, Body = "{\"error\":\"post not found\"}" }
            },
            FuzzTe(o, "like.te-chunked", "POST", $"/posts/{o.KnownPostId}/like", withAuth: true),
            FuzzHttp10(o, "like.http10", "POST", $"/posts/{o.KnownPostId}/like", withAuth: true),
            FuzzParseError("like.parse-error"),
            FuzzPipeline(o, "like.pipeline", $"/posts/{o.KnownPostId}/like", $"/posts/{o.KnownPostId}"),
            new ProbeStep
            {
                Id = "like.ok-first",
                Description = "first like -> 201 {liked:true, already_liked:false, post_id}",
                RequestFactory = opts => Req("POST", $"/posts/{opts.KnownPostId}/like", opts.Bearer, null),
                Expected = new LikeExpectation { KnownPostId = o.KnownPostId, Status = 201, AlreadyLiked = false }
            },
            new ProbeStep
            {
                Id = "like.ok-repeat",
                Description = "repeat like -> 200 {liked:true, already_liked:true, post_id}",
                RequestFactory = opts => Req("POST", $"/posts/{opts.KnownPostId}/like", opts.Bearer, null),
                Expected = new LikeExpectation { KnownPostId = o.KnownPostId, Status = 200, AlreadyLiked = true }
            }
        ]
    };

    // ── token tampering helpers (probe holds no secret) ──────────────────────────

    private static string TamperSignature(string token)
    {
        var parts = token.Split('.');
        return parts.Length >= 3
            ? $"{parts[0]}.{parts[1]}.{parts[2].Substring(0, Math.Max(1, parts[2].Length - 1))}X"
            : $"{token}X";
    }

    private static string AlgNone(string token)
    {
        var parts = token.Split('.');
        var head = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none", typ = "JWT" }))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return parts.Length >= 2 ? $"{head}.{parts[1]}." : token;
    }
}
