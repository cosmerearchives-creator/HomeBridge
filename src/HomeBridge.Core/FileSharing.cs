using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace HomeBridge.Core;

public sealed record SharedLocation(Guid Id, string Path, bool Upload);
public sealed record SharedEntry(string Name, bool Folder, long Size = 0, Guid Share = default, bool Upload = false);
internal sealed record FileRequest(Guid Share, string Path, long Size = 0);
internal sealed record FileResponse(bool Ok, int Count = 0, long Size = 0);

public static class SharePaths
{
    public static string Child(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 ||
            name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\') || name.Contains(':') || name.StartsWith(".homebridge-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid file name.");
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "0123456789¹²³".Contains(stem[3])))
            throw new InvalidDataException("Reserved file name.");
        return Path.Combine(root, name);
    }

    public static void CheckLocal(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || path[1] != ':' || path[2] != '\\' ||
            new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            throw new InvalidDataException("Choose a folder or file on a local fixed drive.");
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Links and junctions cannot be shared or used for transfers.");
        }
    }

    internal static string Resolve(string root, string relative)
    {
        CheckLocal(root);
        if (relative.Length == 0) return root;
        if (relative.Length > 1024) throw new InvalidDataException("Path is too long.");
        var result = root;
        foreach (var name in relative.Split('/')) result = Child(result, name);
        CheckLocal(result);
        return result;
    }

    internal static string UploadTarget(string root, string relative)
    {
        CheckLocal(root);
        if (!Directory.Exists(root) || relative.Length is 0 or > 1024) throw new InvalidDataException("Invalid upload destination.");
        var segments = relative.Split('/');
        // Validate the whole path before creating anything.
        var validated = root;
        foreach (var segment in segments) validated = Child(validated, segment);
        var directory = root;
        foreach (var segment in segments[..^1])
        {
            directory = Child(directory, segment);
            Directory.CreateDirectory(directory);
            CheckLocal(directory);
        }
        return validated;
    }

    internal static async Task ReceiveNew(Stream source, string destination, long length, Func<bool> allowed,
        IProgress<long>? progress, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(destination)!;
        CheckLocal(directory);
        var staging = Path.Combine(directory, $".homebridge-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await Copy(source, file, length, allowed, progress, ct);
                await file.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            if (!allowed()) throw new AuthenticationException("Permission was revoked.");
            CheckLocal(directory);
            File.Move(staging, destination, overwrite: false);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    internal static async Task Copy(Stream source, Stream destination, long length, Func<bool> allowed,
        IProgress<long>? progress, CancellationToken ct)
    {
        if (length is < 0 or > NetworkAgent.MaxFileSize) throw new InvalidDataException("Files must be at most 10 GiB.");
        var buffer = new byte[65536];
        long done = 0;
        while (done < length)
        {
            if (!allowed()) throw new AuthenticationException("Permission was revoked.");
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromSeconds(20));
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - done)), idle.Token);
            if (read == 0) throw new EndOfStreamException("Transfer interrupted.");
            await destination.WriteAsync(buffer.AsMemory(0, read), idle.Token);
            done += read;
            progress?.Report(done);
        }
    }
}

public sealed partial class NetworkAgent
{
    public const long MaxFileSize = 10L * 1024 * 1024 * 1024;
    private const int MaxEntries = 2000;

    private bool CanAccess(string pin, SharedLocation share)
    {
        lock (settings) return IsTrusted(pin) && settings.Shares.Contains(share);
    }

