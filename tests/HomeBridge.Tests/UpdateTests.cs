using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeBridge.Core;
using Xunit;

public sealed class UpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
    private static byte[] Metadata(string version, byte[] installer, string? digest = null, string? url = null, bool draft = false, bool prerelease = true) =>
        JsonSerializer.SerializeToUtf8Bytes(new[] { new {
            tag_name = version, draft, prerelease, assets = new[] { new {
                name = UpdateClient.AssetName, size = installer.LongLength,
                digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant(),
                browser_download_url = url ?? $"https://github.com/{UpdateClient.Repository}/releases/download/{version}/{UpdateClient.AssetName}"
            } }
        } });
    private static UpdateRelease Release(byte[] bytes) => UpdateClient.SelectRelease(Metadata("v1.0.0-alpha.4", bytes), "1.0.0-alpha.3", true)!;

    [Theory]
    [InlineData("1.0.0-alpha.10", "1.0.0-alpha.2", 1)]
    [InlineData("1.0.0", "1.0.0-rc.9", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0+build1", "v1.0.0+build2", 0)]
    [InlineData("2.0.0-alpha.1", "1.9.9", 1)]
    public void VersionsFollowSemver(string left, string right, int direction) =>
        Assert.Equal(direction, Math.Sign(ReleaseVersion.Parse(left).CompareTo(ReleaseVersion.Parse(right))));

    [Theory]
    [InlineData("1.0")] [InlineData("1.0.0-alpha.01")] [InlineData("01.0.0")] [InlineData("1.0.0+bad..tag")]
    public void InvalidVersionsAreRejected(string version) => Assert.Throws<FormatException>(() => ReleaseVersion.Parse(version));

    [Fact] public void EqualOlderDraftAndDisabledPreviewReleasesNeverUpdate()
    {
        var bytes = Encoding.UTF8.GetBytes("test installer");
        Assert.Null(UpdateClient.SelectRelease(Metadata("v1.0.0-alpha.3", bytes), "1.0.0-alpha.3", true));
        Assert.Null(UpdateClient.SelectRelease(Metadata("v1.0.0-alpha.2", bytes), "1.0.0-alpha.3", true));
        Assert.Null(UpdateClient.SelectRelease(Metadata("v1.0.0-alpha.4", bytes, draft: true), "1.0.0-alpha.3", true));
        Assert.Null(UpdateClient.SelectRelease(Metadata("v1.0.0-alpha.4", bytes), "1.0.0-alpha.3", false));
        Assert.NotNull(UpdateClient.SelectRelease(Metadata("v1.0.0", bytes, prerelease: false), "1.0.0-alpha.3", false));
    }

    [Fact] public void ReleasesRequireDigestAndExactRepositoryAssetUrl()
    {
        var bytes = Encoding.UTF8.GetBytes("test installer");
        Assert.Throws<InvalidDataException>(() => UpdateClient.SelectRelease(Metadata("v1.0.0-alpha.4", bytes, digest: ""), "1.0.0-alpha.3", true));
        Assert.Throws<InvalidDataException>(() => UpdateClient.SelectRelease(Metadata("v1.0.0-alpha.4", bytes, url: "https://github.com/attacker/repo/releases/download/v1.0.0-alpha.4/HomeBridge-Setup.exe"), "1.0.0-alpha.3", true));
    }

    [Fact] public async Task VerifiedDownloadIsLockedUntilLaunchAndCanBeDeletedOnCancel()
    {
        using var scratch = new Scratch(); var store = new StateStore(scratch.Path);
        var bytes = Encoding.UTF8.GetBytes("test installer"); var calls = 0;
        using var updater = new UpdateClient(new Handler(request => {
            calls++;
            if (calls == 1) return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/test-asset?signature=test") } };
            Assert.Equal("release-assets.githubusercontent.com", request.RequestUri!.Host);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        using var installer = await updater.Download(Release(bytes), store.DirectoryPath);
        Assert.Equal(bytes, File.ReadAllBytes(installer.Path));
        Assert.Throws<IOException>(() => File.WriteAllText(installer.Path, "replacement"));
        installer.Delete(); Assert.False(File.Exists(installer.Path));
    }

    [Theory] [InlineData("hash")] [InlineData("length")] [InlineData("oversize")]
    public async Task ChangedIncompleteOrOversizedDownloadsAreDeleted(string failure)
    {
        using var scratch = new Scratch(); var store = new StateStore(scratch.Path);
        var bytes = Encoding.UTF8.GetBytes("test installer");
        var downloaded = failure switch { "length" => bytes[..^1], "oversize" => bytes.Concat(new byte[1]).ToArray(), _ => Encoding.UTF8.GetBytes("evil installer") };
        using var updater = new UpdateClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(downloaded) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.Download(Release(bytes), store.DirectoryPath));
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.exe", SearchOption.AllDirectories));
    }

    [Theory] [InlineData("http://github.com/download")] [InlineData("https://evil.example/download")] [InlineData("https://github.com.evil.example/download")]
    public async Task UntrustedRedirectsAreRejected(string target)
    {
        using var scratch = new Scratch(); var store = new StateStore(scratch.Path);
        var bytes = Encoding.UTF8.GetBytes("test installer"); var calls = 0;
        using var updater = new UpdateClient(new Handler(_ => { calls++; return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(target) } }; }));
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.Download(Release(bytes), store.DirectoryPath));
        Assert.Equal(1, calls);
    }

    [Fact] public async Task OfflineAndRateLimitedChecksLeaveInstalledStateAlone()
    {
        using var updater = new UpdateClient(new Handler(_ => new(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<HttpRequestException>(() => updater.Check("1.0.0-alpha.3", true));
        using var empty = new UpdateClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") }));
        Assert.Null(await empty.Check("1.0.0-alpha.3", true));
    }

    [Fact] public void UpdatePreferencesPersistWithoutChangingTrustOrLaunchers()
    {
        using var scratch = new Scratch(); var store = new StateStore(scratch.Path);
        var settings = store.Load(); Assert.True(settings.AutomaticUpdateChecks); Assert.True(settings.IncludePreviewUpdates);
        settings.AutomaticUpdateChecks = false; settings.IncludePreviewUpdates = false;
        settings.Peers.Add(new(new string('A', 64), "Paired PC")); settings.Applications.Add(new("Editor", @"C:\editor.exe")); store.Save(settings);
        var loaded = store.Load(); Assert.False(loaded.AutomaticUpdateChecks); Assert.False(loaded.IncludePreviewUpdates);
        Assert.Single(loaded.Peers); Assert.Single(loaded.Applications);
    }
}
