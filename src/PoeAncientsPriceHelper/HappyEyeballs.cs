using System.Net;
using System.Net.Sockets;

namespace PoeAncientsPriceHelper;

// Fixes stalled poe.ninja/poecdn fetches on connections where one IP family (usually IPv6 on
// Starlink/CGNAT) is a black hole (#16). Those hosts publish both AAAA and A records; the default
// SocketsHttpHandler connect tries addresses in DNS order, so a dead IPv6 SYN hangs until our 15s
// HttpClient timeout fires and the whole fetch cycle fails.
//
// This is a compact RFC 8305 "Happy Eyeballs": resolve the host, then start a TCP connect per
// address staggered by a short delay, racing them. Whichever connects first wins; the losers are
// cancelled and disposed. A dead family loses the race in ~one stagger interval instead of eating
// the timeout, and a healthy connection is unaffected (its attempt starts first and normally wins
// before any second socket is even opened). Covers the reverse case (dead IPv4, healthy IPv6) too.
internal static class HappyEyeballs
{
    // Delay before starting the next address's attempt. RFC 8305 suggests 150–250ms: short enough to
    // stay snappy when the first family is dead, long enough that a healthy first attempt normally
    // wins outright so we don't open throwaway sockets in the common case.
    private static readonly TimeSpan AttemptStagger = TimeSpan.FromMilliseconds(200);

    // Seams for tests: swap in a fixed address list (incl. an unreachable family) and a fake connect
    // so the race/fallback logic can be exercised deterministically without real DNS or sockets.
    internal static Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; set; } =
        static (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    internal static Func<IPAddress, int, CancellationToken, Task<Stream>> ConnectStream { get; set; } =
        DefaultConnectAsync;

    // ConnectCallback signature: (context, ct) => ValueTask<Stream>.
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, CancellationToken ct)
    {
        var addresses = await Resolve(endPoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        var ordered = Interleave(addresses);

        // Cancels the losing attempts (and the pending stagger delay) once we have a winner or bail.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pending = new List<Task<Stream>>(ordered.Count);
        List<Exception>? failures = null;
        Task? stagger = null;
        var next = 0;

        while (next < ordered.Count || pending.Count > 0)
        {
            // Launch the next attempt when nothing is staggered ahead of it (first attempt, or the
            // previous stagger has elapsed). After launching, arm a fresh stagger for the one after.
            if (next < ordered.Count && stagger is null)
            {
                pending.Add(ConnectStream(ordered[next], endPoint.Port, linked.Token));
                next++;
                stagger = next < ordered.Count ? Task.Delay(AttemptStagger, linked.Token) : null;
            }

            var waitSet = new List<Task>(pending);
            if (stagger is not null) waitSet.Add(stagger);

            var done = await Task.WhenAny(waitSet).ConfigureAwait(false);

            if (done == stagger)
            {
                // Stagger elapsed with no winner yet — time to start the next address.
                stagger = null;
                continue;
            }

            var attempt = (Task<Stream>)done;
            pending.Remove(attempt);

            if (attempt.IsCompletedSuccessfully)
            {
                var winner = attempt.Result;
                linked.Cancel();          // stop the stagger + the losing connects
                DisposeLosers(pending);   // dispose any that also connected in the race
                return winner;
            }

            // This address failed (refused/unreachable). Remember why, keep racing the rest.
            (failures ??= []).Add(attempt.Exception?.InnerException ?? attempt.Exception!);
        }

        // Every address failed. Surface the underlying reason(s).
        ct.ThrowIfCancellationRequested();
        throw failures is { Count: 1 }
            ? failures[0]
            : new AggregateException($"Could not connect to {endPoint.Host}:{endPoint.Port}.", failures ?? []);
    }

    // Interleave families (v6, v4, v6, v4, …) so a healthy family is always tried early even when DNS
    // returned the other family's addresses first. IPv6 leads per RFC 8305's preference.
    private static List<IPAddress> Interleave(IPAddress[] addresses)
    {
        var v6 = new Queue<IPAddress>();
        var v4 = new Queue<IPAddress>();
        foreach (var a in addresses)
            (a.AddressFamily == AddressFamily.InterNetworkV6 ? v6 : v4).Enqueue(a);

        var ordered = new List<IPAddress>(addresses.Length);
        while (v6.Count > 0 || v4.Count > 0)
        {
            if (v6.Count > 0) ordered.Add(v6.Dequeue());
            if (v4.Count > 0) ordered.Add(v4.Dequeue());
        }
        return ordered;
    }

    // Observe and dispose losing attempts in the background so a socket that connects just after we
    // picked a winner is closed (and its exception, if any, never goes unobserved).
    private static void DisposeLosers(List<Task<Stream>> losers)
    {
        foreach (var loser in losers)
        {
            _ = loser.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully) t.Result.Dispose();
                else _ = t.Exception; // observe
            }, TaskScheduler.Default);
        }
    }

    private static async Task<Stream> DefaultConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
