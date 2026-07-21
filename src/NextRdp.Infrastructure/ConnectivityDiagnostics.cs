using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NextRdp.Core;

namespace NextRdp.Infrastructure;

public sealed class ConnectivityDiagnostics : IConnectivityDiagnostics
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolveAddressesAsync;

    public ConnectivityDiagnostics()
        : this((host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken))
    {
    }

    internal ConnectivityDiagnostics(Func<string, CancellationToken, Task<IPAddress[]>> resolveAddressesAsync)
    {
        this.resolveAddressesAsync = resolveAddressesAsync ?? throw new ArgumentNullException(nameof(resolveAddressesAsync));
    }

    public async Task<DiagnosticResult> TestAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var stopwatch = Stopwatch.StartNew();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        IPAddress[] addresses;
        try
        {
            addresses = await resolveAddressesAsync(host, timeoutSource.Token);
        }
        catch (SocketException ex)
        {
            stopwatch.Stop();
            return new(host, port, false, [], false, stopwatch.Elapsed, $"DNS lookup failed: {ex.SocketErrorCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new(host, port, false, [], false, stopwatch.Elapsed, "DNS lookup timed out before the total connectivity-test deadline.");
        }

        addresses = addresses.Distinct().ToArray();
        if (addresses.Length == 0)
        {
            stopwatch.Stop();
            return new(host, port, true, [], false, stopwatch.Elapsed, "DNS lookup completed but returned no IPv4 or IPv6 addresses.");
        }

        SocketError? lastSocketError = null;
        var attempts = new List<DiagnosticAttempt>();
        foreach (var address in addresses)
        {
            var attemptTimer = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                await client.ConnectAsync(address, port, timeoutSource.Token);
                attemptTimer.Stop();
                attempts.Add(new(address.ToString(), address.AddressFamily.ToString(), true, "Connected", attemptTimer.Elapsed));
                stopwatch.Stop();
                return new DiagnosticResult(host, port, true, addresses.Select(x => x.ToString()).ToArray(), true, stopwatch.Elapsed,
                    $"DNS and TCP connection succeeded using {address}.")
                { Attempts = attempts };
            }
            catch (SocketException ex)
            {
                attemptTimer.Stop();
                lastSocketError = ex.SocketErrorCode;
                attempts.Add(new(address.ToString(), address.AddressFamily.ToString(), false, ex.SocketErrorCode.ToString(), attemptTimer.Elapsed));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                attemptTimer.Stop();
                attempts.Add(new(address.ToString(), address.AddressFamily.ToString(), false, "Timed out", attemptTimer.Elapsed));
                stopwatch.Stop();
                return new DiagnosticResult(host, port, true, addresses.Select(x => x.ToString()).ToArray(), false, stopwatch.Elapsed,
                    "TCP connection timed out before any resolved address accepted the connection.")
                { Attempts = attempts };
            }
        }
        stopwatch.Stop();
        return new DiagnosticResult(host, port, true, addresses.Select(x => x.ToString()).ToArray(), false, stopwatch.Elapsed,
            $"TCP connection failed for every resolved address: {lastSocketError?.ToString() ?? "unreachable"}.")
        { Attempts = attempts };
    }
}
