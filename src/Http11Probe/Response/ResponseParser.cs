using System.Text;

namespace Http11Probe.Response;

public static class ResponseParser
{
    public static HttpResponse? TryParse(ReadOnlySpan<byte> data, int length)
    {
        if (length == 0)
            return null;

        // UTF-8, not ASCII: bodies carry non-ASCII post text; ASCII.GetString would
        // silently turn every multi-byte codepoint into '?' and fail shape checks.
        var text = Encoding.UTF8.GetString(data[..length]);

        // Find end of status line
        var lineEnd = text.IndexOf('\n');
        if (lineEnd < 0)
            return null;

        var statusLine = text[..lineEnd].TrimEnd('\r');

        // Parse "HTTP/x.x SSS Reason"
        var firstSpace = statusLine.IndexOf(' ');
        if (firstSpace < 0)
            return null;

        var httpVersion = statusLine[..firstSpace];

        var rest = statusLine[(firstSpace + 1)..];
        var secondSpace = rest.IndexOf(' ');

        string statusCodeStr;
        string reasonPhrase;

        if (secondSpace >= 0)
        {
            statusCodeStr = rest[..secondSpace];
            reasonPhrase = rest[(secondSpace + 1)..];
        }
        else
        {
            statusCodeStr = rest;
            reasonPhrase = string.Empty;
        }

        if (!int.TryParse(statusCodeStr, out var statusCode))
            return null;

        // Parse headers
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pos = lineEnd + 1;

        while (pos < text.Length)
        {
            var nextLineEnd = text.IndexOf('\n', pos);
            if (nextLineEnd < 0)
                nextLineEnd = text.Length;

            var line = text[pos..nextLineEnd].TrimEnd('\r');

            // Empty line = end of headers
            if (line.Length == 0)
                break;

            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var name = line[..colonIndex].Trim();
                var value = line[(colonIndex + 1)..].Trim();

                // If duplicate header, append with comma (RFC 9110 §5.3)
                if (headers.TryGetValue(name, out var existing))
                    headers[name] = existing + ", " + value;
                else
                    headers[name] = value;
            }

            pos = nextLineEnd + 1;
        }

        // Extract body after \r\n\r\n
        string? body = null;
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd >= 0)
        {
            var bodyStart = headerEnd + 4;
            if (bodyStart < text.Length)
            {
                var bodyText = text[bodyStart..];
        // The feed's {"posts":[<post> × 20]} body runs ~6 KB — keep the full body so
        // shape checks see whole JSON.
                body = bodyText.Length > 65536 ? bodyText[..65536] : bodyText;
            }
        }

        var rawResponse = text.Length > 8192
            ? text[..8192] + $"\n\n[Truncated — showing 8,192 of {text.Length:N0} bytes]"
            : text;

        return new HttpResponse
        {
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            HttpVersion = httpVersion,
            Headers = headers,
            IsEmpty = false,
            RawResponse = rawResponse,
            Body = body,
            BufferedStatuses = CountStatusLines(data[..length])
        };
    }

    /// <summary>Counts every "HTTP/1.x NNN" status line in the raw buffer so a
    /// pipelined answer pair can be judged as 2xx + 2xx.</summary>
    private static int[] CountStatusLines(ReadOnlySpan<byte> data)
    {
        List<int> statuses = [];
        for (var i = 0; i + 12 <= data.Length;)
        {
            var idx = data[i..].IndexOf("HTTP/1."u8);
            if (idx < 0)
                break;
            i += idx;
            // "HTTP/1." + version digit + space + three status digits
            if (i + 12 <= data.Length
                && data[i + 7] >= (byte)'0' && data[i + 7] <= (byte)'9'
                && data[i + 8] == (byte)' '
                && data[i + 9] >= (byte)'0' && data[i + 9] <= (byte)'9'
                && data[i + 10] >= (byte)'0' && data[i + 10] <= (byte)'9'
                && data[i + 11] >= (byte)'0' && data[i + 11] <= (byte)'9')
            {
                statuses.Add((data[i + 9] - (byte)'0') * 100
                             + (data[i + 10] - (byte)'0') * 10
                             + (data[i + 11] - (byte)'0'));
            }
            i += 7;
        }
        return [.. statuses];
    }
}
