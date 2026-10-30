using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HomeBridge.Core;

public sealed record PairingPrompt(string Name, string Code);
public sealed record PairingChallenge(string Commitment, string Name);
public sealed record PairingReveal(string Nonce);
public sealed record PairingDecision(bool Approved);

public static class PairingComparison
{
    public static string Commit(byte[] nonce) => Convert.ToHexString(SHA256.HashData(nonce));
    public static byte[] Reveal(string nonce, string commitment)
    {
        if (nonce is null || commitment is null || nonce.Length != 64 || commitment.Length != 64)
            throw new InvalidDataException("Invalid pairing challenge.");
        var bytes = Convert.FromHexString(nonce);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(commitment)))
            throw new InvalidDataException("Pairing challenge changed. Cancel pairing.");
        return bytes;
    }
    // Commit both random nonces before revealing either; include the roles and TLS certificates.
    public static string Code(string serverPin, string clientPin, byte[] clientNonce, byte[] serverNonce)
    {
        var transcript = Encoding.ASCII.GetBytes("HomeBridge nearby pairing v1|" + serverPin + "|" + clientPin + "|" +
            Convert.ToHexString(clientNonce) + "|" + Convert.ToHexString(serverNonce));
        var number = BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(transcript)) % 100_000_000;
        var digits = number.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        return digits[..4] + " " + digits[4..];
    }
}

public sealed class NearbyPairingWindow(TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTimeOffset _until;
    private int _attempts;
    private bool _busy;
    public void Open() { lock (_gate) { _until = _clock.GetUtcNow().AddMinutes(2); _attempts = 0; } }
    public void Close() { lock (_gate) _until = DateTimeOffset.MinValue; }
    public bool TryEnter()
    {
        lock (_gate)
        {
            if (_busy || _clock.GetUtcNow() >= _until || _attempts >= 5) return false;
            _attempts++; _busy = true; return true;
        }
    }
    public void Exit() { lock (_gate) _busy = false; }
}
