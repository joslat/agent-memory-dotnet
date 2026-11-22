using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

/// <summary>
/// DRT-0 (C-00): the perf tool's link proxy counts round trips on the wire and can make each one cost a set time.
/// The recorded exchanges are the Bolt messages the driver sent in strategy/performance/database-round-trips/evidence
/// (wire-messages.json); each list is one connection, in order, with a server answer between bursts.
/// </summary>
public sealed class LinkProxyTests
{
    private static long Count(params string[] sends)
    {
        var counter = new RoundTripCounter();
        foreach (var send in sends)
        {
            if (send.StartsWith('>')) counter.ClientSent(1);
            else counter.ServerSent(1);
        }
        return counter.Count;
    }

    [Fact]
    public void An_auto_commit_query_on_a_new_session_is_two_round_trips()
    {
        Count(">RESET", "<SUCCESS", ">RUN", ">PULL", "<SUCCESS RECORD SUCCESS").Should().Be(2);
    }

    [Fact]
    public void A_managed_read_transaction_of_one_query_is_four_round_trips()
    {
        Count(">RESET", "<SUCCESS", ">BEGIN", "<SUCCESS", ">RUN PULL", "<SUCCESS", "<RECORD SUCCESS", ">COMMIT", "<SUCCESS")
            .Should().Be(4);
    }

    [Fact]
    public void Bytes_of_one_burst_arriving_in_pieces_are_one_round_trip()
    {
        Count(">RU", ">N PU", ">LL", "<SUCC", "<ESS").Should().Be(1);
    }

    [Fact]
    public void Each_connection_counts_its_own_round_trips()
    {
        var counter = new RoundTripCounter();
        counter.ClientSent(1);
        counter.ClientSent(2);
        counter.ServerSent(1);
        counter.ClientSent(2);
        counter.ClientSent(1);

        counter.Count.Should().Be(3);
    }

    [Fact]
    public void Sends_outside_a_measurement_are_not_counted_but_still_close_a_burst()
    {
        var counter = new RoundTripCounter();
        counter.ClientSent(1, count: false);
        counter.ClientSent(1);
        counter.ServerSent(1);
        counter.ClientSent(1);

        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task Through_the_proxy_each_exchange_is_counted_and_costs_the_round_trip_set()
    {
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var serving = Task.Run(async () =>
        {
            using var accepted = await echo.AcceptSocketAsync();
            var buffer = new byte[256];
            int read;
            while ((read = await accepted.ReceiveAsync(buffer.AsMemory(), SocketFlags.None)) > 0)
                await accepted.SendAsync(buffer.AsMemory(0, read), SocketFlags.None);
        });
        using var proxy = LinkProxy.Start("127.0.0.1", ((IPEndPoint)echo.LocalEndpoint).Port);
        using var client = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);

        async Task<double> ExchangeMs()
        {
            var answer = new byte[4];
            var started = Stopwatch.GetTimestamp();
            await client.SendAsync("ping"u8.ToArray(), SocketFlags.None);
            var got = 0;
            while (got < 4) got += await client.ReceiveAsync(answer.AsMemory(got), SocketFlags.None);
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        await ExchangeMs();                         // not measuring: forwarded at once, not counted
        proxy.Counter.Count.Should().Be(0);

        proxy.RoundTrip = TimeSpan.FromMilliseconds(40);
        proxy.Measuring = true;
        var slow = new[] { await ExchangeMs(), await ExchangeMs(), await ExchangeMs() };
        proxy.Measuring = false;

        proxy.Counter.Count.Should().Be(3);
        slow.Should().OnlyContain(ms => ms >= 38, "each exchange waits half the round trip each way");
        client.Shutdown(SocketShutdown.Both);
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
