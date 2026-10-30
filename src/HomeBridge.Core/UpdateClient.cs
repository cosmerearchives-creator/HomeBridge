using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HomeBridge.Core;

public sealed record UpdateRelease(string Tag, Uri DownloadUrl, string Sha256, long Size);

// The trust root is GitHub HTTPS and control of this exact repository, not a LAN advertisement.
public sealed class UpdateClient : IDisposable
{
    public const string Repository = "cosmerearchives-creator/HomeBridge";
    public const string AssetName = "HomeBridge-Setup.exe";
    public const long MaximumInstallerSize = 300 * 1024 * 1024;
    private readonly HttpClient _http;
    public UpdateClient(HttpMessageHandler? handler = null)
    {
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HomeBridge", "1.0"));
    }
    public async Task<UpdateRelease?> Check(string currentVersion, bool includePrerelease, CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bytes = new MemoryStream();
        await CopyBounded(stream, bytes, 2 * 1024 * 1024, timeout.Token);
        return SelectRelease(bytes.ToArray(), currentVersion, includePrerelease);
    }
    public static UpdateRelease? SelectRelease(byte[] json, string currentVersion, bool includePrerelease)
    {
        var current = ReleaseVersion.Parse(currentVersion);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid release list.");
        var candidates = new List<(ReleaseVersion Version, JsonElement Release)>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            ReleaseVersion version;
            try { version = ReleaseVersion.Parse(tag); } catch (FormatException) { continue; }
            if (!includePrerelease && (release.GetProperty("prerelease").GetBoolean() || version.Prerelease.Length > 0)) continue;
            if (version.CompareTo(current) > 0) candidates.Add((version, release));
        }
        if (candidates.Count == 0) return null;
        var latest = candidates.OrderByDescending(c => c.Version).First().Release;
        var tagName = latest.GetProperty("tag_name").GetString()!;
        var asset = latest.GetProperty("assets").EnumerateArray().SingleOrDefault(a => a.GetProperty("name").GetString() == AssetName);
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("The new release has no Windows installer yet. Try again later.");
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        var url = asset.GetProperty("browser_download_url").GetString();
        var size = asset.GetProperty("size").GetInt64();
        var expected = $"https://github.com/{Repository}/releases/download/{tagName}/{AssetName}";
        if (url != expected || digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 ||
            !digest[7..].All(Uri.IsHexDigit) || size is <= 0 or > MaximumInstallerSize)
            throw new InvalidDataException("Release installer verification metadata is missing or invalid. No update will be installed.");
        return new(tagName, new Uri(url), digest[7..], size);
    }
    public async Task<VerifiedInstaller> Download(UpdateRelease release, string stateDirectory, CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromMinutes(10)); cancel = timeout.Token;
        // Revalidate even when called without Check. Never download an arbitrary URL supplied by another module.
        ReleaseVersion.Parse(release.Tag);
        if (release.DownloadUrl.AbsoluteUri != $"https://github.com/{Repository}/releases/download/{release.Tag}/{AssetName}" ||
            release.Size is <= 0 or > MaximumInstallerSize || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid installer source.");
        var directory = Path.Combine(stateDirectory, "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var security = new DirectoryInfo(directory).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
        var path = Path.Combine(directory, AssetName);
        FileStream? file = null;
        try
        {
            var url = release.DownloadUrl;
            using var response = await FollowDownload(url, cancel);
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("Installer size changed.");
            using var source = await response.Content.ReadAsStreamAsync(cancel);
            file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 81920, FileOptions.Asynchronous);
            await CopyBounded(source, file, release.Size, cancel);
            if (file.Length != release.Size) throw new InvalidDataException("Installer download was incomplete.");
            await file.FlushAsync(cancel); file.Flush(true); file.Dispose();
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var hash = await SHA256.HashDataAsync(file, cancel);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(release.Sha256))) throw new InvalidDataException("Installer SHA-256 did not match. Download deleted.");
            // Keep an open read-only share to prevent replacement between verification and launch.
            return new VerifiedInstaller(path, file);
        }
        catch { file?.Dispose(); if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); throw; }
    }
    private async Task<HttpResponseMessage> FollowDownload(Uri url, CancellationToken cancel)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo) || url.Port != 443 ||
                (url.Host != "github.com" && url.Host != "release-assets.githubusercontent.com"))
                throw new InvalidDataException("Untrusted installer redirect.");
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new InvalidDataException("Empty download redirect.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location); continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("Too many installer redirects.");
    }
    private static async Task CopyBounded(Stream source, Stream destination, long maximum, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct); if (read == 0) return;
            total += read; if (total > maximum) throw new InvalidDataException("Download exceeds the permitted size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
    public void Dispose() => _http.Dispose();
}

public sealed class VerifiedInstaller(string path, FileStream guard) : IDisposable
{
    public string Path { get; } = path;
    public void Dispose() => guard.Dispose();
    public void Delete()
    {
        guard.Dispose(); if (File.Exists(Path)) File.Delete(Path);
        Directory.Delete(System.IO.Path.GetDirectoryName(Path)!);
    }
}
