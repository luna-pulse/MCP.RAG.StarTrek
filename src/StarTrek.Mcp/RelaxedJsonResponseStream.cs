using System.Text.Encodings.Web;
using System.Text.Json;

namespace StarTrek.Mcp;

internal sealed class RelaxedJsonResponseStream : Stream
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Stream original;
    private readonly Func<string?> contentType;
    private readonly Action clearContentLength;
    private readonly Action<long> setContentLength;
    private readonly Func<bool> responseStarted;
    private readonly MemoryStream held = new();
    private readonly MemoryStream pending = new();
    private bool passThrough;
    private bool streamingSse;
    private bool completed;

    public RelaxedJsonResponseStream(
        Stream original,
        Func<string?> contentType,
        Action clearContentLength,
        Action<long> setContentLength,
        Func<bool> responseStarted)
    {
        this.original = original;
        this.contentType = contentType;
        this.clearContentLength = clearContentLength;
        this.setContentLength = setContentLength;
        this.responseStarted = responseStarted;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public async ValueTask CompleteAsync()
    {
        if (completed)
        {
            return;
        }

        completed = true;
        var ready = Observe();
        if (ready.Length > 0)
        {
            await original.WriteAsync(ready);
        }

        if (streamingSse)
        {
            var tail = RewritePending(includeRemainder: true);
            if (tail.Length > 0)
            {
                await original.WriteAsync(tail);
            }

            return;
        }

        if (passThrough)
        {
            return;
        }

        if (held.Length == 0)
        {
            return;
        }

        var body = held.ToArray();
        held.SetLength(0);
        if (IsJson(contentType()))
        {
            if (TryRewrite(body, out var rewritten))
            {
                body = rewritten;
            }

            if (!responseStarted())
            {
                setContentLength(body.Length);
            }
        }

        await original.WriteAsync(body);
    }

    public override void Flush()
    {
        WriteReady(Observe());
        if (streamingSse)
        {
            WriteReady(RewritePending(includeRemainder: false));
            original.Flush();
        }
        else if (passThrough)
        {
            original.Flush();
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await WriteReadyAsync(Observe(), cancellationToken);
        if (streamingSse)
        {
            await WriteReadyAsync(RewritePending(includeRemainder: false), cancellationToken);
            await original.FlushAsync(cancellationToken);
        }
        else if (passThrough)
        {
            await original.FlushAsync(cancellationToken);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        if (passThrough)
        {
            original.Write(buffer);
            return;
        }

        if (streamingSse)
        {
            pending.Write(buffer);
            WriteReady(RewritePending(includeRemainder: false));
            return;
        }

        held.Write(buffer);
        WriteReady(Observe());
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        if (passThrough)
        {
            await original.WriteAsync(buffer, cancellationToken);
            return;
        }

        if (streamingSse)
        {
            pending.Write(buffer.Span);
            await WriteReadyAsync(RewritePending(includeRemainder: false), cancellationToken);
            return;
        }

        held.Write(buffer.Span);
        var ready = Observe();
        if (ready.Length > 0)
        {
            await original.WriteAsync(ready, cancellationToken);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private byte[] Observe()
    {
        if (passThrough || streamingSse)
        {
            return [];
        }

        var mediaType = contentType();
        if (string.IsNullOrEmpty(mediaType))
        {
            return [];
        }

        if (IsEventStream(mediaType))
        {
            streamingSse = true;
            if (!responseStarted())
            {
                clearContentLength();
            }

            if (held.Length > 0)
            {
                pending.Write(held.GetBuffer(), 0, (int)held.Length);
                held.SetLength(0);
            }

            return RewritePending(includeRemainder: false);
        }

        if (IsJson(mediaType))
        {
            return [];
        }

        passThrough = true;
        if (held.Length == 0)
        {
            return [];
        }

        var buffered = held.ToArray();
        held.SetLength(0);
        return buffered;
    }

    private void WriteReady(byte[] ready)
    {
        if (ready.Length > 0)
        {
            original.Write(ready);
        }
    }

    private async ValueTask WriteReadyAsync(byte[] ready, CancellationToken cancellationToken)
    {
        if (ready.Length > 0)
        {
            await original.WriteAsync(ready, cancellationToken);
        }
    }

    private byte[] RewritePending(bool includeRemainder)
    {
        var length = (int)pending.Length;
        if (length == 0)
        {
            return [];
        }

        var source = pending.GetBuffer();
        var complete = 0;
        for (var index = 0; index < length; index++)
        {
            if (source[index] == (byte)'\n')
            {
                complete = index + 1;
            }
        }

        if (includeRemainder)
        {
            complete = length;
        }

        if (complete == 0)
        {
            return [];
        }

        var rewritten = RewriteSse(source.AsSpan(0, complete));
        var rest = length - complete;
        if (rest > 0)
        {
            Buffer.BlockCopy(source, complete, source, 0, rest);
        }

        pending.SetLength(rest);
        pending.Position = rest;
        return rewritten;
    }

    private static byte[] RewriteSse(ReadOnlySpan<byte> chunk)
    {
        using var output = new MemoryStream(chunk.Length + 32);
        var start = 0;
        for (var index = 0; index <= chunk.Length; index++)
        {
            var atEnd = index == chunk.Length;
            if (!atEnd && chunk[index] != (byte)'\n')
            {
                continue;
            }

            if (atEnd && start == chunk.Length)
            {
                break;
            }

            var line = chunk.Slice(start, index - start + (atEnd ? 0 : 1));
            start = index + 1;
            if (TryGetDataPayload(line, out var payload, out var lineBreak) &&
                TryRewrite(payload, out var json))
            {
                output.Write("data: "u8);
                output.Write(json);
                output.Write(lineBreak);
            }
            else
            {
                output.Write(line);
            }
        }

        return output.ToArray();
    }

    private static bool TryGetDataPayload(
        ReadOnlySpan<byte> line,
        out ReadOnlySpan<byte> payload,
        out ReadOnlySpan<byte> lineBreak)
    {
        payload = default;
        lineBreak = default;
        var content = line;
        if (content.Length > 0 && content[^1] == (byte)'\n')
        {
            var breakLength = 1;
            if (content.Length > 1 && content[^2] == (byte)'\r')
            {
                breakLength = 2;
            }

            lineBreak = content[^breakLength..];
            content = content[..^breakLength];
        }

        ReadOnlySpan<byte> prefix = "data:"u8;
        if (!content.StartsWith(prefix))
        {
            return false;
        }

        content = content[prefix.Length..];
        if (content.Length > 0 && content[0] == (byte)' ')
        {
            content = content[1..];
        }

        payload = content;
        return payload.Length > 0;
    }

    internal static bool TryRewrite(ReadOnlySpan<byte> utf8Json, out byte[] rewritten)
    {
        rewritten = [];
        if (utf8Json.IsEmpty)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            using var buffer = new MemoryStream(utf8Json.Length + 16);
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                if (!reader.Read())
                {
                    return false;
                }

                CopyValue(ref reader, writer);
                if (reader.Read())
                {
                    return false;
                }

                writer.Flush();
            }

            rewritten = buffer.ToArray();
            return rewritten.Length > 0;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static void CopyValue(ref Utf8JsonReader reader, Utf8JsonWriter writer)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                writer.WriteStartObject();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        writer.WriteEndObject();
                        return;
                    }

                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        throw new JsonException();
                    }

                    writer.WritePropertyName(reader.GetString() ?? string.Empty);
                    if (!reader.Read())
                    {
                        throw new JsonException();
                    }

                    CopyValue(ref reader, writer);
                }

                throw new JsonException();
            case JsonTokenType.StartArray:
                writer.WriteStartArray();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        writer.WriteEndArray();
                        return;
                    }

                    CopyValue(ref reader, writer);
                }

                throw new JsonException();
            case JsonTokenType.String:
                writer.WriteStringValue(reader.GetString());
                return;
            case JsonTokenType.Number:
                if (reader.HasValueSequence)
                {
                    throw new JsonException();
                }

                writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                return;
            case JsonTokenType.True:
                writer.WriteBooleanValue(true);
                return;
            case JsonTokenType.False:
                writer.WriteBooleanValue(false);
                return;
            case JsonTokenType.Null:
                writer.WriteNullValue();
                return;
            default:
                throw new JsonException();
        }
    }

    private static bool IsJson(string? mediaType) =>
        HasMediaType(mediaType, "application/json");

    private static bool IsEventStream(string? mediaType) =>
        HasMediaType(mediaType, "text/event-stream");

    private static bool HasMediaType(string? header, string mediaType)
    {
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith(mediaType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return header.Length == mediaType.Length || header[mediaType.Length] is ';' or ' ' or '\t';
    }
}