    private async Task ServeFiles(string operation, string pin, Stream tls, CancellationToken ct)
    {
        if (operation == "files-roots")
        {
            SharedEntry[] entries;
            lock (settings)
                entries = settings.Shares.Select(s => new SharedEntry(Path.GetFileName(s.Path.TrimEnd('\\')),
                    Directory.Exists(s.Path), Share: s.Id, Upload: s.Upload)).Take(MaxEntries).ToArray();
            await SendEntries(tls, entries, ct);
            return;
        }
        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        var request = await Wire.Read<FileRequest>(tls, requestDeadline.Token);
        SharedLocation? share;
        lock (settings) share = settings.Shares.Find(s => s.Id == request.Share);
        if (share is null || !CanAccess(pin, share) || request.Path is null) return;
        bool Allowed() => CanAccess(pin, share);
        switch (operation)
        {
            case "files-list":
                var folder = SharePaths.Resolve(share.Path, request.Path);
                var entries = Directory.EnumerateFileSystemEntries(folder)
                    .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                        !Path.GetFileName(path).StartsWith(".homebridge-", StringComparison.OrdinalIgnoreCase))
                    .Take(MaxEntries + 1)
                    .Select(path => new SharedEntry(Path.GetFileName(path), Directory.Exists(path)))
                    .ToArray();
                if (entries.Length > MaxEntries || !Allowed()) return;
                await SendEntries(tls, entries, ct);
                break;
            case "files-get":
                var source = SharePaths.Resolve(share.Path, request.Path);
                if (Path.GetFileName(source).StartsWith(".homebridge-", StringComparison.OrdinalIgnoreCase)) return;
                await using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                {
                    if (file.Length > MaxFileSize || !Allowed()) return;
                    await Wire.Write(tls, new FileResponse(true, Size: file.Length), ct);
                    await SharePaths.Copy(file, tls, file.Length, Allowed, null, ct);
                }
                break;
            case "files-mkdir":
                if (!share.Upload || !Allowed()) return;
                var newFolder = SharePaths.UploadTarget(share.Path, request.Path);
                Directory.CreateDirectory(newFolder);
                SharePaths.CheckLocal(newFolder);
                await Wire.Write(tls, new FileResponse(true), ct);
                break;
            case "files-put":
                if (!share.Upload || request.Size is < 0 or > MaxFileSize) return;
                var destination = SharePaths.UploadTarget(share.Path, request.Path);
                if (File.Exists(destination) || Directory.Exists(destination) || !Allowed()) return;
                await Wire.Write(tls, new FileResponse(true), ct);
                await SharePaths.ReceiveNew(tls, destination, request.Size, Allowed, null, ct);
                await Wire.Write(tls, new FileResponse(true), ct);
                break;
        }
    }

    private static async Task SendEntries(Stream tls, SharedEntry[] entries, CancellationToken ct)
    {
        await Wire.Write(tls, new FileResponse(true, Count: entries.Length), ct);
        foreach (var entry in entries) await Wire.Write(tls, entry, ct);
    }

    private async Task WithFileConnection(TrustedPeer peer, string operation, FileRequest? request,
        Func<Stream, CancellationToken, Task> work, CancellationToken cancel)
    {
        if (!IsTrusted(peer.Fingerprint) || !IPAddress.TryParse(peer.Address, out var ip) || !LanAddress.IsLocal(ip))
            throw new AuthenticationException("Select a reachable paired PC.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        handshake.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync(ip, Port, handshake.Token);
        using var tls = new SslStream(client.GetStream());
        await AuthenticateClient(tls, peer.Fingerprint, handshake.Token);
        await Wire.Write(tls, new Message(operation), handshake.Token);
        if (request is not null) await Wire.Write(tls, request, handshake.Token);
        try { await work(tls, timeout.Token); }
        catch (EndOfStreamException ex)
        { throw new IOException("The other PC refused access or ended the transfer. Check sharing permissions, duplicate names and connectivity.", ex); }
    }

    private static async Task<FileResponse> ReadFileResponse(Stream stream, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var reply = await Wire.Read<FileResponse>(stream, deadline.Token);
        return reply.Ok ? reply : throw new IOException("The other PC refused the request.");
    }

    public async Task<IReadOnlyList<SharedEntry>> Browse(TrustedPeer peer, Guid share, string path, CancellationToken ct)
    {
        var entries = new List<SharedEntry>();
        await WithFileConnection(peer, share == default ? "files-roots" : "files-list",
            share == default ? null : new FileRequest(share, path), async (stream, cancel) =>
            {
                var reply = await ReadFileResponse(stream, cancel);
                if (reply.Count is < 0 or > MaxEntries) throw new InvalidDataException("Invalid folder listing.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                for (var i = 0; i < reply.Count; i++) entries.Add(await Wire.Read<SharedEntry>(stream, deadline.Token));
            }, ct);
        return entries;
    }

    public Task Download(TrustedPeer peer, Guid share, string path, string destination, IProgress<long>? progress, CancellationToken ct)
        => WithFileConnection(peer, "files-get", new(share, path), async (stream, cancel) =>
        {
            var reply = await ReadFileResponse(stream, cancel);
            await SharePaths.ReceiveNew(stream, destination, reply.Size, () => IsTrusted(peer.Fingerprint), progress, cancel);
        }, ct);

    public Task CreateUploadFolder(TrustedPeer peer, Guid share, string path, CancellationToken ct)
        => WithFileConnection(peer, "files-mkdir", new(share, path),
            async (stream, cancel) => { await ReadFileResponse(stream, cancel); }, ct);

    public async Task Upload(TrustedPeer peer, Guid share, string path, string source, IProgress<long>? progress, CancellationToken ct)
    {
        SharePaths.CheckLocal(source);
        await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (file.Length > MaxFileSize) throw new InvalidDataException("Files must be at most 10 GiB.");
        await WithFileConnection(peer, "files-put", new(share, path, file.Length), async (stream, cancel) =>
        {
            await ReadFileResponse(stream, cancel);
            await SharePaths.Copy(file, stream, file.Length, () => IsTrusted(peer.Fingerprint), progress, cancel);
            await ReadFileResponse(stream, cancel);
        }, ct);
    }
}
