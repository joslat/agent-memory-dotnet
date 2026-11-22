using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// Counts database round trips from the order of sends on each connection (DRT-0, C-00): a round trip starts when the
/// client sends after the server has answered, and with a connection's first send. Messages the client sends together
/// before an answer (RUN and PULL, or a batch) are one round trip, however the bytes arrive. It sees only who spoke
/// when, so what the driver does inside does not change the count.
/// </summary>
public sealed class RoundTripCounter
{
    private readonly ConcurrentDictionary<int, StrongBox<int>> _serverSpoke = new();
    private long _count;

    /// <summary>Round trips counted while counting was on.</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>The client sent bytes on <paramref name="connection"/>. Counts a round trip when one starts here.</summary>
    public void ClientSent(int connection, bool count = true)
    {
        var spoke = _serverSpoke.GetOrAdd(connection, _ => new StrongBox<int>(1));
        if (Interlocked.Exchange(ref spoke.Value, 0) == 1 && count)
            Interlocked.Increment(ref _count);
    }

    /// <summary>The server sent bytes on <paramref name="connection"/>.</summary>
    public void ServerSent(int connection) =>
        Volatile.Write(ref _serverSpoke.GetOrAdd(connection, _ => new StrongBox<int>(1)).Value, 1);

    public void Closed(int connection) => _serverSpoke.TryRemove(connection, out _);

    private sealed class StrongBox<T>(T value)
    {
        public T Value = value;
    }
}

/// <summary>
/// A TCP proxy between the product's Neo4j driver and the store, so a perf run can measure waiting (DRT-0, C-00). It
/// counts round trips on the wire (<see cref="RoundTripCounter"/>) and, with <see cref="RoundTrip"/> set, holds every
/// chunk for half of it in each direction, so one round trip costs about that time, as with a store that far away.
/// Counting and the delay apply only while <see cref="Measuring"/> is on: seeding, resets and read-backs outside a turn
/// go at full speed and are not counted. Order is kept in each direction.
/// </summary>
public sealed class LinkProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly ConcurrentDictionary<int, (Socket Client, Socket Server)> _connections = new();
    private readonly bool _finerTimer;
    private int _nextConnection;
    private volatile bool _disposed;
    private long _halfDelayTicks;
    private long _lastActivity = Stopwatch.GetTimestamp();

    private LinkProxy(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        // Windows sleeps in 15.6 ms steps unless asked for 1 ms: a 5 ms round trip would otherwise cost 31 ms.
        _finerTimer = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;
        new Thread(AcceptLoop) { IsBackground = true, Name = "link-proxy-accept" }.Start();
    }

    /// <summary>Starts a proxy on a free loopback port that forwards to <paramref name="targetHost"/>:<paramref name="targetPort"/>.</summary>
    public static LinkProxy Start(string targetHost, int targetPort) => new(targetHost, targetPort);

    /// <summary>The local port the driver connects to.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public RoundTripCounter Counter { get; } = new();

    /// <summary>Whether round trips are counted and delayed now.</summary>
    public volatile bool Measuring;

    /// <summary>The time one round trip costs while measuring; zero forwards at once.</summary>
    public TimeSpan RoundTrip
    {
        get => TimeSpan.FromSeconds(2.0 * Interlocked.Read(ref _halfDelayTicks) / Stopwatch.Frequency);
        set => Interlocked.Exchange(ref _halfDelayTicks, (long)(value.TotalSeconds * Stopwatch.Frequency / 2));
    }

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            Socket client;
            try
            {
                client = _listener.AcceptSocket();
            }
            catch (Exception) when (_disposed)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            var connection = Interlocked.Increment(ref _nextConnection);
            var server = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            client.NoDelay = true;
            try
            {
                server.Connect(_targetHost, _targetPort);
            }
            catch (SocketException)
            {
                client.Dispose();
                server.Dispose();
                continue;
            }
            _connections[connection] = (client, server);
            Pump(connection, client, server, fromClient: true);
            Pump(connection, server, client, fromClient: false);
        }
    }

    private void Pump(int connection, Socket source, Socket destination, bool fromClient)
    {
        var queue = new BlockingCollection<(long Due, byte[] Data)>();
        new Thread(() =>
        {
            try
            {
                foreach (var (due, data) in queue.GetConsumingEnumerable())
                {
                    WaitUntil(due);
                    destination.Send(data);
                }
                destination.Shutdown(SocketShutdown.Send);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // The other side went away; the reader ends too.
            }
        }) { IsBackground = true, Name = $"link-proxy-{connection}-{(fromClient ? "up" : "down")}-send" }.Start();

        new Thread(() =>
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    var read = source.Receive(buffer);
                    if (read == 0) break;
                    Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());
                    var measuring = Measuring;
                    // The flag is set before the bytes move on, so an answer is always seen before the next send.
                    if (fromClient) Counter.ClientSent(connection, measuring);
                    else Counter.ServerSent(connection);
                    var half = measuring ? Interlocked.Read(ref _halfDelayTicks) : 0;
                    queue.Add((Stopwatch.GetTimestamp() + half, buffer.AsSpan(0, read).ToArray()));
                }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // Closed under us.
            }
            finally
            {
                queue.CompleteAdding();
                if (!fromClient) Counter.Closed(connection);
            }
        }) { IsBackground = true, Name = $"link-proxy-{connection}-{(fromClient ? "up" : "down")}-read" }.Start();
    }

    /// <summary>
    /// Waits until no bytes have crossed for <paramref name="quiet"/>: work the product left running after a turn (a
    /// queued write, which a delay makes slower) ends before the next turn starts and is counted there. Gives up after
    /// <paramref name="timeout"/> and says so.
    /// </summary>
    public async Task<bool> WaitForQuietAsync(TimeSpan quiet, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var quietTicks = (long)(quiet.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastActivity) >= quietTicks) return true;
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static void WaitUntil(long due)
    {
        while (true)
        {
            var remaining = due - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            if (remaining * 1000 / Stopwatch.Frequency > 2) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _listener.Stop();
        foreach (var (client, server) in _connections.Values)
        {
            client.Dispose();
            server.Dispose();
        }
        if (_finerTimer) TimeEndPeriod(1);
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
