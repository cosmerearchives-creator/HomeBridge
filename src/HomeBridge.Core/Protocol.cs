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
