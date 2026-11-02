using System.Diagnostics;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Drivers.Slmp;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Tests;

public sealed class SlmpDriverTests : IAsyncLifetime
{
    private static readonly SlmpAddressSyntax Syntax = new();

    private readonly FakeSlmpServer _server = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // Every test runs against a server that records anything but a Device Read; none may ever be sent.
    public async Task DisposeAsync()
    {
        var unexpected = _server.UnexpectedFrames;
        var commands = _server.Commands;
        await _server.DisposeAsync();
        Assert.Empty(unexpected);
        Assert.All(commands, command => Assert.Contains(command, new ushort[] { 0x0401, 0x1401 }));
    }

    [Fact]
    public async Task StringAndLReal_AreReadFromDRegisters()
    {
        _server.Memory[100] = 0x4241; // "AB"
        _server.Memory[101] = 0x0043; // "C" and a NUL
        var bits = BitConverter.DoubleToInt64Bits(1.5e300);
        for (var w = 0; w < 4; w++)
        {
            _server.Memory[200 + w] = (ushort)(bits >> (16 * w)); // low word first
        }

        await using var driver = CreateDriver(timeoutMs: 3000);

        var results = await driver.ReadAsync(
        [
            new ResolvedTag("Recipe", Syntax.Parse("D100", PlcDataType.String, 4), PlcDataType.String, false, null, null, Length: 4),
            Tag("Ratio", "D200", PlcDataType.LReal),
        ], CancellationToken.None);

        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal(["ABC", 1.5e300], results.Select(r => r.Value));
    }

    [Theory]
    [InlineData("D100", PlcDataType.String, 254, true)]
    [InlineData("D100", PlcDataType.String, 255, false)]
    [InlineData("D7998", PlcDataType.String, 6, false)] // three registers run past D7999
    [InlineData("M100", PlcDataType.String, 2, false)]
    [InlineData("D7996", PlcDataType.LReal, null, true)]
    [InlineData("D7997", PlcDataType.LReal, null, false)]
    [InlineData("D100", PlcDataType.DTL, null, false)]
    public void NewTypes_FitTheDRegisters(string address, PlcDataType type, int? length, bool accepted)
    {
        if (accepted)
        {
            Assert.Equal(address, Syntax.Parse(address, type, length).Canonical);
        }
        else
        {
            Assert.ThrowsAny<PlcException>(() => Syntax.Parse(address, type, length));
        }
    }

    [Fact]
    public async Task DeviceWriteFrames_AreExact()
    {
        await using var driver = CreateDriver(timeoutMs: 3000);

        // [UNVERIFIED until checked against SH-081951 and a real FX5U] the read header with command 01 14, subcommand
        // 00 00 (words) or 01 00 (bits), head, device code, points and data.
        Assert.Equal(
            [0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x0E, 0x00, 0x0A, 0x00, 0x01, 0x14, 0x00, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x01, 0x00, 0xD2, 0x04],
            driver.BuildDeviceWrite(SlmpDevice.D, 100, 0x0000, 1, [0xD2, 0x04]));
        Assert.Equal(
            [0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x0D, 0x00, 0x0A, 0x00, 0x01, 0x14, 0x01, 0x00, 0x05, 0x00, 0x00, 0x90, 0x01, 0x00, 0x10],
            driver.BuildDeviceWrite(SlmpDevice.M, 5, 0x0001, 1, [0x10]));
    }

