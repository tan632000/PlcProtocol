using System.Collections.Concurrent;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Tests;

/// <summary>The S7 driver's batch read against the address-aware fake: request packing, values and error isolation.</summary>
public class S7BatchReadTests
{
    private const byte FunctionRead = 0x04;
    private const byte FunctionWrite = 0x05;
    private const string AccessDeniedHint =
        "In TIA Portal enable \"Permit access with PUT/GET communication\" and disable \"Optimized block access\" on the DB.";

    // The 21 tags of src/Edge/appsettings.json, in the same order.
    private static readonly ResolvedTag[] LabTags =
    [
        Tag("DB1.DBX0.0", PlcDataType.Bool),
        Tag("DB1.DBW2", PlcDataType.Int),
        Tag("DB1.DBW4", PlcDataType.Int),
        Tag("DB1.DBD6", PlcDataType.Real),
        Tag("I0.0", PlcDataType.Bool),
        Tag("IB0", PlcDataType.Byte),
        Tag("Q0.0", PlcDataType.Bool),
        Tag("IW64", PlcDataType.Int),
        Tag("M0.5", PlcDataType.Bool),
        Tag("DB12.DBW238", PlcDataType.Int),
        Tag("DB12.DBW202", PlcDataType.Int),
        Tag("DB17.DBD28", PlcDataType.Real),
        Tag("DB17.DBD24", PlcDataType.Real),
        Tag("DB17.DBW36", PlcDataType.Int),
        Tag("DB17.DBW34", PlcDataType.Int),
        Tag("DB17.DBW32", PlcDataType.Int),
        Tag("DB2.DBD28", PlcDataType.Real),
        Tag("DB2.DBD32", PlcDataType.Real),
        Tag("DB2.DBD12", PlcDataType.Real),
        Tag("DB2.DBD46", PlcDataType.Real),
        Tag("DB2.DBD4", PlcDataType.Real),
    ];

    private static readonly object[] LabValues =
    [
        true, (short)300, (short)-200, 21.5f, true, (byte)5, false, (short)0x1234, true, (short)7, (short)3,
        12.25f, 30.5f, (short)10, (short)20, (short)30, 80f, 121f, 2.5f, -1.5f, 0.75f,
    ];

    private static readonly ResolvedTag[] FiveTags =
    [
        Tag("DB1.DBW2", PlcDataType.Int),
        Tag("DB1.DBW4", PlcDataType.Int),
        Tag("DB1.DBD6", PlcDataType.Real),
        Tag("IB0", PlcDataType.Byte),
        Tag("IW64", PlcDataType.Int),
    ];

    [Fact]
    public async Task LabTags_Pdu240_TwoJobs_ValuesMatchSeededMemory()
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        SeedLab(server);
        await using var driver = CreateDriver(server);

        var results = await driver.ReadAsync(LabTags, CancellationToken.None);

