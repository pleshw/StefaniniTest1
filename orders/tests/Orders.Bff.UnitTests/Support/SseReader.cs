namespace Orders.Bff.UnitTests.Support;

/// <summary>Reads Server-Sent Events off a response stream: one <c>id</c>/<c>event</c>/<c>data</c> block per event.</summary>
internal sealed class SseReader(Stream stream) : IDisposable
{
    private readonly StreamReader _reader = new(stream);

    public async Task<SseEvent> NextAsync(CancellationToken cancellationToken)
    {
        string? id = null, type = null;
        var data = new List<string>();

        while (await _reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Count > 0 || type is not null)
                {
                    return new SseEvent(id, type ?? "message", string.Join('\n', data));
                }

                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var (field, value) = colon < 0 ? (line, string.Empty) : (line[..colon], line[(colon + 1)..].TrimStart(' '));

            switch (field)
            {
                case "id": id = value; break;
                case "event": type = value; break;
                case "data": data.Add(value); break;
            }
        }

        throw new EndOfStreamException("The event stream ended.");
    }

    public void Dispose() => _reader.Dispose();

    public async Task<SseEvent> NextSkippingHeartbeatsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var next = await NextAsync(cancellationToken);

            if (next.Type != "heartbeat")
            {
                return next;
            }
        }
    }
}

internal sealed record SseEvent(string? Id, string Type, string Data);
