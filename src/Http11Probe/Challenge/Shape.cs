using System.Text.Json;

namespace Http11Probe.Challenge;

public static class Shape
{
    public const string CreatedAtPattern = "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$";

    /// <summary>Checks a body is a JSON object with exactly the five post keys
    /// (id, body, created_at, author, like_count) in that order, with the right types;
    /// created_at must match the SPEC millisecond-Z format.</summary>
    public static string? IsPost(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return "not a JSON object";
            var keys = doc.RootElement.EnumerateObject().ToArray();
            var names = keys.Select(k => k.Name).ToArray();
            var want = new[] { "id", "body", "created_at", "author", "like_count" };
            if (names.Length != want.Length || !names.SequenceEqual(want))
                return $"keys are [{string.Join(",", names)}], wanted [{string.Join(",", want)}]";
            for (var i = 0; i < keys.Length; i++)
            {
                switch (want[i])
                {
                    case "id":
                    case "like_count":
                        if (keys[i].Value.ValueKind != JsonValueKind.Number)
                            return $"{want[i]} is not a number";
                        break;
                    case "body":
                    case "created_at":
                    case "author":
                        if (keys[i].Value.ValueKind != JsonValueKind.String)
                            return $"{want[i]} is not a string";
                        break;
                }
            }
            var created = doc.RootElement.GetProperty("created_at").GetString()!;
            if (!System.Text.RegularExpressions.Regex.IsMatch(created, CreatedAtPattern))
                return $"created_at '{created}' does not match {CreatedAtPattern}";
            return null;
        }
        catch (JsonException ex)
        {
            return $"body is not JSON ({ex.Message})";
        }
    }

    /// <summary>Checks {"posts":[ ... ]} — exactly 20 entries, each a valid post object.</summary>
    public static string? IsFeed(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return "not a JSON object";
            var props = doc.RootElement.EnumerateObject().ToArray();
            if (props.Length != 1 || props[0].Name != "posts")
                return $"top-level keys are [{string.Join(",", props.Select(p => p.Name))}], wanted [posts]";
            if (props[0].Value.ValueKind != JsonValueKind.Array)
                return "posts is not an array";
            var posts = props[0].Value.EnumerateArray().ToArray();
            if (posts.Length != 20)
                return $"posts has {posts.Length} entries, wanted 20";
            for (var i = 0; i < posts.Length; i++)
            {
                var issue = IsPost(posts[i].GetRawText());
                if (issue is not null)
                    return $"posts[{i}] is not a post: {issue}";
            }
            return null;
        }
        catch (JsonException ex)
        {
            return $"body is not JSON ({ex.Message})";
        }
    }

    /// <summary>The known post's like_count at the end of the like endpoint's good steps.</summary>
    public static int? PostLikeCount(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("post", out var post) && post.TryGetProperty("like_count", out var lc) && lc.ValueKind == JsonValueKind.Number)
                return lc.GetInt32();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