        Assert.Equal(LabValues, results.Select(r => r.Value));
        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal([18, 3], ReadJobs(server));
        Assert.Equal(1, server.AcceptCount);
    }

    [Fact]
    public async Task EveryDataType_OddItemsBeforeWordAndReal_DecodedExactly()
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        server.Seed("DB3.DBB1", 0xAB);
        server.Seed("DB3.DBW2", 0xBE, 0xEF);
        server.Seed("DB3.DBB9", 0x80);
        server.Seed("DB3.DBD10", 0xDE, 0xAD, 0xBE, 0xEF);
        server.Seed("MB5", 0x02);
        server.Seed("DB3.DBD14", 0xFF, 0xFF, 0xFF, 0x85);
        server.Seed("QB3", 0xFE);
        server.Seed("DB3.DBD18", 0x41, 0xAC, 0x00, 0x00);
        server.Seed("IB3", 0x7F);
        server.Seed("DB3.DBW22", 0x80, 0x00);
        await using var driver = CreateDriver(server);
        ResolvedTag[] tags =
        [
            Tag("DB3.DBB1", PlcDataType.Byte),
            Tag("DB3.DBW2", PlcDataType.Word),
            Tag("DB3.DBX9.7", PlcDataType.Bool),
            Tag("DB3.DBD10", PlcDataType.DWord),
            Tag("M5.1", PlcDataType.Bool),
            Tag("DB3.DBD14", PlcDataType.DInt),
            Tag("Q3.0", PlcDataType.Bool),
            Tag("DB3.DBD18", PlcDataType.Real),
            Tag("IB3", PlcDataType.Byte),
            Tag("DB3.DBW22", PlcDataType.Int),
        ];

        var results = await driver.ReadAsync(tags, CancellationToken.None);

        Assert.Equal(
            [(byte)0xAB, (ushort)0xBEEF, true, 0xDEADBEEFu, true, -123, false, 21.5f, (byte)0x7F, short.MinValue],
            results.Select(r => r.Value));
        Assert.Equal([10], ReadJobs(server));
    }

    [Fact]
    public async Task HandWrittenReplyFrame_MatchesTheFake_AndDecodes()
    {
        // S7comm ack-data for three items: IB0 (1 byte, then a fill byte), DB1.DBW2 (2 bytes), DB1.DBD4 (4 bytes).
        byte[] expectedFrame =
        [
            0x03, 0x00, 0x00, 0x29, 0x02, 0xF0, 0x80,
            0x32, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x14, 0x00, 0x00,
            0x04, 0x03,
            0xFF, 0x04, 0x00, 0x08, 0x05, 0x00,
            0xFF, 0x04, 0x00, 0x10, 0x01, 0x2C,
            0xFF, 0x04, 0x00, 0x20, 0x41, 0xAC, 0x00, 0x00,
        ];
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        server.Seed("IB0", 0x05);
        server.Seed("DB1.DBW2", 0x01, 0x2C, 0x41, 0xAC, 0x00, 0x00);
        await using var driver = CreateDriver(server);

        var results = await driver.ReadAsync(
            [Tag("IB0", PlcDataType.Byte), Tag("DB1.DBW2", PlcDataType.Int), Tag("DB1.DBD4", PlcDataType.Real)],
            CancellationToken.None);

        Assert.Equal(expectedFrame, server.LastReply);
        Assert.Equal([(byte)5, (short)300, 21.5f], results.Select(r => r.Value));
    }

    [Theory]
    [InlineData(18, 240, new[] { 18 })]
    [InlineData(19, 240, new[] { 18, 1 })]
    [InlineData(30, 960, new[] { 20, 10 })]
    public async Task ItemCountLimits_DriverSplitsAtRequestLimitAndCap(int tagCount, int pdu, int[] expectedJobs)
    {
        await using var server = FakeS7Server.StartWithMemory(pdu);
        await using var driver = CreateDriver(server);
        var tags = Enumerable.Range(0, tagCount).Select(i => Tag($"DB4.DBB{i}", PlcDataType.Byte)).ToArray();

        var results = await driver.ReadAsync(tags, CancellationToken.None);

        Assert.All(results, r => Assert.Equal((byte)0xEE, r.Value));
        Assert.Equal(expectedJobs, ReadJobs(server));
    }

    [Theory]
    [InlineData(new[] { 100, 118 }, 240, new[] { 2 })]      // response 14 + 8 + 218 = 240: exactly at the limit
    [InlineData(new[] { 100, 119 }, 240, new[] { 1, 1 })]   // 119 is padded to 120: one byte over
    [InlineData(new[] { 1, 1, 1 }, 43, new[] { 2, 1 })]     // request 19 + 24 = 43 fits, 19 + 36 does not
    [InlineData(new[] { 4, 4 }, 30, new[] { 1, 1 })]        // even a single item is over the limit: still one per request
    public void Planner_Boundaries(int[] byteLengths, int pdu, int[] expectedJobs)
    {
        var jobs = new List<int>();
        for (var start = 0; start < byteLengths.Length;)
        {
            var count = S7ReadPlanner.Fit(byteLengths, start, pdu);
            jobs.Add(count);
            start += count;
        }

        Assert.Equal(expectedJobs, jobs);
    }

    [Fact]
    public async Task PduChangesOnReconnect_LaterRequestsPlannedForNewPdu()
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 960);
        server.SetPduSizes(960, 240);
        SeedLab(server);
        await using var driver = CreateDriver(server, timeoutMs: 500);

        Assert.All(await driver.ReadAsync(LabTags, CancellationToken.None), r => Assert.Null(r.Error));
        server.FailJob(3, FakeS7JobFault.Close);
        Assert.All(await driver.ReadAsync(LabTags, CancellationToken.None), r => Assert.IsType<PlcUnavailableException>(r.Error));
        var results = await driver.ReadAsync(LabTags, CancellationToken.None);

        Assert.Equal(LabValues, results.Select(r => r.Value));
        Assert.Equal([(1, 20), (1, 1), (1, 20), (2, 18), (2, 3)], server.Jobs.Select(j => (j.Connection, j.ItemCount)));
    }

    [Fact]
    public async Task ShortFinalItem_ProtocolError_ConnectionDiscarded_NoFurtherRequest()
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        SeedLab(server);
        server.FailJob(1, FakeS7JobFault.ShortLastItem);
        await using var driver = CreateDriver(server);

        var results = await driver.ReadAsync(LabTags, CancellationToken.None);

        var error = Assert.IsType<PlcUnavailableException>(results[0].Error);
        Assert.StartsWith("Reading DB1.DBX0.0 failed: ", error.Message, StringComparison.Ordinal);
        Assert.All(results, r => Assert.Same(error, r.Error));
        Assert.Equal([18], ReadJobs(server));

        Assert.Equal(LabValues, (await driver.ReadAsync(LabTags, CancellationToken.None)).Select(r => r.Value));
        Assert.Equal(2, server.AcceptCount);
    }

    [Theory]
    [InlineData(0x05, 0)] // first item: S7netplus reports "Received error from PLC"
    [InlineData(0x0A, 0)]
    [InlineData(0x05, 2)] // later item: S7netplus reports a wrong byte count
    [InlineData(0x0A, 4)]
    public async Task RefusedItem_OnlyThatTagDenied_ConnectionKept_SoloUntilAccepted(byte returnCode, int refusedIndex)
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        SeedFive(server);
        var refused = FiveTags[refusedIndex].Address.Canonical;
        server.Refuse(refused, returnCode);
        await using var driver = CreateDriver(server);

        var results = await driver.ReadAsync(FiveTags, CancellationToken.None);

        for (var i = 0; i < FiveTags.Length; i++)
        {
            if (i == refusedIndex)
            {
                var denied = Assert.IsType<PlcAccessDeniedException>(results[i].Error);
                Assert.Equal($"PLC refused reading {refused}. {AccessDeniedHint}", denied.Message);
            }
            else
            {
                Assert.Equal(FiveValues[i], results[i].Value);
            }
        }

        Assert.Equal([5, 1, 1, 1, 1, 1], ReadJobs(server));

        // Next cycle: the good tags packed, the remembered one alone.
        await driver.ReadAsync(FiveTags, CancellationToken.None);
        Assert.Equal([4, 1], ReadJobs(server)[6..]);

        // Accepted again: read alone once (and forgotten), then packed with the rest.
        server.Accept(refused);
        Assert.Equal(FiveValues, (await driver.ReadAsync(FiveTags, CancellationToken.None)).Select(r => r.Value));
        Assert.Equal(FiveValues, (await driver.ReadAsync(FiveTags, CancellationToken.None)).Select(r => r.Value));
        Assert.Equal([4, 1, 5], ReadJobs(server)[8..]);
        Assert.Equal(1, server.AcceptCount);
    }

    [Fact]
    public async Task RefusedThenOutage_IsolatedTagsKeepTheirResults()
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        SeedLab(server);
        server.Refuse("DB1.DBW2", 0x05);
        server.FailJob(20, FakeS7JobFault.Close); // job 1 packed (refused), jobs 2-19 solo, job 20 the second packed request
        await using var driver = CreateDriver(server);

        var results = await driver.ReadAsync(LabTags, CancellationToken.None);

        Assert.IsType<PlcAccessDeniedException>(results[1].Error);
        Assert.Equal(LabValues[..18].Where((_, i) => i != 1), results.Take(18).Where((_, i) => i != 1).Select(r => r.Value));
        var error = Assert.IsType<PlcUnavailableException>(results[18].Error);
        Assert.Same(error, results[20].Error);
        Assert.Equal(20, server.Jobs.Count);
    }

    [Fact]
    public async Task RememberedTag_PrunedWhenNoLongerInTheBatch()
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        SeedFive(server);
        server.Refuse("DB1.DBD6", 0x05);
        await using var driver = CreateDriver(server);

        await driver.ReadAsync(FiveTags, CancellationToken.None);
        await driver.ReadAsync([.. FiveTags.Where((_, i) => i != 2)], CancellationToken.None);
        server.Accept("DB1.DBD6");
        var results = await driver.ReadAsync(FiveTags, CancellationToken.None);

        Assert.Equal(FiveValues, results.Select(r => r.Value));
        Assert.Equal([4, 5], ReadJobs(server)[6..]);
    }

    [Fact]
    public async Task PutGetOff_EveryTagDeniedWithTodaysText_ConnectionKept()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenErrorHeader);
        await using var driver = CreateDriver(server);
        var tags = FiveTags[..3];

        for (var cycle = 0; cycle < 2; cycle++)
        {
            var results = await driver.ReadAsync(tags, CancellationToken.None);
            for (var i = 0; i < tags.Length; i++)
            {
                var denied = Assert.IsType<PlcAccessDeniedException>(results[i].Error);
                Assert.Equal($"PLC refused reading {tags[i].Address.Canonical}. {AccessDeniedHint}", denied.Message);
            }
        }

        Assert.Equal(1, server.AcceptCount);

        // Cycle 1: one packed request, then each tag alone; cycle 2: every tag is remembered, so only solo reads.
        Assert.Equal([3, 1, 1, 1, 1, 1, 1], ReadJobs(server));
    }

    [Fact]
    public async Task TruncatedFrame_IsAConnectionFailure_NotAnErrorReply()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenTruncatedFrame);
        await using var driver = CreateDriver(server);

        var results = await driver.ReadAsync(FiveTags, CancellationToken.None);

        var error = Assert.IsType<PlcUnavailableException>(results[0].Error);
        Assert.All(results, r => Assert.Same(error, r.Error));
        Assert.Equal([5], ReadJobs(server));

        await driver.ReadAsync(FiveTags, CancellationToken.None);
        Assert.Equal(2, server.AcceptCount);
    }

    [Fact]
    public async Task BlackHoleAtConnect_OneAttempt_SameException_PreIoErrorsKeptPerTag()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.BlackHole);
        await using var driver = CreateDriver(server, timeoutMs: 300);
        ResolvedTag[] tags = [FiveTags[0], Tag("DB1.DBW4", PlcDataType.Real), FiveTags[2], FiveTags[3]];

        var results = await driver.ReadAsync(tags, CancellationToken.None);

        Assert.IsType<PlcValueException>(results[1].Error);
        var error = Assert.IsType<PlcUnavailableException>(results[0].Error);
        Assert.Same(error, results[2].Error);
        Assert.Same(error, results[3].Error);
        Assert.Equal(1, server.AcceptCount);
        Assert.Empty(server.Jobs);
    }

    [Theory]
    [InlineData(FakeS7JobFault.Close)]
    [InlineData(FakeS7JobFault.Silent)]
    public async Task DropAfterJobOneOfTwo_LaterTagsShareOneException_NoFurtherJob(FakeS7JobFault fault)
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        SeedLab(server);
        server.FailJob(2, fault);
        // One budget covers connect plus the first 18-tag job; 500 ms ran out on a busy CI runner.
        await using var driver = CreateDriver(server, timeoutMs: 2000);

        var results = await driver.ReadAsync(LabTags, CancellationToken.None);

        Assert.Equal(LabValues[..18], results.Take(18).Select(r => r.Value));
        var error = Assert.IsType<PlcUnavailableException>(results[18].Error);
        Assert.StartsWith("Reading DB2.DBD12 failed: ", error.Message, StringComparison.Ordinal);
        Assert.Same(error, results[19].Error);
        Assert.Same(error, results[20].Error);
        Assert.Equal([18, 3], ReadJobs(server));
        Assert.Equal(1, server.AcceptCount);

        // The next cycle makes exactly one more connection attempt.
        Assert.Equal(LabValues, (await driver.ReadAsync(LabTags, CancellationToken.None)).Select(r => r.Value));
        Assert.Equal(2, server.AcceptCount);
    }

    [Fact]
    public async Task DropDuringIsolation_RemainingTagsShareOneException_NoFurtherJob()
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        SeedFive(server);
        server.Refuse("DB1.DBD6", 0x05);
        server.FailJob(3, FakeS7JobFault.Close); // job 1 packed (refused), job 2 first solo read, job 3 second solo read
        await using var driver = CreateDriver(server);

        var results = await driver.ReadAsync(FiveTags, CancellationToken.None);

        Assert.Equal(FiveValues[0], results[0].Value);
        var error = Assert.IsType<PlcUnavailableException>(results[1].Error);
        Assert.All(results.Skip(1), r => Assert.Same(error, r.Error));
        Assert.Equal([5, 1, 1], ReadJobs(server));
        Assert.Equal(1, server.AcceptCount);

        await driver.ReadAsync(FiveTags, CancellationToken.None);
        Assert.Equal(2, server.AcceptCount);
    }

    [Fact]
    public async Task MixedBatch_InvalidTagsKeepTheirErrors_AndAreNeverSent()
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        SeedFive(server);
        await using var driver = CreateDriver(server);
        ResolvedTag[] tags =
        [
            FiveTags[0],
            Tag("DB1.DBW4", PlcDataType.Real),
            new("Foreign", new ForeignAddress(), PlcDataType.Int, false, null, null),
            FiveTags[2],
        ];

        var results = await driver.ReadAsync(tags, CancellationToken.None);

        Assert.Equal(FiveValues[0], results[0].Value);
        Assert.IsType<PlcValueException>(results[1].Error);
        var foreign = Assert.IsType<PlcAccessDeniedException>(results[2].Error);
        Assert.Equal("Address 'X1' is not an S7 address.", foreign.Message);
        Assert.Equal(FiveValues[2], results[3].Value);
        Assert.Equal([2], ReadJobs(server));
    }

    [Fact]
    public async Task WriteDuringTwoJobBatch_RunsBetweenTheJobs()
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240, responseDelay: TimeSpan.FromMilliseconds(150));
        SeedLab(server);
        await using var driver = CreateDriver(server);

        var batch = driver.ReadAsync(LabTags, CancellationToken.None);
        await server.WaitForJobsAsync(1);
        var write = driver.WriteAsync(PlcAddressParser.Parse("DB9.DBW0"), PlcDataType.Int, (short)5, CancellationToken.None);
        await Task.WhenAll(batch, write);

        Assert.Equal([(FunctionRead, 18), (FunctionWrite, 1), (FunctionRead, 3)], server.Jobs.Select(j => (j.Function, j.ItemCount)));
        Assert.Equal(LabValues, (await batch).Select(r => r.Value));
    }

    [Fact]
    public async Task CallerCancel_Propagates()
    {
        await using var server = FakeS7Server.Start(FakeS7Mode.HandshakeThenSilent);
        await using var driver = CreateDriver(server, timeoutMs: 10000);
        using var cts = new CancellationTokenSource();

        var batch = driver.ReadAsync(FiveTags, cts.Token);
        await server.JobReceived.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch);
    }

    [Fact]
    public async Task Logs_NegotiatedPdu_AndJobCount()
    {
        await using var server = FakeS7Server.StartWithMemory(pduSize: 240);
        var logger = new ListLogger();
        await using var driver = new SiemensS7Driver(
            Options.Create(new SiemensS7Options { Host = "127.0.0.1", Port = server.Port, TimeoutMs = 3000 }), logger);

        await driver.ReadAsync(LabTags, CancellationToken.None);
        await driver.ReadAsync(LabTags, CancellationToken.None);

        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("PDU size 240", StringComparison.Ordinal));
        Assert.Equal(2, logger.Entries.Count(e => e.Message.Contains("21 tag(s) in 2 request(s)", StringComparison.Ordinal)));
    }

    private static readonly object[] FiveValues = [(short)300, (short)-200, 21.5f, (byte)5, (short)0x1234];

    private static void SeedFive(FakeS7Server server)
    {
        server.Seed("DB1.DBW2", 0x01, 0x2C, 0xFF, 0x38, 0x41, 0xAC, 0x00, 0x00);
        server.Seed("IB0", 0x05);
        server.Seed("IW64", 0x12, 0x34);
    }

    private static void SeedLab(FakeS7Server server)
    {
        server.Seed("DB1.DBB0", 0x01);
        SeedFive(server);
        server.Seed("QB0", 0x02);
        server.Seed("MB0", 0x20);
        server.Seed("DB12.DBW238", 0x00, 0x07);
        server.Seed("DB12.DBW202", 0x00, 0x03);
        server.Seed("DB17.DBD24", 0x41, 0xF4, 0x00, 0x00, 0x41, 0x44, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x14, 0x00, 0x0A);
        server.Seed("DB2.DBD4", 0x3F, 0x40, 0x00, 0x00);
        server.Seed("DB2.DBD12", 0x40, 0x20, 0x00, 0x00);
        server.Seed("DB2.DBD28", 0x42, 0xA0, 0x00, 0x00, 0x42, 0xF2, 0x00, 0x00);
        server.Seed("DB2.DBD46", 0xBF, 0xC0, 0x00, 0x00);
    }

    private static int[] ReadJobs(FakeS7Server server) =>
        [.. server.Jobs.Where(j => j.Function == FunctionRead).Select(j => j.ItemCount)];

    private static SiemensS7Driver CreateDriver(FakeS7Server server, int timeoutMs = 3000) =>
        new(Options.Create(new SiemensS7Options { Host = "127.0.0.1", Port = server.Port, TimeoutMs = timeoutMs }));

    private static ResolvedTag Tag(string address, PlcDataType type) =>
        new(address, PlcAddressParser.Parse(address), type, false, null, null);

    private sealed record ForeignAddress : IPlcAddress
    {
        public string Canonical => "X1";

        public bool IsBit => false;

        public string? ReadOnlyReason => null;
    }

    private sealed class ListLogger : ILogger<SiemensS7Driver>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
