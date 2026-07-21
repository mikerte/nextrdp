using System.Buffers.Binary;
using System.Security.Cryptography;
using NextRdp.Platform.Application;

namespace NextRdp.Platform.Infrastructure;

public sealed class ProtectedSyncCursorCodec(ReadOnlyMemory<byte> key) : ISyncCursorCodec
{
    private const int PayloadLength = sizeof(long);
    private const int TagLength = 16;
    private readonly byte[] key = ValidateKey(key);

    public string Encode(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        Span<byte> payload = stackalloc byte[PayloadLength];
        BinaryPrimitives.WriteInt64BigEndian(payload, sequence);
        var tag = HMACSHA256.HashData(key, payload);
        Span<byte> encoded = stackalloc byte[PayloadLength + TagLength];
        payload.CopyTo(encoded);
        tag.AsSpan(0, TagLength).CopyTo(encoded[PayloadLength..]);
        return Convert.ToBase64String(encoded).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public long Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        if (cursor.Length > 128) throw PlatformProblemException.Validation(["Synchronization cursor is invalid."]);

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            var bytes = Convert.FromBase64String(normalized);
            if (bytes.Length != PayloadLength + TagLength) throw new FormatException();
            var expected = HMACSHA256.HashData(key, bytes.AsSpan(0, PayloadLength));
            if (!CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, TagLength), bytes.AsSpan(PayloadLength, TagLength)))
                throw new CryptographicException();
            var sequence = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, PayloadLength));
            if (sequence < 0) throw new FormatException();
            return sequence;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw PlatformProblemException.Validation(["Synchronization cursor is invalid."]);
        }
    }

    private static byte[] ValidateKey(ReadOnlyMemory<byte> value)
    {
        if (value.Length < 32) throw new ArgumentException("Cursor protection key must contain at least 32 bytes.", nameof(value));
        return value.ToArray();
    }
}
