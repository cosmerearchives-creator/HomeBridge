using System.Buffers.Binary;

namespace HomeBridge.Core;

// A local approval grants one connection to one certificate. It is never persisted.
public sealed class ScreenConsent(TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private ScreenLease? _grant;
    private DateTimeOffset _expires;
    private bool _taken;
    public string State
    {
        get
        {
            lock (_gate)
            {
                if (_grant is null) return "Off";
                if (!_taken && _clock.GetUtcNow() >= _expires) { Stop(); return "Off"; }
                return _taken ? "Sharing" : "Waiting";
            }
        }
    }
    public void Approve(string fingerprint)
    {
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid peer fingerprint.");
        Stop();
        lock (_gate)
        {
            _taken = false; _expires = _clock.GetUtcNow().AddMinutes(5);
            _grant = new ScreenLease(fingerprint, Release);
        }
    }
    public ScreenLease? TryBegin(string fingerprint)
    {
        lock (_gate)
        {
            if (_grant is null || _taken || _grant.Fingerprint != fingerprint) return null;
            if (_clock.GetUtcNow() >= _expires) { Stop(); return null; }
            _taken = true; _grant.StartDeadline(); return _grant;
        }
    }
    public void Stop()
    {
        ScreenLease? grant; bool taken;
        lock (_gate) { grant = _grant; taken = _taken; _grant = null; _taken = false; }
        if (grant is null) return;
        grant.Cancel(); if (!taken) grant.Dispose();
    }
    private void Release(ScreenLease lease)
    {
        lock (_gate) if (ReferenceEquals(_grant, lease)) { _grant = null; _taken = false; }
    }
}

public sealed class ScreenLease(string fingerprint, Action<ScreenLease> release) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    public string Fingerprint { get; } = fingerprint;
    public CancellationToken Token => _stop.Token;
    internal void StartDeadline() => _stop.CancelAfter(TimeSpan.FromMinutes(30));
    internal void Cancel() { try { _stop.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); release(this); _stop.Dispose();
    }
}

public static class ScreenFrames
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const int MaximumWidth = 1280;
    public const int MaximumHeight = 720;
    public static (int Width, int Height) ValidateJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 4 or > MaximumBytes || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9)
            throw new InvalidDataException("Invalid screen JPEG.");
        var offset = 2; (int Width, int Height)? dimensions = null;
        while (offset < bytes.Length - 2)
        {
            if (bytes[offset++] != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset + 3 > bytes.Length) break;
            var marker = bytes[offset++];
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || offset + length > bytes.Length) throw new InvalidDataException("Invalid JPEG segment.");
            if (marker == 0xc0)
            {
                if (dimensions is not null || length < 8 || bytes[offset + 2] != 8) throw new InvalidDataException("Invalid JPEG dimensions.");
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                if (width is < 1 or > MaximumWidth || height is < 1 or > MaximumHeight) throw new InvalidDataException("Screen dimensions exceed limits.");
                dimensions = (width, height);
            }
            else if (marker is >= 0xc1 and <= 0xcf && marker is not 0xc4 and not 0xc8 and not 0xcc)
                throw new InvalidDataException("Only baseline JPEG screen frames are supported.");
            if (marker == 0xda) return dimensions ?? throw new InvalidDataException("JPEG dimensions missing.");
            offset += length;
        }
        throw new InvalidDataException("Incomplete screen JPEG header.");
    }
    public static async Task Write(Stream stream, byte[] jpeg, CancellationToken ct)
    {
        ValidateJpeg(jpeg);
        var size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, jpeg.Length);
        await stream.WriteAsync(size, ct); await stream.WriteAsync(jpeg, ct); await stream.FlushAsync(ct);
    }
    public static async Task<byte[]> Read(Stream stream, CancellationToken ct)
    {
        var size = new byte[4]; await stream.ReadExactlyAsync(size, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(size);
        if (length is < 4 or > MaximumBytes) throw new InvalidDataException("Screen frame exceeds size limits.");
        var jpeg = new byte[length]; await stream.ReadExactlyAsync(jpeg, ct); ValidateJpeg(jpeg); return jpeg;
    }
}

public sealed class ScreenStreamServer(ScreenConsent consent, Func<CancellationToken, Task<byte[]>> capture, Func<string, bool> isTrusted)
{
    public async Task Serve(string fingerprint, Stream stream, CancellationToken stop)
    {
        using var lease = isTrusted(fingerprint) ? consent.TryBegin(fingerprint) : null;
        if (lease is null) { await Wire.Write(stream, new Reply(false), stop); return; }
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop, lease.Token);
        var ct = session.Token;
        await Wire.Write(stream, new Reply(true), ct);
        while (!ct.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var request = await Wire.Read<Message>(stream, deadline.Token);
            if (request.Operation != "next-frame" || !isTrusted(fingerprint)) return;
            var jpeg = await capture(deadline.Token);
            if (!isTrusted(fingerprint)) return;
            ct.ThrowIfCancellationRequested();
            await ScreenFrames.Write(stream, jpeg, deadline.Token);
            await Task.Delay(200, ct); // At most five frames/second; one outstanding frame prevents queues.
        }
    }
}
