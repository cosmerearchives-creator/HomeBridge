using System.Buffers.Binary;
using System.IO;
using System.Security.Authentication;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HomeBridge.Core;
using Xunit;

public sealed class ScreenTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal static byte[] Jpeg()
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Rgb24, null, new byte[12], 6);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    [Fact] public void ApprovalMatchesOnePeerExpiresAndCannotReconnect()
    {
        var clock = new Clock(); var consent = new ScreenConsent(clock);
        var first = new string('A', 64); var second = new string('B', 64);
        Assert.Null(consent.TryBegin(first)); consent.Approve(first);
        Assert.Null(consent.TryBegin(second)); Assert.Equal("Waiting", consent.State);
        using (var session = consent.TryBegin(first)!) {
            Assert.NotNull(session); Assert.Equal("Sharing", consent.State); Assert.Null(consent.TryBegin(first));
            consent.Stop(); Assert.True(session.Token.IsCancellationRequested);
        }
        Assert.Null(consent.TryBegin(first));
        consent.Approve(first); clock.Now += TimeSpan.FromMinutes(5); Assert.Null(consent.TryBegin(first)); Assert.Equal("Off", consent.State);
        consent.Approve(first); using var old = consent.TryBegin(first)!;
        consent.Approve(second); Assert.True(old.Token.IsCancellationRequested);
        using var current = consent.TryBegin(second)!; Assert.NotNull(current);
    }
    [Fact] public async Task ConcurrentConnectionsCannotReuseApproval()
    {
        var consent = new ScreenConsent(); var peer = new string('A', 64); consent.Approve(peer);
        var sessions = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => consent.TryBegin(peer))));
        Assert.Single(sessions, x => x is not null); foreach (var session in sessions) session?.Dispose();
        Assert.Equal("Off", consent.State);
    }
    [Fact] public async Task RealJpegFramesRoundTripAndOversizedDimensionsAreRejectedBeforeDecode()
    {
        var bytes = Jpeg(); Assert.Equal((2, 2), ScreenFrames.ValidateJpeg(bytes));
        using var stream = new MemoryStream(); await ScreenFrames.Write(stream, bytes, default); stream.Position = 0;
        Assert.Equal(bytes, await ScreenFrames.Read(stream, default));
        var offset = 0;
        for (var i = 0; i < bytes.Length - 1; i++) if (bytes[i] == 0xff && bytes[i + 1] == 0xc0) { offset = i; break; }
        Assert.NotEqual(0, offset); bytes[offset + 7] = 0x7f; bytes[offset + 8] = 0xff;
        Assert.Throws<InvalidDataException>(() => ScreenFrames.ValidateJpeg(bytes));
    }
    [Fact] public async Task MalformedTruncatedAndOversizedFramesFailClosed()
    {
        var size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, ScreenFrames.MaximumBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => ScreenFrames.Read(new MemoryStream(size), default));
        BinaryPrimitives.WriteInt32BigEndian(size, 100);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ScreenFrames.Read(new MemoryStream(size), default));
        Assert.Throws<InvalidDataException>(() => ScreenFrames.ValidateJpeg([0xff, 0xd8, 0xff, 0xd9]));
        Assert.Throws<InvalidDataException>(() => ScreenFrames.ValidateJpeg([1, 2, 3, 4]));
    }
}

[Collection("Network")]
public sealed class ScreenNetworkTests
{
    [Fact] public async Task PairedViewerNeedsFreshApprovalAndStopEndsStream()
    {
        using var hostDir = new Scratch(); using var viewerDir = new Scratch();
        var hostStore = new StateStore(hostDir.Path); var viewerStore = new StateStore(viewerDir.Path);
        var hostSettings = new UserSettings(); var viewerSettings = new UserSettings();
        using var hostId = Identity.Load(hostStore); using var viewerId = Identity.Load(viewerStore);
        await using var host = new NetworkAgent(hostStore, hostSettings, hostId);
        await using var viewer = new NetworkAgent(viewerStore, viewerSettings, viewerId);
        var consent = new ScreenConsent(); var captured = 0; var jpeg = ScreenTests.Jpeg();
        var sharing = new ScreenStreamServer(consent, _ => { Interlocked.Increment(ref captured); return Task.FromResult(jpeg); }, host.IsTrusted);
        host.ScreenRequestHandler = sharing.Serve; host.Start();
        Assert.False((await NetworkTests.RawRequest(hostId, viewerId, new Message("screen-v1"))).Success); Assert.Equal(0, captured);
        var invite = host.Invite("127.0.0.1"); await viewer.Pair(invite);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<AuthenticationException>(() => viewer.ViewScreen("127.0.0.1", invite.Fingerprint, (_, _) => Task.CompletedTask, timeout.Token));
        Assert.Equal(0, captured);
        consent.Approve(Identity.Fingerprint(viewerId));
        var frames = 0;
        var stopped = await Record.ExceptionAsync(() => viewer.ViewScreen("127.0.0.1", invite.Fingerprint, (bytes, _) => {
            Assert.Equal(jpeg, bytes); frames++; consent.Stop(); return Task.CompletedTask;
        }, timeout.Token));
        Assert.True(stopped is IOException or OperationCanceledException, stopped?.ToString());
        Assert.Equal(1, frames); Assert.Equal(1, captured);
        await Assert.ThrowsAsync<AuthenticationException>(() => viewer.ViewScreen("127.0.0.1", invite.Fingerprint, (_, _) => Task.CompletedTask, timeout.Token));
        Assert.Equal(1, captured);
        consent.Approve(Identity.Fingerprint(viewerId));
        var revoked = await Record.ExceptionAsync(() => viewer.ViewScreen("127.0.0.1", invite.Fingerprint, (_, _) => {
            lock (hostSettings) hostSettings.Peers.Clear(); return Task.CompletedTask;
        }, timeout.Token));
        Assert.True(revoked is IOException or OperationCanceledException, revoked?.ToString());
        Assert.Equal(2, captured); Assert.Equal("Off", consent.State);
    }
}