    [Fact]
    public async Task WrittenValues_ReadBack()
    {
        await using var driver = CreateDriver();

        await driver.WriteAsync(Syntax.Parse("D100", PlcDataType.Int), PlcDataType.Int, (short)1234, CancellationToken.None);
        await driver.WriteAsync(Syntax.Parse("D102", PlcDataType.DInt), PlcDataType.DInt, 123456, CancellationToken.None);
        await driver.WriteAsync(Syntax.Parse("D110", PlcDataType.LReal), PlcDataType.LReal, 1.5e300, CancellationToken.None);
        await driver.WriteAsync(Syntax.Parse("D120", PlcDataType.String, 5), PlcDataType.String, "AB\0\0\0", CancellationToken.None);
        await driver.WriteAsync(Syntax.Parse("M5", PlcDataType.Bool), PlcDataType.Bool, true, CancellationToken.None);
        await driver.WriteAsync(Syntax.Parse("Y7", PlcDataType.Bool), PlcDataType.Bool, true, CancellationToken.None);

        var results = await driver.ReadAsync(
        [
            Tag("a", "D100", PlcDataType.Int),
            Tag("b", "D102", PlcDataType.DInt),
            Tag("c", "D110", PlcDataType.LReal),
            new ResolvedTag("d", Syntax.Parse("D120", PlcDataType.String, 5), PlcDataType.String, false, null, null, Length: 5),
            Tag("e", "M5", PlcDataType.Bool),
            Tag("f", "Y7", PlcDataType.Bool),
            Tag("g", "M4", PlcDataType.Bool),
        ], CancellationToken.None);

        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal([(short)1234, 123456, 1.5e300, "AB", true, true, false], results.Select(r => r.Value));
        Assert.Equal(0, _server.Memory[122]); // the padding cleared the String's last register
        Assert.Equal(
            [(0xA8, 100, 1), (0xA8, 102, 2), (0xA8, 110, 4), (0xA8, 120, 3), (0x90, 5, 1), (0x9D, 7, 1)],
            _server.Writes.Select(w => ((int)w.Code, w.Head, w.Points)));
    }

    [Fact]
    public async Task AWriteWhoseReplyNeverComes_HasAnUnknownOutcome()
    {
        _server.DropNextWrite();
        await using var driver = CreateDriver();

        var error = await Record.ExceptionAsync(
            () => driver.WriteAsync(Syntax.Parse("D100", PlcDataType.Int), PlcDataType.Int, (short)1, CancellationToken.None));

        Assert.IsType<PlcWriteOutcomeUnknownException>(error);
    }

    [Fact]
    public async Task AWriteTheFx5uRefuses_IsAccessDenied_AndTheConnectionStaysInStep()
    {
        _server.FailNextWith(0xC051);
        _server.Memory[0] = 7;
        await using var driver = CreateDriver();

        var error = await Record.ExceptionAsync(
            () => driver.WriteAsync(Syntax.Parse("D100", PlcDataType.Int), PlcDataType.Int, (short)1, CancellationToken.None));
        var read = await driver.ReadAsync([Tag("a", "D0", PlcDataType.Int)], CancellationToken.None);

        Assert.Contains("0xC051", Assert.IsType<PlcAccessDeniedException>(error).Message, StringComparison.Ordinal);
        Assert.Equal((short)7, read[0].Value);
        Assert.Equal(1, _server.Connections);
        Assert.False(_server.Memory.ContainsKey(100));
    }

    [Fact]
    public async Task AWrongValueForTheAddress_IsRefusedBeforeAnyIo()
    {
        await using var driver = CreateDriver();

        await Assert.ThrowsAsync<PlcValueException>(() => driver.WriteAsync(Syntax.Parse("M5", PlcDataType.Bool), PlcDataType.Int, (short)1, CancellationToken.None));
        await Assert.ThrowsAsync<PlcValueException>(() => driver.WriteAsync(Syntax.Parse("D7999", PlcDataType.Int), PlcDataType.DInt, 1, CancellationToken.None));

        Assert.Empty(_server.Frames);
    }

    [Fact]
    public async Task AWriteDuringAReadCycle_WaitsAndBothComplete()
    {
        _server.Mode = FakeSlmpMode.RespondByteByByte; // slow replies keep the read busy
        _server.Memory[0] = 7;
        await using var driver = CreateDriver(timeoutMs: 5000);
        var tags = Enumerable.Range(0, 10).Select(i => Tag($"t{i}", $"D{i * 10}", PlcDataType.Int)).ToArray();

        var read = driver.ReadAsync(tags, CancellationToken.None);
        var write = driver.WriteAsync(Syntax.Parse("D500", PlcDataType.Int), PlcDataType.Int, (short)42, CancellationToken.None);
        var results = await read;
        await write;

        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal((short)7, results[0].Value);
        Assert.Equal(42, _server.Memory[500]);
    }

