using System.Collections.Concurrent;
using System.Diagnostics;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Monitoring;
using IndustrialIot.Edge.Polling;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.Shared.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IndustrialIot.Edge.Tests;

public class BatchReadTests
{
    private static readonly ResolvedTag[] ThreeTags =
    [
        Tag("Motor.Running", "DB1.DBX0.0", PlcDataType.Bool),
        Tag("Line1.Speed", "DB1.DBW2", PlcDataType.Int),
        Tag("Tank.Level", "DB1.DBD4", PlcDataType.Real),
    ];

    [Fact]
    public async Task RealDriver_Outage_OneConnectionAttemptAndSameExceptionForAllTags()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.BlackHole);
        await using var driver = new SiemensS7Driver(
            Options.Create(new SiemensS7Options { Host = "127.0.0.1", Port = server.Port, TimeoutMs = 300 }));

        var stopwatch = Stopwatch.StartNew();
        var results = await driver.ReadAsync(ThreeTags, CancellationToken.None);

        Assert.True(stopwatch.ElapsedMilliseconds < 850, $"batch took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(1, server.AcceptCount);
        Assert.Equal(3, results.Count);
        var first = Assert.IsType<PlcUnavailableException>(results[0].Error);
        Assert.Same(first, results[1].Error);
        Assert.Same(first, results[2].Error);
    }

    [Fact]
    public async Task Helper_KeepsOrderAndIsolatesAnAccessDeniedTag()
    {
        var denied = new PlcAccessDeniedException("PUT/GET disabled");
        var results = await SequentialBatchReader.ReadAsync(
            ThreeTags,
            (tag, _) => tag.TagId == "Line1.Speed" ? throw denied : Task.FromResult<object>(tag.TagId),
            CancellationToken.None);

        Assert.Equal("Motor.Running", results[0].Value);
        Assert.Same(denied, results[1].Error);
        Assert.Equal("Tank.Level", results[2].Value);
    }

    [Fact]
    public async Task Helper_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SequentialBatchReader.ReadAsync(
            ThreeTags,
            (_, token) =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult<object>(0);
            },
            cts.Token));
    }

    [Fact]
    public async Task Poller_CancelledCycle_StoresNothing()
    {
        var driver = new FakePlcDriver();
        driver.Block();
        await using var edge = await StartPollerAsync(driver, new LogCapture());
        await driver.GateEntered.WaitAsync(TimeSpan.FromSeconds(5));

        await edge.Host.StopAsync();

        Assert.Equal(0, await edge.Services.GetRequiredService<TelemetryStore>().CountAsync(default));
        Assert.Null(edge.Services.GetRequiredService<LiveTagSnapshot>().Latest);
    }

    [Fact]
    public async Task Poller_UnexpectedException_OnlyThatTagIsBad_LoggedOncePerCycle()
    {
        var driver = new FakePlcDriver();
        driver.SetRead("DB1.DBX0.0", true);
        driver.SetReadFailure("DB1.DBW2", new InvalidOperationException("boom"));
        driver.SetReadFailure("DB1.DBD4", new InvalidOperationException("boom again"));
        var logs = new LogCapture();
        await using var edge = await StartPollerAsync(driver, logs);

        var snapshot = edge.Services.GetRequiredService<LiveTagSnapshot>();
        var deadline = Stopwatch.StartNew();
        while (snapshot.Latest is null)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), "timed out waiting for the first cycle");
            await Task.Delay(10);
        }

        var tags = snapshot.Latest.Readings;
        Assert.Equal([TelemetryQuality.Good, TelemetryQuality.Bad, TelemetryQuality.Bad], tags.Select(t => t.Quality));
        Assert.Equal([null, "Unexpected error; see the Edge log.", "Unexpected error; see the Edge log."], tags.Select(t => t.Error));
        Assert.Equal(3, driver.Reads.Count);
        Assert.Equal(1, logs.Count("Unexpected error while reading"));
    }

    public static TheoryData<string> BrokenBatches => new() { "throws", "short", "foreign-cancel" };

    [Theory]
    [MemberData(nameof(BrokenBatches))]
    public async Task Poller_BrokenBatch_AllTagsUnexpected_LoggedOnce_PollingContinues(string kind)
    {
        var calls = 0;
        var driver = new BatchDriver(_ =>
        {
            Interlocked.Increment(ref calls);
            return kind switch
            {
                "throws" => throw new InvalidOperationException("driver bug"),
                "short" => Task.FromResult<IReadOnlyList<PlcReadResult>>([PlcReadResult.Ok((short)1)]),
                _ => throw new OperationCanceledException(new CancellationTokenSource().Token),
            };
        });
        var logs = new LogCapture();
        var clock = new FakeTimeProvider();
        await using var edge = await StartPollerAsync(driver, logs, clock, "1000");

        var poller = edge.HostedService<TagPoller>();
        await WaitUntilAsync(() => poller.WaitsArmed >= 1, "first cycle");
        var tags = edge.Services.GetRequiredService<LiveTagSnapshot>().Latest!.Readings;
        Assert.All(tags, t => Assert.Equal(TelemetryQuality.Bad, t.Quality));
        Assert.All(tags, t => Assert.Equal("Unexpected error; see the Edge log.", t.Error));
        Assert.Equal(1, logs.Count("Unexpected error while reading"));

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => poller.WaitsArmed >= 2, "next cycle");
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Poller_ResultWithoutValueOrError_IsBadNotFinite_NoLog()
    {
        var driver = new BatchDriver(_ => Task.FromResult<IReadOnlyList<PlcReadResult>>(
            [PlcReadResult.Ok((short)1), default, PlcReadResult.Ok(1.5f)]));
        var logs = new LogCapture();
        await using var edge = await StartPollerAsync(driver, logs);

        var snapshot = edge.Services.GetRequiredService<LiveTagSnapshot>();
        await WaitUntilAsync(() => snapshot.Latest is not null, "first cycle");

        Assert.Equal([null, "Value is not a finite number.", null], snapshot.Latest!.Readings.Select(t => t.Error));
        Assert.Equal(0, logs.Count("Unexpected error"));
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

    private sealed class BatchDriver(Func<IReadOnlyList<ResolvedTag>, Task<IReadOnlyList<PlcReadResult>>> read) : IPlcDriver
    {
        public Task<IReadOnlyList<PlcReadResult>> ReadAsync(IReadOnlyList<ResolvedTag> tags, CancellationToken ct) => read(tags);

        public Task WriteAsync(IPlcAddress address, PlcDataType type, object value, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> IsReachableAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private static async Task<EdgeTestHost> StartPollerAsync(
        IPlcDriver driver, LogCapture logs, FakeTimeProvider? clock = null, string intervalMs = "3600000")
    {
        var overrides = new Dictionary<string, string?>();
        for (var i = 0; i < ThreeTags.Length; i++)
        {
            overrides[$"Edge:Tags:{i}:TagId"] = ThreeTags[i].TagId;
            overrides[$"Edge:Tags:{i}:Address"] = ThreeTags[i].Address.Canonical;
            overrides[$"Edge:Tags:{i}:DataType"] = ThreeTags[i].DataType.ToString();
        }

        overrides["Edge:Poll:IntervalMs"] = intervalMs;

        var edge = EdgeTestHost.Create(
            overrides,
            services =>
            {
                services.AddSingleton<IPlcDriver>(driver);
                services.AddSingleton<ILoggerProvider>(logs);
                if (clock is not null)
                {
                    services.AddSingleton<TimeProvider>(clock);
                }
            },
            runAgentServices: true);
        await edge.Host.StartAsync();
        return edge;
    }

    private static ResolvedTag Tag(string tagId, string address, PlcDataType type) =>
        new(tagId, PlcAddressParser.Parse(address), type, false, null, null);

    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public int Count(string fragment) => _entries.Count(e => e.Contains(fragment, StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) => new Logger(_entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception));
        }
    }
}
