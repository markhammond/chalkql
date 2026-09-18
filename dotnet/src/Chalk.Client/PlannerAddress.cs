using System.Globalization;
using System.Net.Sockets;

namespace Chalk.Client;

/// <summary>
/// The two shapes a planner address takes (docs/design/09-unix-socket-transport.md §4):
/// <c>http://host:port</c> / <c>https://…</c> for a sidecar reached over TCP, and
/// <c>unix:///absolute/path</c> for one on the same machine.
/// </summary>
internal static class PlannerAddress
{
    internal const string UnixScheme = "unix";

    /// <summary>
    /// The longest socket path the planner will bind: macOS's <c>sun_path</c> is 104 bytes including
    /// the terminator and Linux's is 108, so 100 is the portable budget. Kept in step with
    /// <c>PlannerConfig.MAX_SOCKET_PATH_BYTES</c> on the Java side.
    /// </summary>
    internal const int MaxSocketPathBytes = 100;

    internal static bool IsUnix(Uri address) =>
        string.Equals(address.Scheme, UnixScheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The filesystem path behind a <c>unix:</c> address. A relative path has no meaning to a
    /// sidecar with its own working directory, so it is refused here rather than failing to connect
    /// somewhere surprising.
    /// </summary>
    internal static string RequireSocketPath(Uri address, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IsUnix(address))
        {
            throw new ArgumentException($"{address} is not a unix: address", parameterName);
        }

        if (!string.IsNullOrEmpty(address.Host) || !address.LocalPath.StartsWith('/'))
        {
            throw new ArgumentException(
                $"a unix: planner address needs an absolute path, as in unix:///tmp/chalk.sock; got {address}",
                parameterName);
        }

        return address.LocalPath;
    }

    /// <summary>The address of a sidecar listening on <paramref name="socketPath"/>.</summary>
    internal static Uri ForSocket(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        if (!Path.IsPathRooted(socketPath))
        {
            throw new ArgumentException(
                $"a socket path must be absolute; got {socketPath}", nameof(socketPath));
        }

        return new Uri(UnixScheme + "://" + socketPath);
    }

    /// <summary>
    /// Refuses a path that <c>sockaddr_un</c> cannot hold before anything tries to bind or connect,
    /// where the failure would be a bare <c>EINVAL</c>.
    /// </summary>
    internal static void RequireBindablePath(string socketPath, string parameterName)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(socketPath);
        if (bytes > MaxSocketPathBytes)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"socket path is {bytes} bytes, over the {MaxSocketPathBytes}-byte limit this "
                    + $"platform's sockaddr_un leaves: {socketPath}"),
                parameterName);
        }
    }

    /// <summary>
    /// A handler that dials a Unix socket instead of a TCP endpoint. The channel address is a dummy
    /// <c>http://localhost</c> — HTTP/2 needs an authority, and this one is never resolved.
    /// </summary>
    internal static SocketsHttpHandler UnixSocketHandler(string socketPath) =>
        new()
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

    /// <summary>The dummy authority a Unix-socket channel carries.</summary>
    internal static readonly Uri UnixChannelAuthority = new("http://localhost");
}
