using System.Net;
using System.Net.Sockets;

namespace NextRdp.Infrastructure.Tests;

public sealed class ConnectivityDiagnosticsTests
{
    [Fact]
    public async Task DiagnosticsTryResolvedAddressesUntilTcpSucceeds()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var result = await new ConnectivityDiagnostics().TestAsync("localhost", port, TimeSpan.FromSeconds(3));
            Assert.True(result.DnsSucceeded);
            Assert.True(result.PortReachable);
            Assert.Contains("succeeded", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(result.Attempts, attempt => attempt.Succeeded && attempt.Outcome == "Connected");
            Assert.All(result.Attempts, attempt => Assert.False(string.IsNullOrWhiteSpace(attempt.AddressFamily)));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task DiagnosticsExplainClosedPorts()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var result = await new ConnectivityDiagnostics().TestAsync("127.0.0.1", port, TimeSpan.FromSeconds(2));
        Assert.True(result.DnsSucceeded);
        Assert.False(result.PortReachable);
        Assert.Contains("TCP connection", result.Message, StringComparison.OrdinalIgnoreCase);
        var attempt = Assert.Single(result.Attempts);
        Assert.False(attempt.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(attempt.Outcome));
    }

    [Fact]
    public async Task DiagnosticsApplyTheTotalDeadlineToDnsResolution()
    {
        var diagnostics = new ConnectivityDiagnostics(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });

        var result = await diagnostics.TestAsync("slow-dns.example.test", 3389, TimeSpan.FromMilliseconds(40));

        Assert.False(result.DnsSucceeded);
        Assert.False(result.PortReachable);
        Assert.Empty(result.Attempts);
        Assert.Contains("DNS lookup timed out", result.Message, StringComparison.Ordinal);
        Assert.True(result.Duration < TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("", 3389, 1)]
    [InlineData("localhost", 0, 1)]
    [InlineData("localhost", 3389, 0)]
    public async Task DiagnosticsRejectInvalidInputs(string host, int port, int timeoutSeconds)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            new ConnectivityDiagnostics().TestAsync(host, port, TimeSpan.FromSeconds(timeoutSeconds)));
    }
}
