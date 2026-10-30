using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace HomeBridge.Core;

public static class LanAddress
{
    public static bool IsLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        return IPAddress.IsLoopback(ip) || (b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 ||
            b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 169 && b[1] == 254));
    }
}

public sealed record Invitation(int Version, string Address, int Port, string Fingerprint, string Token, DateTimeOffset Expires)
{
    public void Validate()
    {
        if (Version != 1 || !IPAddress.TryParse(Address, out var ip) || !LanAddress.IsLocal(ip) ||
            Port is < 1024 or > 65535 || Fingerprint is null || Fingerprint.Length != 64 || !Fingerprint.All(Uri.IsHexDigit) ||
            Token is null || Token.Length != 64 || !Token.All(Uri.IsHexDigit) || Expires <= DateTimeOffset.UtcNow || Expires > DateTimeOffset.UtcNow.AddMinutes(6))
            throw new InvalidDataException("Invalid or expired local-network invitation.");
    }
}

public sealed class PairingGate(TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private byte[]? _token;
    private DateTimeOffset _expires;
    private int _attempts;
    public (string Token, DateTimeOffset Expires) Open()
    {
        lock (_gate)
        {
            _token = RandomNumberGenerator.GetBytes(32);
            _expires = _clock.GetUtcNow().AddMinutes(5); _attempts = 0;
            return (Convert.ToHexString(_token), _expires);
        }
    }
    // Consumption is serialized and precedes persistence: a failed disk write requires a new invitation.
    public bool Consume(string? token)
    {
        lock (_gate)
        {
            if (_token is null || _clock.GetUtcNow() >= _expires || ++_attempts > 5) { _token = null; return false; }
            byte[] candidate;
            try { candidate = Convert.FromHexString(token ?? ""); } catch (FormatException) { return false; }
            if (!CryptographicOperations.FixedTimeEquals(_token, candidate)) return false;
            CryptographicOperations.ZeroMemory(_token); _token = null; return true;
        }
    }
    public void Close() { lock (_gate) { if (_token is not null) CryptographicOperations.ZeroMemory(_token); _token = null; } }
}

public sealed record Message(string Operation, string Name = "", string? Token = null);
public sealed record Reply(bool Success, string Name = "");
public static class Wire
{
    public const int MaximumBytes = 4096;
    public static async Task Write<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Message too large.");
        var size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length);
        await stream.WriteAsync(size, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public static async Task<T> Read<T>(Stream stream, CancellationToken ct)
    {
        var size = new byte[4]; await stream.ReadExactlyAsync(size, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(size);
        if (length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid message size.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty message.");
    }
}
