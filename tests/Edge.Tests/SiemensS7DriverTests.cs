using System.Diagnostics;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Tests;

public class SiemensS7DriverTests
{
    private static readonly PlcAddress IntAddress = PlcAddressParser.Parse("DB1.DBW0");
    private static readonly PlcAddress BoolAddress = PlcAddressParser.Parse("DB1.DBX0.1");

    [Fact]
    public async Task ClosedPort_ThrowsUnavailable()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.ClosedPort);
        await using var driver = CreateDriver(server);

        await Assert.ThrowsAsync<PlcUnavailableException>(() => driver.ReadAsync(IntAddress, PlcDataType.Int, CancellationToken.None));

        // Nothing could be sent, so even a write is "unavailable" (safe to retry), not "outcome unknown".
        await Assert.ThrowsAsync<PlcUnavailableException>(() => driver.WriteAsync(IntAddress, PlcDataType.Int, (short)5, CancellationToken.None));
        Assert.False(await driver.IsReachableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BlackHole_TimesOutWithinBound()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.BlackHole);
        await using var driver = CreateDriver(server, timeoutMs: 500);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<PlcUnavailableException>(() => driver.ReadAsync(IntAddress, PlcDataType.Int, CancellationToken.None));
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"read took {stopwatch.ElapsedMilliseconds} ms");

        // The handshake never completed, so no write job was sent.
        stopwatch.Restart();
        await Assert.ThrowsAsync<PlcUnavailableException>(() => driver.WriteAsync(IntAddress, PlcDataType.Int, (short)5, CancellationToken.None));
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"write took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task AfterFailure_Reconnects()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenSilent);
        await using var driver = CreateDriver(server, timeoutMs: 300);

        await Assert.ThrowsAsync<PlcUnavailableException>(() => driver.ReadAsync(IntAddress, PlcDataType.Int, CancellationToken.None));
        Assert.Equal(1, server.AcceptCount);

        await Assert.ThrowsAsync<PlcUnavailableException>(() => driver.ReadAsync(IntAddress, PlcDataType.Int, CancellationToken.None));
        Assert.Equal(2, server.AcceptCount);
        await WaitUntilAsync(() => server.OpenConnections == 0, "both failed sockets closed");
    }

    [Fact]
    public async Task ConcurrentCalls_AreSerialized()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenRespond, TimeSpan.FromMilliseconds(30), [0x12, 0x34]);
        await using var driver = CreateDriver(server);

        var reads = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => driver.ReadAsync(IntAddress, PlcDataType.Int, CancellationToken.None)))
            .ToArray();
        var values = await Task.WhenAll(reads);

        Assert.All(values, value => Assert.Equal((short)0x1234, value));
        Assert.Equal(1, driver.MaxObservedConcurrency);
        Assert.Equal(1, server.AcceptCount);
    }

    [Fact]
    public async Task LockWaitTimeout_DoesNotDropConnection()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.BlackHole);
        await using var driver = CreateDriver(server, timeoutMs: 4000);

        var holder = driver.ReadAsync(IntAddress, PlcDataType.Int, CancellationToken.None);
        await WaitUntilAsync(() => server.AcceptCount == 1, "holder connected");

        await Assert.ThrowsAsync<PlcBusyException>(
            () => driver.ReadAsync(IntAddress, PlcDataType.Int, TimeSpan.FromMilliseconds(200), CancellationToken.None));

        // Give a wrongly closed socket time to show up on the server side.
        await Task.Delay(300);
        Assert.False(holder.IsCompleted);
        Assert.Equal(1, server.OpenConnections);

        await Assert.ThrowsAsync<PlcUnavailableException>(() => holder);
    }

    [Fact]
    public async Task CallerCancelMidRead_NextCallUsesNewSocket()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenSilent);
        await using var driver = CreateDriver(server, timeoutMs: 10000);

        using var first = new CancellationTokenSource();
        var firstRead = driver.ReadAsync(IntAddress, PlcDataType.Int, first.Token);
        await server.JobReceived.WaitAsync(TimeSpan.FromSeconds(5));
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRead);

        using var second = new CancellationTokenSource();
        var secondRead = driver.ReadAsync(IntAddress, PlcDataType.Int, second.Token);
        await WaitUntilAsync(() => server.AcceptCount == 2, "second call opened a new socket");
        await second.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondRead);

        Assert.Equal(2, server.AcceptCount);
    }

    [Fact]
    public async Task WriteNoAck_OutcomeUnknown()
    {
        await using (var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenSilent))
        await using (var driver = CreateDriver(server, timeoutMs: 500))
        {
            await AssertExactly<PlcWriteOutcomeUnknownException>(() => driver.WriteAsync(IntAddress, PlcDataType.Int, (short)5, CancellationToken.None));
            await AssertExactly<PlcWriteOutcomeUnknownException>(() => driver.WriteAsync(BoolAddress, PlcDataType.Bool, true, CancellationToken.None));
        }

        // Cancelling after the job reached the PLC must not turn "maybe applied" into "cancelled".
        await using (var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenSilent))
        await using (var driver = CreateDriver(server, timeoutMs: 800))
        {
            using var cts = new CancellationTokenSource();
            var stopwatch = Stopwatch.StartNew();
            var write = driver.WriteAsync(IntAddress, PlcDataType.Int, (short)5, cts.Token);
            await server.JobReceived.WaitAsync(TimeSpan.FromSeconds(5));
            await cts.CancelAsync();

            await AssertExactly<PlcWriteOutcomeUnknownException>(() => write);

            // The write ran until its own budget expired; the caller's cancel did not cut it short.
            Assert.True(stopwatch.ElapsedMilliseconds >= 700, $"write ended after {stopwatch.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public async Task WriteTruncatedReply_OutcomeUnknown()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenTruncatedFrame);
        await using var driver = CreateDriver(server);

        // A malformed frame proves nothing about whether the PLC applied the value.
        await AssertExactly<PlcWriteOutcomeUnknownException>(() => driver.WriteAsync(IntAddress, PlcDataType.Int, (short)5, CancellationToken.None));
        await AssertExactly<PlcWriteOutcomeUnknownException>(() => driver.WriteAsync(BoolAddress, PlcDataType.Bool, true, CancellationToken.None));
        await AssertExactly<PlcUnavailableException>(() => driver.ReadAsync(IntAddress, PlcDataType.Int, CancellationToken.None));
    }

    [Theory]
    [InlineData(FakeS7Mode.HandshakeThenErrorHeader)]
    [InlineData(FakeS7Mode.HandshakeThenItemAccessDenied)]
    public async Task PlcErrorResponses_AccessDenied_ReadAndWrite(FakeS7Mode mode)
    {
        await using var server = FakeS7Server.Start(mode);
        await using var driver = CreateDriver(server);

        await AssertExactly<PlcAccessDeniedException>(() => driver.ReadAsync(IntAddress, PlcDataType.Int, CancellationToken.None));
        Assert.Equal(1, server.AcceptCount);

        await AssertExactly<PlcAccessDeniedException>(() => driver.WriteAsync(IntAddress, PlcDataType.Int, (short)5, CancellationToken.None));
        Assert.Equal(2, server.AcceptCount);

        await AssertExactly<PlcAccessDeniedException>(() => driver.WriteAsync(BoolAddress, PlcDataType.Bool, true, CancellationToken.None));
        Assert.Equal(3, server.AcceptCount);

        await AssertExactly<PlcAccessDeniedException>(() => driver.ReadAsync(BoolAddress, PlcDataType.Bool, CancellationToken.None));
        Assert.Equal(4, server.AcceptCount);
    }

    [Fact]
    public async Task SizeMismatch_NoIo()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenRespond);
        await using var driver = CreateDriver(server);
        var wordAddress = PlcAddressParser.Parse("DB1.DBW2");

        await Assert.ThrowsAsync<PlcValueException>(() => driver.ReadAsync(wordAddress, PlcDataType.Real, CancellationToken.None));
        await Assert.ThrowsAsync<PlcValueException>(() => driver.WriteAsync(wordAddress, PlcDataType.Real, 1.5f, CancellationToken.None));
        await Assert.ThrowsAsync<PlcValueException>(() => driver.WriteAsync(wordAddress, PlcDataType.Int, 5, CancellationToken.None)); // int, not short
        await Assert.ThrowsAsync<PlcValueException>(() => driver.ReadAsync(BoolAddress, PlcDataType.Int, CancellationToken.None));

        Assert.Equal(0, server.AcceptCount);
    }

    [Fact]
    public async Task ReadWrite_HappyPath_DecodesBigEndianAndReusesConnection()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenRespond, readData: [0xFF, 0xFE]);
        await using var driver = CreateDriver(server);

        Assert.True(await driver.IsReachableAsync(CancellationToken.None));
        Assert.Equal((short)-2, await driver.ReadAsync(IntAddress, PlcDataType.Int, CancellationToken.None));
        Assert.Equal(true, await driver.ReadAsync(BoolAddress, PlcDataType.Bool, CancellationToken.None)); // 0xFF, bit 1
        await driver.WriteAsync(IntAddress, PlcDataType.Int, (short)5, CancellationToken.None);
        await driver.WriteAsync(BoolAddress, PlcDataType.Bool, false, CancellationToken.None);

        Assert.Equal(1, server.AcceptCount);
    }

    [Theory]
    [InlineData("", "102", "0", "1", "3000")]
    [InlineData("  ", "102", "0", "1", "3000")]
    [InlineData("10.0.0.5", "102", "0", "16", "3000")]
    [InlineData("10.0.0.5", "0", "0", "1", "3000")]
    [InlineData("10.0.0.5", "65536", "0", "1", "3000")]
    [InlineData("10.0.0.5", "102", "8", "1", "3000")]
    [InlineData("10.0.0.5", "102", "0", "1", "99")]
    [InlineData("10.0.0.5", "102", "0", "1", "60001")]
    public async Task InvalidOptions_FailValidation(string plcHost, string port, string rack, string slot, string timeoutMs)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Edge:Plc:Host"] = plcHost,
            ["Edge:Plc:Port"] = port,
            ["Edge:Plc:Rack"] = rack,
            ["Edge:Plc:Slot"] = slot,
            ["Edge:Plc:TimeoutMs"] = timeoutMs,
        };

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSiemensS7Driver(builder.Configuration);
        using var host = builder.Build();

        // ValidateOnStart: the host refuses to start instead of failing at the first PLC call.
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task ValidOptions_RegisterSingletonDriver()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Edge:Plc:Host"] = "10.0.0.5" })
            .Build();
        await using var provider = new ServiceCollection().AddSiemensS7Driver(configuration).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<SiemensS7Options>>().Value;
        Assert.Equal((102, 0, 1, 3000), (options.Port, options.Rack, options.Slot, options.TimeoutMs));
        Assert.Same(provider.GetRequiredService<SiemensS7Driver>(), provider.GetRequiredService<IPlcDriver>());
    }

    private static SiemensS7Driver CreateDriver(FakeS7Server server, int timeoutMs = 3000) =>
        new(Options.Create(new SiemensS7Options { Host = "127.0.0.1", Port = server.Port, TimeoutMs = timeoutMs }));

    private static async Task AssertExactly<TException>(Func<Task> action)
        where TException : Exception
    {
        var exception = await Record.ExceptionAsync(action);
        Assert.IsType<TException>(exception);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), $"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }
}
