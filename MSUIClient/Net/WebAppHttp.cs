using System.Net;
using System.Net.Sockets;

namespace MSUIClient.Net;

// ─────────────────────────────────────────────────────────────────────────────
// The HttpClient every MangosSuperUI web-app call is made through.
//
// It exists for one reason: "localhost". .NET resolves a dual-stack name to its
// IPv6 address FIRST, and the web app listens on IPv4 only. A connection to ::1
// is not refused, it is DROPPED - so the default connect sits in the operating
// system's TCP retransmit timeout (~21 s on Windows) and every request dies on
// its own timeout long before the IPv4 address is ever tried. The symptom is a
// verify or a push that times out against a server the browser loads instantly,
// because browsers implement Happy Eyeballs (RFC 8305) and .NET 8 does not.
//
// So the connect is done here instead: resolve, put IPv4 first, and give each
// address its own short deadline so a black-holed family costs seconds, not the
// whole request budget.
// ─────────────────────────────────────────────────────────────────────────────

public static class WebAppHttp
{
    /// <summary>Per-ADDRESS connect budget. Short on purpose: this is a LAN or
    /// loopback service, and the point is to fall through a dead address family
    /// fast enough that the caller's own timeout still has room to succeed.</summary>
    private static readonly TimeSpan PerAddressTimeout = TimeSpan.FromSeconds(3);

    public static HttpClient Create(TimeSpan timeout) =>
        new(new SocketsHttpHandler { ConnectCallback = ConnectAsync }) { Timeout = timeout };

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        DnsEndPoint endPoint = context.DnsEndPoint;
        IPAddress[] addresses = IPAddress.TryParse(endPoint.Host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endPoint.Host, cancel).ConfigureAwait(false);

        // IPv4 first - see the header comment. Stable ordering otherwise, so a
        // multi-homed host still gets tried in the order DNS returned.
        IPAddress[] ordered = addresses
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        Exception? last = null;
        foreach (IPAddress address in ordered)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                attempt.CancelAfter(PerAddressTimeout);
                await socket.ConnectAsync(address, endPoint.Port, attempt.Token)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
                // The CALLER gave up (its own timeout, or the app is closing) - that is
                // not this address failing, so stop rather than burn the next one too.
                cancel.ThrowIfCancellationRequested();
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
