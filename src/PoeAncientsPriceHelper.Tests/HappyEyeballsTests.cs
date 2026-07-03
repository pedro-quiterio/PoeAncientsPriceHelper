using System.IO;
using System.Net;
using System.Net.Sockets;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

// #16: verify the Happy Eyeballs connect races address families and doesn't stall on a dead one.
// The per-address connect is stubbed so a "black hole" (a connect that never completes until it is
// cancelled) is deterministic, without depending on the host's actual IPv6 routing.
public class HappyEyeballsTests : IDisposable
{
    private static readonly IPAddress V6 = IPAddress.Parse("2001:db8::1"); // documentation range, unroutable
    private static readonly IPAddress V4 = IPAddress.Parse("93.184.216.34");

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _origResolve = HappyEyeballs.Resolve;
    private readonly Func<IPAddress, int, CancellationToken, Task<Stream>> _origConnect = HappyEyeballs.ConnectStream;

    public void Dispose()
    {
        HappyEyeballs.Resolve = _origResolve;
        HappyEyeballs.ConnectStream = _origConnect;
    }

    // A stream we can identify by reference so tests can assert which family won.
    private sealed class TaggedStream(IPAddress address) : MemoryStream
    {
        public IPAddress Address { get; } = address;
    }

    [Fact]
    public async Task Falls_back_to_IPv4_when_IPv6_black_holes()
    {
        HappyEyeballs.Resolve = (_, _) => Task.FromResult(new[] { V6, V4 });
        var v6Cancelled = false;
        HappyEyeballs.ConnectStream = async (addr, _, ct) =>
        {
            if (addr.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // Never connects — simulates a black-holed SYN that only ends when we cancel it.
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { v6Cancelled = true; throw; }
            }
            return new TaggedStream(addr);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = await HappyEyeballs.ConnectAsync(new DnsEndPoint("poe.ninja", 443), cts.Token);

        var tagged = Assert.IsType<TaggedStream>(stream);
        Assert.Equal(AddressFamily.InterNetwork, tagged.Address.AddressFamily);
        // Give the cancelled loser a moment to observe the cancellation.
        await Task.Delay(100);
        Assert.True(v6Cancelled, "the losing IPv6 attempt should have been cancelled");
    }

    [Fact]
    public async Task Healthy_first_address_wins_without_opening_a_second_socket()
    {
        HappyEyeballs.Resolve = (_, _) => Task.FromResult(new[] { V6, V4 });
        var attempts = 0;
        HappyEyeballs.ConnectStream = (addr, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult<Stream>(new TaggedStream(addr));
        };

        var stream = await HappyEyeballs.ConnectAsync(new DnsEndPoint("poe.ninja", 443), default);

        var tagged = Assert.IsType<TaggedStream>(stream);
        Assert.Equal(AddressFamily.InterNetworkV6, tagged.Address.AddressFamily); // v6 preferred, wins instantly
        Assert.Equal(1, attempts); // second family never dialled in the common case
    }

    [Fact]
    public async Task Surfaces_the_error_when_every_address_fails()
    {
        HappyEyeballs.Resolve = (_, _) => Task.FromResult(new[] { V4 });
        HappyEyeballs.ConnectStream = (_, _, _) =>
            Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused));

        var ex = await Assert.ThrowsAsync<SocketException>(
            () => HappyEyeballs.ConnectAsync(new DnsEndPoint("poe.ninja", 443), default).AsTask());
        Assert.Equal(SocketError.ConnectionRefused, ex.SocketErrorCode);
    }

    [Fact]
    public async Task Throws_HostNotFound_when_DNS_returns_nothing()
    {
        HappyEyeballs.Resolve = (_, _) => Task.FromResult(Array.Empty<IPAddress>());

        var ex = await Assert.ThrowsAsync<SocketException>(
            () => HappyEyeballs.ConnectAsync(new DnsEndPoint("nope.invalid", 443), default).AsTask());
        Assert.Equal(SocketError.HostNotFound, ex.SocketErrorCode);
    }
}
