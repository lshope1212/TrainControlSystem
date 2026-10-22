using System.Text;
using System.Text.Json;
using TrainControl.Common.Communication;

namespace TrackController.Runtime;
/// <summary>Shared message envelopes over an ordered request/reply stream inside an SSH tunnel.</summary>
internal sealed class PiProtocol : IDisposable
{
    public const int Version = 8;
    private const int MaxCharacters = 1_048_576;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    /// <summary>Wraps a connected stream for bounded line-delimited application messages.</summary>
    public PiProtocol(Stream stream)
    {
        _reader = new(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        _writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            NewLine = "\n"
        };
    }

    /// <summary>Reads one bounded JSON envelope and rejects truncated or malformed input.</summary>
    public async Task<MessageEnvelope> ReadAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (true)
        {
            if (await _reader.ReadAsync(character.AsMemory(), cancellationToken) == 0)
            {
                throw new EndOfStreamException("Pi connection closed.");
            }

            if (character[0] == '\n')
            {
                return MessageSerializer.Deserialize(line.ToString());
            }

            if (line.Length >= MaxCharacters)
            {
                throw new InvalidDataException("Pi message exceeds 1 MiB characters.");
            }

            line.Append(character[0]);
        }
    }

    /// <summary>Writes a bounded application message, preserving any existing envelope.</summary>
    public async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        string json = MessageSerializer.Serialize(message);
        if (json.Length > MaxCharacters)
        {
            throw new InvalidDataException("Pi message exceeds 1 MiB characters.");
        }

        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);
    }

    /// <summary>Closes the stream readers and writers owned by the Pi session.</summary>
    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
    }
}
