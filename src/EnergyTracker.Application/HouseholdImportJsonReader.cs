using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace EnergyTracker.Application;

// Story 11.10 (audit S4): the buffer mechanics of validating an upload without ever holding it
// whole. Utf8JsonReader is a ref struct, so this class keeps the stream, the rented buffer and the
// JsonReaderState between calls and builds a fresh reader over the unconsumed bytes for each one.
// Memory is bounded by MaxBufferBytes whatever the file holds: a token or array element that does
// not fit fails with TooLargeException instead of growing the buffer. Utf8JsonReader.Skip() is never
// used on a value of unknown size, because it needs the whole value in the buffer: containers are
// crossed token by token, and only one bounded array element at a time is materialized.
internal sealed class HouseholdImportJsonReader : IDisposable
{
    internal const int MaxBufferBytes = 4 * 1024 * 1024;
    private const int InitialBufferBytes = 64 * 1024;

    // Same limits as JsonDocument.Parse (default JsonReaderOptions: no comments, no trailing commas,
    // depth 64), so a file the old whole-string validator accepted is still accepted.
    private readonly Stream _stream;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferBytes);
    private int _start;
    private int _length;
    private bool _final;
    private JsonReaderState _state;

    public HouseholdImportJsonReader(Stream stream)
    {
        _stream = stream;
        Rewind();
    }

    // Set by ReadToken(captureName: true) when the token is a property name.
    public string? PropertyName { get; private set; }

    public sealed class TooLargeException : Exception
    {
    }

    public void Rewind()
    {
        _stream.Position = 0;
        _start = 0;
        _length = 0;
        _final = false;
        _state = default;
        SkipUtf8Bom();
    }

    // The old StreamReader stripped a BOM; Utf8JsonReader throws on it.
    private void SkipUtf8Bom()
    {
        while (_length - _start < 3 && !_final)
        {
            Fill();
        }

        if (_length - _start >= 3 && _buffer[_start] == 0xEF && _buffer[_start + 1] == 0xBB && _buffer[_start + 2] == 0xBF)
        {
            _start += 3;
        }
    }

    // Reads more of the stream: moves the unconsumed bytes to the front, doubles the buffer up to the
    // cap when they already fill it, and marks the end of input when the stream has nothing left.
    private void Fill()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _length - _start);
            _length -= _start;
            _start = 0;
        }

        if (_length == _buffer.Length)
        {
            if (_buffer.Length >= MaxBufferBytes)
            {
                throw new TooLargeException();
            }

            var bigger = ArrayPool<byte>.Shared.Rent(Math.Min(_buffer.Length * 2, MaxBufferBytes));
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _length);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }

        var read = _stream.Read(_buffer, _length, _buffer.Length - _length);
        if (read == 0)
        {
            _final = true;
        }
        else
        {
            _length += read;
        }
    }

    private void Advance(ref Utf8JsonReader reader)
    {
        _start += (int)reader.BytesConsumed;
        _state = reader.CurrentState;
    }

    // Consumes the next token; JsonTokenType.None means the document ended cleanly. Throws
    // JsonException for malformed input anywhere, including trailing content after the root value.
    public JsonTokenType ReadToken(bool captureName = false)
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _length - _start), _final, _state);
            if (reader.Read())
            {
                PropertyName = captureName && reader.TokenType == JsonTokenType.PropertyName ? reader.GetString() : null;
                var type = reader.TokenType;
                Advance(ref reader);
                return type;
            }

            if (_final)
            {
                Advance(ref reader);
                return JsonTokenType.None;
            }

            Fill();
        }
    }

    public JsonTokenType PeekToken()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _length - _start), _final, _state);
            if (reader.Read())
            {
                return reader.TokenType;
            }

            if (_final)
            {
                return JsonTokenType.None;
            }

            Fill();
        }
    }

    // Materializes the next value (scalar, object or array) once it is complete in the buffer. TrySkip
    // on a copy of the reader answers "is the whole value here yet" without consuming anything.
    public JsonDocument ReadValue()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _length - _start), _final, _state);
            if (reader.Read())
            {
                var probe = reader;
                if (probe.TrySkip())
                {
                    var document = JsonDocument.ParseValue(ref reader);
                    Advance(ref reader);
                    return document;
                }
            }
            else if (_final)
            {
                throw new JsonException("The file ended where a value was expected.");
            }

            Fill();
        }
    }

    // Consumes the next value token by token, so a value of any size costs only the buffer.
    public void SkipValue() => SkipRestOfValue(ReadToken());

    // Same, for a value whose first token (the one passed in) has already been consumed.
    public void SkipRestOfValue(JsonTokenType first)
    {
        if (first is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            return;
        }

        var depth = 1;
        while (depth > 0)
        {
            var token = ReadToken();
            if (token is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                depth++;
            }
            else if (token is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                depth--;
            }
        }
    }

    // After the root value: proves nothing but whitespace follows (a JsonException otherwise).
    public void FinishDocument()
    {
        while (ReadToken() != JsonTokenType.None)
        {
        }
    }

    // Inside an array whose StartArray was consumed: the next element as a document, or false at the
    // closing bracket (consumed).
    public bool TryReadArrayElement([NotNullWhen(true)] out JsonDocument? element)
    {
        if (PeekToken() == JsonTokenType.EndArray)
        {
            ReadToken();
            element = null;
            return false;
        }

        element = ReadValue();
        return true;
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}