    [Fact]
    public void TheCommandCodes_AreExactlyDeviceReadAndDeviceWrite()
    {
        var codes = typeof(SlmpCommands)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (ushort)f.GetRawConstantValue()!)
            .Order();

        Assert.Equal(new ushort[] { 0x0401, 0x1401 }, codes);
    }

    [Fact]
    public async Task DeviceReadFrame_IsExact()
    {
        await using var driver = CreateDriver(timeoutMs: 3000);

        // [UNVERIFIED until checked against SH-081951] layout per the SLMP 3E binary frame:
        // 50 00 | 00 | FF | FF 03 | 00 | length 12 | timer (3000-500)/250 = 10 | 0401 | 0000 | D100 = 64 00 00 | A8 | 2 points
        byte[] expected =
        [
            0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x0C, 0x00, 0x0A, 0x00,
            0x01, 0x04, 0x00, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x02, 0x00,
        ];

        Assert.Equal(expected, driver.BuildDeviceReadWords(SlmpDevice.D, 100, 2));
        await driver.ReadAsync([Tag("T", "D100", PlcDataType.DInt)], CancellationToken.None);
        Assert.Equal(expected, Assert.Single(_server.Frames));
    }

    [Fact]
    public async Task Decoding_IsLittleEndianLowWordFirst()
    {
        _server.Memory[0] = 0xFFFF; // Int -1
        _server.Memory[1] = 0xE240; // DInt 123456 = 0x0001E240
        _server.Memory[2] = 0x0001;
        _server.Memory[3] = 0xFFFE; // DInt -2
        _server.Memory[4] = 0xFFFF;
        _server.Memory[5] = 0x0000; // Real 12.5 = 0x41480000
        _server.Memory[6] = 0x4148;
        _server.Memory[7] = 0xFFFF; // Word 65535
        _server.Memory[8] = 0x2800; // DWord 4000000000 = 0xEE6B2800
        _server.Memory[9] = 0xEE6B;
        await using var driver = CreateDriver();

        var results = await driver.ReadAsync(
            [
                Tag("a", "D0", PlcDataType.Int),
                Tag("b", "D1", PlcDataType.DInt),
                Tag("c", "D3", PlcDataType.DInt),
                Tag("d", "D5", PlcDataType.Real),
                Tag("e", "D7", PlcDataType.Word),
                Tag("f", "d8", PlcDataType.DWord),
            ],
            CancellationToken.None);

        Assert.Equal((short)-1, results[0].Value);
        Assert.Equal(123456, results[1].Value);
        Assert.Equal(-2, results[2].Value);
        Assert.Equal(12.5f, results[3].Value);
        Assert.Equal((ushort)65535, results[4].Value);
        Assert.Equal(4000000000u, results[5].Value);
        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Single(_server.Frames);
    }

    [Fact]
    public async Task TouchingTags_AreOneRequest()
    {
        await using var driver = CreateDriver();

        await driver.ReadAsync([Tag("a", "D0", PlcDataType.Int), Tag("b", "D1", PlcDataType.Int)], CancellationToken.None);

        Assert.Equal([(0, 2)], _server.Requests.Select(r => (r.Head, r.Points)));
        Assert.Equal(0xA8, Assert.Single(_server.Requests).Code);
    }

    [Fact]
    public async Task TagsWithAGap_AreTwoRequestsAndNoGapWordIsRead()
    {
        await using var driver = CreateDriver();

        await driver.ReadAsync([Tag("a", "D0", PlcDataType.Int), Tag("b", "D2", PlcDataType.Int)], CancellationToken.None);

        Assert.Equal([(0, 1), (2, 1)], _server.Requests.Select(r => (r.Head, r.Points)));
    }

    [Fact]
    public async Task OverlappingTags_ShareOneRequest()
    {
        await using var driver = CreateDriver();

        await driver.ReadAsync([Tag("a", "D0", PlcDataType.DInt), Tag("b", "D1", PlcDataType.Int)], CancellationToken.None);

        Assert.Equal([(0, 2)], _server.Requests.Select(r => (r.Head, r.Points)));
    }

    [Fact]
    public async Task Words961_AreTwoRequests()
    {
        await using var driver = CreateDriver();
        var tags = Enumerable.Range(0, 961).Select(i => Tag($"t{i}", $"D{i}", PlcDataType.Int)).ToArray();

        var results = await driver.ReadAsync(tags, CancellationToken.None);

        Assert.Equal([(0, 960), (960, 1)], _server.Requests.Select(r => (r.Head, r.Points)));
        Assert.All(results, r => Assert.Null(r.Error));
    }

    [Fact]
    public async Task AddressEndCode_IsolatesTheBadTagOnce_ThenCostsAtMostThreeRequests()
    {
        for (var i = 0; i < 100; i++)
        {
            _server.Memory[i] = (ushort)(i + 1);
        }

        _server.BadDevices[50] = 0;
        await using var driver = CreateDriver();
        var tags = Enumerable.Range(0, 100).Select(i => Tag($"t{i}", $"D{i}", PlcDataType.Int)).ToArray();

        var first = await driver.ReadAsync(tags, CancellationToken.None);

        Assert.Equal(101, _server.Requests.Count); // the block, then each tag alone, once
        AssertOnlyBad(first, 50);
        var denied = Assert.IsType<PlcAccessDeniedException>(first[50].Error);
        Assert.Contains("SLMP end code 0xC056 reading D50\u2013D50", denied.Message);
        Assert.Equal((short)100, first[99].Value);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var before = _server.Requests.Count;
            var next = await driver.ReadAsync(tags, CancellationToken.None);
            Assert.True(_server.Requests.Count - before <= 3);
            AssertOnlyBad(next, 50);
            Assert.Contains("0xC056", Assert.IsType<PlcAccessDeniedException>(next[50].Error).Message);
        }

        // The good tags are merged again around the bad one: D0-D49, D51-D99, and D50 alone.
        Assert.Equal(
            [(0, 50), (51, 49), (50, 1)],
            _server.Requests.Skip(_server.Requests.Count - 3).Select(r => (r.Head, r.Points)));

        _server.BadDevices.Clear();
        var recovered = await driver.ReadAsync(tags, CancellationToken.None);
        Assert.All(recovered, r => Assert.Null(r.Error));
        var afterRecovery = _server.Requests.Count;
        await driver.ReadAsync(tags, CancellationToken.None);
        Assert.Equal(afterRecovery + 1, _server.Requests.Count);
    }

    [Fact]
    public async Task OtherEndCode_GivesTheBlockTheErrorWithNoFanOut()
    {
        await using var driver = CreateDriver();
        var tags = Enumerable.Range(0, 20).Select(i => Tag($"t{i}", $"D{i}", PlcDataType.Int)).ToArray();
        _server.FailNextWith(0xCEE0);

        var results = await driver.ReadAsync(tags, CancellationToken.None);

        Assert.Single(_server.Requests);
        Assert.All(results, r => Assert.Contains("0xCEE0", Assert.IsType<PlcAccessDeniedException>(r.Error).Message));

        // Nothing was remembered: the next cycle is one block request again.
        var next = await driver.ReadAsync(tags, CancellationToken.None);
        Assert.All(next, r => Assert.Null(r.Error));
        Assert.Equal(2, _server.Requests.Count);
    }

    [Theory]
    [InlineData("M100", 100)]
    [InlineData("m7679", 7679)]
    [InlineData("X17", 15)]
    [InlineData("X20", 16)]
    [InlineData("Y0", 0)]
    [InlineData("Y1777", 1023)]
    public void BitSyntax_ParsesDecimalMAndOctalXY(string address, int number)
    {
        var parsed = Assert.IsType<SlmpAddress>(Syntax.Parse(address, PlcDataType.Bool));

        Assert.Equal(number, parsed.Number);
        Assert.True(parsed.IsBit);
        Assert.Equal(address.StartsWith('X') ? SlmpAddress.InputReadOnlyText : null, parsed.ReadOnlyReason);
    }

    [Theory]
    [InlineData("X8", PlcDataType.Bool)]
    [InlineData("Y19", PlcDataType.Bool)]
    [InlineData("X2000", PlcDataType.Bool)]
    [InlineData("M7680", PlcDataType.Bool)]
    [InlineData("M0", PlcDataType.Int)]
    [InlineData("X0", PlcDataType.Word)]
    [InlineData("Y0", PlcDataType.Real)]
    public void BitSyntax_RejectsOctalDigitsRangeAndNonBool(string address, PlcDataType type)
    {
        Assert.ThrowsAny<PlcException>(() => Syntax.Parse(address, type));
    }

    [Fact]
    public void BitSyntax_CanonicalSpellsXYInOctal()
    {
        Assert.Equal("X17", Syntax.Parse("x17", PlcDataType.Bool).Canonical);
        Assert.Equal("M96", Syntax.Parse("M96", PlcDataType.Bool).Canonical);
        Assert.Contains("'M0'", Assert.Throws<PlcValueException>(() => Syntax.Parse("M0", PlcDataType.Int)).Message);
    }

    [Fact]
    public async Task BitDeviceReadFrame_IsExact()
    {
        await using var driver = CreateDriver(timeoutMs: 3000);

        // [UNVERIFIED until checked against SH-081951 and a real FX5U] word-unit read of M96, 1 point:
        // same header as a D read; head 96 = 60 00 00; device code 90.
        byte[] expected =
        [
            0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x0C, 0x00, 0x0A, 0x00,
            0x01, 0x04, 0x00, 0x00, 0x60, 0x00, 0x00, 0x90, 0x01, 0x00,
        ];

        Assert.Equal(expected, driver.BuildDeviceReadWords(SlmpDevice.M, 96, 1));
        await driver.ReadAsync([Tag("a", "M96", PlcDataType.Bool)], CancellationToken.None);
        Assert.Equal(expected, Assert.Single(_server.Frames));
    }

    [Theory]
    [InlineData("M96", 0x0001)]
    [InlineData("M103", 0x0080)]
    [InlineData("M104", 0x0100)]
    [InlineData("M111", 0x8000)]
    [InlineData("M101", 0x0020)]
    public async Task BitDecoding_UsesWordHeadAndLittleEndianBytes(string address, int wordValue)
    {
        _server.BitWords[(0x90, 6)] = (ushort)wordValue; // device numbers 96..111
        await using var driver = CreateDriver();

        var results = await driver.ReadAsync(
            [Tag("on", address, PlcDataType.Bool), Tag("off", address == "M96" ? "M97" : "M96", PlcDataType.Bool)],
            CancellationToken.None);

        Assert.Equal(true, results[0].Value);
        Assert.Equal(false, results[1].Value);
        Assert.Equal([(96, 1)], _server.Requests.Select(r => (r.Head, r.Points)));
        Assert.Equal(0x90, Assert.Single(_server.Requests).Code);
    }

    [Fact]
    public async Task OctalXAndY_MapToTheirWordAndBit()
    {
        _server.SetBit(0x9C, 8, true);  // X10 octal
        _server.SetBit(0x9D, 16, true); // Y20 octal
        await using var driver = CreateDriver();

        var x10 = await driver.ReadAsync([Tag("x10", "X10", PlcDataType.Bool)], CancellationToken.None);
        var x20 = await driver.ReadAsync([Tag("x20", "X20", PlcDataType.Bool)], CancellationToken.None);
        var y20 = await driver.ReadAsync([Tag("y20", "Y20", PlcDataType.Bool)], CancellationToken.None);

        Assert.Equal(true, x10[0].Value);
        Assert.Equal(false, x20[0].Value);
        Assert.Equal(true, y20[0].Value);
        Assert.Equal(
            [(0x9C, 0, 1), (0x9C, 16, 1), (0x9D, 16, 1)],
            _server.Requests.Select(r => ((int)r.Code, r.Head, r.Points)));
    }

    [Fact]
    public async Task BitsOfTwoTouchingWords_AreOneRequest()
    {
        await using var driver = CreateDriver();
        var tags = Enumerable.Range(0, 32).Select(i => Tag($"m{i}", $"M{i}", PlcDataType.Bool)).ToArray();

        await driver.ReadAsync(tags, CancellationToken.None);

        Assert.Equal([(0x90, 0, 2)], _server.Requests.Select(r => ((int)r.Code, r.Head, r.Points)));
    }

    [Fact]
    public async Task DAndMInOneCycle_AreTwoRequests()
    {
        _server.Memory[0] = 3;
        _server.SetBit(0x90, 0, true);
        await using var driver = CreateDriver();

        var results = await driver.ReadAsync([Tag("d", "D0", PlcDataType.Int), Tag("m", "M0", PlcDataType.Bool)], CancellationToken.None);

        Assert.Equal((short)3, results[0].Value);
        Assert.Equal(true, results[1].Value);
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task BitDevice_AddressEndCodeIsolatesOnce_OtherCodesDoNot()
    {
        await using var driver = CreateDriver();
        ResolvedTag[] tags = [Tag("a", "M0", PlcDataType.Bool), Tag("b", "M16", PlcDataType.Bool), Tag("c", "M32", PlcDataType.Bool)];

        _server.FailNextWith(0xCEE0);
        var other = await driver.ReadAsync(tags, CancellationToken.None);
        Assert.All(other, r => Assert.IsType<PlcAccessDeniedException>(r.Error));
        Assert.Single(_server.Requests);

        _server.FailNextWith(0xC056);
        var address = await driver.ReadAsync(tags, CancellationToken.None);
        // The block, then each word alone: the fake only fails the first, so every tag reads Good.
        Assert.All(address, r => Assert.Null(r.Error));
        Assert.Equal(1 + 1 + 3, _server.Requests.Count);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        _server.Mode = FakeSlmpMode.BlackHole;
        await using var driver = CreateDriver(timeoutMs: 5000);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => driver.ReadAsync([Tag("a", "D0", PlcDataType.Int)], cts.Token));
    }

    private static void AssertOnlyBad(IReadOnlyList<PlcReadResult> results, int badIndex)
    {
        for (var i = 0; i < results.Count; i++)
        {
            Assert.Equal(i != badIndex, results[i].Error is null);
        }
    }

    [Theory]
    [InlineData(0xC059, "not supported")]
    [InlineData(0xC05C, "Request error")]
    [InlineData(0xC061, "SLMP end code 0xC061 reading D5")]
    public async Task EndCode_OnASingleTagBlock_NamesCodeRangeAndHint(int endCode, string expectedText)
    {
        await using var driver = CreateDriver();
        _server.FailNextWith((ushort)endCode);

        var results = await driver.ReadAsync([Tag("a", "D5", PlcDataType.Int)], CancellationToken.None);

        var denied = Assert.IsType<PlcAccessDeniedException>(results[0].Error);
        Assert.Contains($"0x{endCode:X4}", denied.Message);
        Assert.Contains("reading D5–D5", denied.Message);
        Assert.Contains(expectedText, denied.Message);
        Assert.Single(_server.Frames);
    }

    [Fact]
    public async Task Outage_IsOneConnectionAttemptAndOneRequestPerBatch()
    {
        _server.Mode = FakeSlmpMode.BlackHole;
        await using var driver = CreateDriver(timeoutMs: 1000);
        ResolvedTag[] tags = [Tag("a", "D0", PlcDataType.Int), Tag("b", "D10", PlcDataType.Int), Tag("c", "D20", PlcDataType.Int)];

        var stopwatch = Stopwatch.StartNew();
        var results = await driver.ReadAsync(tags, CancellationToken.None);

        Assert.True(stopwatch.ElapsedMilliseconds < 2500, $"batch took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(1, _server.Connections);
        Assert.Single(_server.Frames);
        var first = Assert.IsType<PlcUnavailableException>(results[0].Error);
        Assert.Same(first, results[1].Error);
        Assert.Same(first, results[2].Error);
    }

    [Fact]
    public async Task ShortReply_IsAProtocolError()
    {
        _server.Mode = FakeSlmpMode.ShortReply;
        await using var driver = CreateDriver();

        var results = await driver.ReadAsync([Tag("a", "D0", PlcDataType.DInt)], CancellationToken.None);

        var error = Assert.IsType<PlcUnavailableException>(results[0].Error);
        Assert.Contains("expected 4", error.Message);
    }

    [Fact]
    public async Task AsciiLookingReply_NamesTheBinarySetting()
    {
        _server.Mode = FakeSlmpMode.AsciiReply;
        await using var driver = CreateDriver();

        var results = await driver.ReadAsync([Tag("a", "D0", PlcDataType.Int)], CancellationToken.None);

        var error = Assert.IsType<PlcUnavailableException>(results[0].Error);
        Assert.Contains("D0 00", error.Message);
        Assert.Contains("Binary", error.Message);
    }

    [Fact]
    public async Task FragmentedReply_IsReassembled()
    {
        _server.Mode = FakeSlmpMode.RespondByteByByte;
        _server.Memory[0] = 7;
        await using var driver = CreateDriver(timeoutMs: 10000);

        var results = await driver.ReadAsync([Tag("a", "D0", PlcDataType.Int)], CancellationToken.None);

        Assert.Equal((short)7, results[0].Value);
    }

    [Fact]
    public async Task XWrite_IsDeniedBeforeAnyIo()
    {
        await using var driver = CreateDriver();

        var ex = await Assert.ThrowsAsync<PlcAccessDeniedException>(
            () => driver.WriteAsync(new SlmpAddress(SlmpDevice.X, 0), PlcDataType.Bool, true, CancellationToken.None));

        Assert.Contains(SlmpAddress.InputReadOnlyText, ex.Message);
        Assert.Equal(0, _server.Connections);
        Assert.Empty(_server.Frames);
    }

    [Fact]
    public async Task IsReachable_FollowsTheConnection()
    {
        await using var driver = CreateDriver();

        Assert.True(await driver.IsReachableAsync(CancellationToken.None));
        Assert.Empty(_server.Frames);
    }

    [Fact]
    public void WritableXTag_IsRejectedByTheCatalogWithTheReadOnlyText()
    {
        var options = new PlcTagCatalogOptions
        {
            Tags = [new PlcTagDefinition { TagId = "a", Address = "X0", DataType = PlcDataType.Bool, Writable = true }],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new PlcTagCatalog(options, Syntax));

        Assert.Contains(SlmpAddress.InputReadOnlyText, ex.Message);
    }

    [Theory]
    [InlineData("D0", PlcDataType.Bool)]
    [InlineData("D0", PlcDataType.Byte)]
    [InlineData("D8000", PlcDataType.Int)]
    [InlineData("D7999", PlcDataType.DInt)]
    [InlineData("DB1.DBW0", PlcDataType.Int)]
    [InlineData("D", PlcDataType.Int)]
    public void Syntax_RejectsWhatTheDriverCannotRead(string address, PlcDataType type)
    {
        Assert.ThrowsAny<PlcException>(() => Syntax.Parse(address, type));
    }

    [Fact]
    public void Syntax_NamesTheAddressAndTypeForBoolOnD()
    {
        var ex = Assert.Throws<PlcValueException>(() => Syntax.Parse("D0", PlcDataType.Bool));

        Assert.Contains("'D0'", ex.Message);
        Assert.Contains("Bool", ex.Message);
    }

    [Fact]
    public void Syntax_NormalizesCaseAndReportsReadOnly()
    {
        var address = Syntax.Parse("d007", PlcDataType.Real);

        Assert.Equal("D7", address.Canonical);
        Assert.False(address.IsBit);
        Assert.Null(address.ReadOnlyReason);
        Assert.Equal("D7999", Syntax.Parse("D7999", PlcDataType.Word).Canonical);
    }

    [Theory]
    [InlineData(0, 3000, "Edge:Plc:Port")]
    [InlineData(102, 3000, "102 is the S7 port; use the SLMP port set in GX Works3")]
    [InlineData(70000, 3000, "Edge:Plc:Port")]
    [InlineData(5000, 999, "Edge:Plc:TimeoutMs")]
    [InlineData(5000, 60001, "Edge:Plc:TimeoutMs")]
    public void Options_RejectBadPortAndTimeout(int port, int timeoutMs, string expected)
    {
        var result = new SlmpOptionsValidator().Validate(null, new SlmpOptions { Host = "h", Port = port, TimeoutMs = timeoutMs });

        Assert.True(result.Failed);
        Assert.Contains(expected, result.FailureMessage);
    }

    [Fact]
    public void Options_RequireHost_AndAcceptValidSettings()
    {
        var validator = new SlmpOptionsValidator();

        Assert.Contains("Edge:Plc:Host", validator.Validate(null, new SlmpOptions { Port = 5000 }).FailureMessage);
        Assert.True(validator.Validate(null, new SlmpOptions { Host = "h", Port = 5000 }).Succeeded);
    }

    [Fact]
    public async Task Host_StartsWithSlmp_AndPollsTheServer()
    {
        _server.Memory[0] = 5;
        await using var edge = EdgeTestHost.Create(SlmpSettings(), runAgentServices: true);

        await edge.Host.StartAsync();
        Assert.IsType<MitsubishiSlmpDriver>(edge.Services.GetRequiredService<PlcDeviceRegistry>().Devices[0].Driver!);
        var deadline = Stopwatch.StartNew();
        while (_server.Frames.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }

        await edge.Host.StopAsync();
        Assert.NotEmpty(_server.Frames);
    }

    [Fact]
    public async Task Host_FailsStartWithoutAPort()
    {
        var settings = SlmpSettings();
        settings["Edge:Plc:Port"] = null;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(settings));

        Assert.Contains("Edge:Plc:Port", ex.Message);
    }

    [Fact]
    public async Task Host_FailsStartOnTheS7Port()
    {
        var settings = SlmpSettings();
        settings["Edge:Plc:Port"] = "102";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(settings));

        Assert.Contains("102 is the S7 port", ex.Message);
    }

    [Fact]
    public async Task Host_FailsStartOnAWritableXTag()
    {
        var settings = SlmpSettings();
        settings["Edge:Tags:0:Address"] = "X0";
        settings["Edge:Tags:0:DataType"] = "Bool";
        settings["Edge:Tags:0:Writable"] = "true";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(settings));

        Assert.Contains(SlmpAddress.InputReadOnlyText, ex.Message);
    }

    [Fact]
    public async Task Host_FailsStartOnABoolTagOnD()
    {
        var settings = SlmpSettings();
        settings["Edge:Tags:0:DataType"] = "Bool";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(settings));

        Assert.Contains("D0", ex.Message);
        Assert.Contains("Bool", ex.Message);
    }

    private static async Task StartAsync(Dictionary<string, string?> settings)
    {
        await using var edge = EdgeTestHost.Create(settings, runAgentServices: true);
        await edge.Host.StartAsync();
    }

    // Overrides EdgeTestHost defaults: its TimeoutMs (500) is below the SLMP minimum, commands default on,
    // and its tag is an S7 address.
    private Dictionary<string, string?> SlmpSettings() => new()
    {
        ["Edge:Plc:Protocol"] = "Slmp",
        ["Edge:Plc:Port"] = _server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Edge:Plc:TimeoutMs"] = "1000",
        ["Edge:Commands:Enabled"] = "false",
        ["Edge:Tags:0:Address"] = "D0",
        ["Edge:Tags:0:DataType"] = "Int",
    };

    private static ResolvedTag Tag(string id, string address, PlcDataType type) =>
        new(id, Syntax.Parse(address, type), type, Writable: false, Min: null, Max: null);

    private MitsubishiSlmpDriver CreateDriver(int timeoutMs = 2000) =>
        new(Options.Create(new SlmpOptions { Host = "127.0.0.1", Port = _server.Port, TimeoutMs = timeoutMs }));
}
