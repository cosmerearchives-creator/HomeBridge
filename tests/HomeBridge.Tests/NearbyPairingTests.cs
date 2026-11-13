using System.Security.Authentication;
using System.Security.Cryptography;
using HomeBridge.Core;
using Xunit;

public sealed class ComparisonTests
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    [Fact] public void CommitmentsDetectChangedNoncesAndCodesBindRolesAndCertificates()
    {
        var a = RandomNumberGenerator.GetBytes(32); var b = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(a, PairingComparison.Reveal(Convert.ToHexString(a), PairingComparison.Commit(a)));
        Assert.Throws<InvalidDataException>(() => PairingComparison.Reveal(Convert.ToHexString(b), PairingComparison.Commit(a)));
        var x = new string('A', 64); var y = new string('B', 64);
        var code = PairingComparison.Code(x, y, a, b);
        Assert.Matches("^[0-9]{4} [0-9]{4}$", code);
        Assert.NotEqual(code, PairingComparison.Code(y, x, a, b));
        Assert.NotEqual(code, PairingComparison.Code(x, y, b, a));
    }
    [Fact] public void PairingWindowExpiresLimitsAttemptsAndSerializesRequests()
    {
        var clock = new Clock(); var window = new NearbyPairingWindow(clock);
        Assert.False(window.TryEnter()); window.Open();
        for (var i = 0; i < 5; i++) { Assert.True(window.TryEnter()); Assert.False(window.TryEnter()); window.Exit(); }
        Assert.False(window.TryEnter()); window.Open(); clock.Now += TimeSpan.FromMinutes(2); Assert.False(window.TryEnter());
        window.Open(); window.Close(); Assert.False(window.TryEnter());
    }
}

[Collection("Network")]
public sealed class NearbyPairingTests
{
    [Fact] public async Task ClosingPairingWindowCancelsPendingApprovalWithoutSavingTrust()
    {
        using var hostDir = new Scratch(); using var clientDir = new Scratch();
        var hs = new StateStore(hostDir.Path); var cs = new StateStore(clientDir.Path);
        var hostSettings = new UserSettings(); var clientSettings = new UserSettings();
        using var hi = Identity.Load(hs); using var ci = Identity.Load(cs);
        await using var host = new NetworkAgent(hs, hostSettings, hi); await using var client = new NetworkAgent(cs, clientSettings, ci);
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ConfirmNearbyPairing = async (_, ct) => { prompted.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return true; };
        host.Start(); host.OpenNearbyPairing();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pairing = client.PairNearby(new("127.0.0.1", "Host", Identity.Fingerprint(hi)), (_, _) => Task.FromResult(true), deadline.Token);
        await prompted.Task.WaitAsync(deadline.Token); host.CloseNearbyPairing();
        var error = await Record.ExceptionAsync(() => pairing);
        Assert.True(error is IOException or OperationCanceledException or AuthenticationException, error?.ToString());
        Assert.Empty(hostSettings.Peers); Assert.Empty(clientSettings.Peers);
    }
    [Theory] [InlineData(true, true)] [InlineData(false, true)] [InlineData(true, false)]
    public async Task TrustRequiresTwoExplicitMatchingCodeApprovals(bool serverApproves, bool clientApproves)
    {
        using var hostDir = new Scratch(); using var clientDir = new Scratch();
        var hs = new StateStore(hostDir.Path); var cs = new StateStore(clientDir.Path);
        var hostSettings = new UserSettings(); var clientSettings = new UserSettings();
        using var hi = Identity.Load(hs); using var ci = Identity.Load(cs);
        await using var host = new NetworkAgent(hs, hostSettings, hi); await using var client = new NetworkAgent(cs, clientSettings, ci);
        string? hostCode = null; string? clientCode = null;
        host.ConfirmNearbyPairing = (p, _) => { hostCode = p.Code; return Task.FromResult(serverApproves); };
        host.Start();
        var device = new DiscoveredDevice("127.0.0.1", "Host", Identity.Fingerprint(hi));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<AuthenticationException>(() => client.PairNearby(device, (_, _) => Task.FromResult(true), deadline.Token));
        Assert.Null(hostCode); Assert.Empty(hostSettings.Peers);
        host.OpenNearbyPairing();
        var error = await Record.ExceptionAsync(() => client.PairNearby(device, (p, _) => { clientCode = p.Code; return Task.FromResult(clientApproves); }, deadline.Token));
        Assert.Equal(hostCode, clientCode); Assert.NotNull(hostCode);
        if (serverApproves && clientApproves) {
            Assert.Null(error); Assert.Single(hs.Load().Peers); Assert.Single(cs.Load().Peers);
            Assert.Equal("127.0.0.1", hs.Load().Peers[0].Address); Assert.Equal("127.0.0.1", cs.Load().Peers[0].Address);
            await client.Ping(device.Address, device.Fingerprint, deadline.Token);
            await Assert.ThrowsAsync<AuthenticationException>(() => client.PairNearby(device, (_, _) => Task.FromResult(true), deadline.Token));
        }
        else { Assert.IsType<AuthenticationException>(error); Assert.Empty(hostSettings.Peers); Assert.Empty(clientSettings.Peers); }
    }
}
