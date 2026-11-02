using System.Reflection;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Mc1e;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// The FX3U (MC protocol, A-compatible 1E frame) driver against a fake adapter that enforces the FX3U-ENET-ADP User's
/// Manual (JY997D45801) limits. Golden frames are the manual's own examples.
/// </summary>
public sealed class Mc1eDriverTests : IAsyncLifetime
{
    private static readonly Mc1eAddressSyntax Syntax = new();
    private readonly FakeMc1eServer _server = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            // Every test: the driver sent only well-formed batch reads and writes.
            Assert.Empty(_server.UnexpectedFrames);
            Assert.All(_server.Commands, command => Assert.Contains(command, new byte[] { 0x00, 0x01, 0x02, 0x03 }));
        }
        finally
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReadFrames_AreTheManualsExamples()
    {
        await using var driver = CreateDriver(timeoutMs: 3000); // monitoring timer (3000 - 500) / 250 = 10 = 000AH

        // Section 7.5.5 (page 72): Y100 to Y137, two words; Y100 is 40H.
        Assert.Equal(
            [0x01, 0xFF, 0x0A, 0x00, 0x40, 0x00, 0x00, 0x00, 0x20, 0x59, 0x02, 0x00],
            driver.BuildBatchReadWords(Mc1eDevice.Y, 0x40, 2));

        // Sample program (page 169): "01FF000A4420000000000500", D0 to D4, in binary.
        Assert.Equal(
            [0x01, 0xFF, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x44, 0x05, 0x00],
            driver.BuildBatchReadWords(Mc1eDevice.D, 0, 5));
    }

    [Fact]
    public async Task WriteFrames_AreTheManualsExamples()
    {
        await using var driver = CreateDriver(timeoutMs: 3000);

        // Section 7.5.6 (page 76): D100 to D102 <- 1234H, 9876H, 0109H.
        Assert.Equal(
            [0x03, 0xFF, 0x0A, 0x00, 0x64, 0x00, 0x00, 0x00, 0x20, 0x44, 0x03, 0x00, 0x34, 0x12, 0x76, 0x98, 0x09, 0x01],
            driver.BuildBatchWriteWords(Mc1eDevice.D, 100, [0x1234, 0x9876, 0x0109]));

        // Section 7.5.3 (page 68): M50 to M61, a nibble per point, the first point high.
        Assert.Equal(
            [0x02, 0xFF, 0x0A, 0x00, 0x32, 0x00, 0x00, 0x00, 0x20, 0x4D, 0x0C, 0x00, 0x01, 0x11, 0x01, 0x00, 0x00, 0x01],
            driver.BuildBatchWriteBits(Mc1eDevice.M, 50, [false, true, true, true, false, true, false, false, false, false, false, true]));
    }

    [Fact]
    public async Task WrittenValues_ReadBack()
    {
        await using var driver = CreateDriver();

        await driver.WriteAsync(Syntax.Parse("D100", PlcDataType.Int), PlcDataType.Int, (short)1234, CancellationToken.None);
        await driver.WriteAsync(Syntax.Parse("D102", PlcDataType.DInt), PlcDataType.DInt, 123456, CancellationToken.None);
        await driver.WriteAsync(Syntax.Parse("D110", PlcDataType.String, 5), PlcDataType.String, "AB\0\0\0", CancellationToken.None);
        await driver.WriteAsync(Syntax.Parse("M5", PlcDataType.Bool), PlcDataType.Bool, true, CancellationToken.None);
        await driver.WriteAsync(Syntax.Parse("Y7", PlcDataType.Bool), PlcDataType.Bool, true, CancellationToken.None);

        var results = await driver.ReadAsync(
        [
            Tag("a", "D100", PlcDataType.Int),
            Tag("b", "D102", PlcDataType.DInt),
            new ResolvedTag("c", Syntax.Parse("D110", PlcDataType.String, 5), PlcDataType.String, false, null, null, Length: 5),
            Tag("d", "M5", PlcDataType.Bool),
            Tag("e", "Y7", PlcDataType.Bool),
            Tag("f", "M4", PlcDataType.Bool),
        ], CancellationToken.None);

        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal([(short)1234, 123456, "AB", true, true, false], results.Select(r => r.Value));
        Assert.Equal(
            [(FakeMc1eServer.D, 100, 1), (FakeMc1eServer.D, 102, 2), (FakeMc1eServer.D, 110, 3), (FakeMc1eServer.M, 5, 1), (FakeMc1eServer.Y, 7, 1)],
            _server.Writes);
    }

    [Fact]
    public async Task AnXWrite_IsRefusedBeforeAnyIo()
    {
        await using var driver = CreateDriver();

        var error = await Record.ExceptionAsync(() => driver.WriteAsync(Syntax.Parse("X10", PlcDataType.Bool), PlcDataType.Bool, true, CancellationToken.None));

        Assert.Contains("X inputs cannot be written", Assert.IsType<PlcAccessDeniedException>(error).Message, StringComparison.Ordinal);
        Assert.Equal(0, _server.Connections);
        Assert.Empty(_server.Frames);
    }

    [Fact]
    public async Task AWriteTheAdapterRefuses_IsAccessDenied_AndTheConnectionStaysInStep()
    {
        _server.FailNextWith(0x56);
        _server.Memory[0] = 7;
        await using var driver = CreateDriver();

        var error = await Record.ExceptionAsync(() => driver.WriteAsync(Syntax.Parse("D100", PlcDataType.Int), PlcDataType.Int, (short)1, CancellationToken.None));
        var read = await driver.ReadAsync([Tag("a", "D0", PlcDataType.Int)], CancellationToken.None);

        Assert.Contains("0x56", Assert.IsType<PlcAccessDeniedException>(error).Message, StringComparison.Ordinal);
        Assert.Equal((short)7, read[0].Value);
        Assert.Equal(1, _server.Connections);
        Assert.Empty(_server.Writes);
    }

    [Fact]
    public async Task AWriteWhoseReplyNeverComes_HasAnUnknownOutcome()
    {
        _server.DropNextWrite();
        await using var driver = CreateDriver();

        var error = await Record.ExceptionAsync(() => driver.WriteAsync(Syntax.Parse("D100", PlcDataType.Int), PlcDataType.Int, (short)1, CancellationToken.None));

        Assert.IsType<PlcWriteOutcomeUnknownException>(error);
    }

    [Fact]
    public void OnlyXIsReadOnly()
    {
        Assert.Equal("X inputs cannot be written", Syntax.Parse("X0", PlcDataType.Bool).ReadOnlyReason);
        Assert.Null(Syntax.Parse("Y0", PlcDataType.Bool).ReadOnlyReason);
        Assert.Null(Syntax.Parse("M0", PlcDataType.Bool).ReadOnlyReason);
        Assert.Null(Syntax.Parse("D0", PlcDataType.Int).ReadOnlyReason);
    }

    [Fact]
    public async Task TheManualsReply_DecodesBitsInOrder()
    {
        // Section 7.5.5: the reply 81 00 9D 82 3E 55 holds Y100-Y117 in 829DH and Y120-Y137 in 553EH.
        _server.BitWords[(FakeMc1eServer.Y, 4)] = 0x829D;
        _server.BitWords[(FakeMc1eServer.Y, 5)] = 0x553E;
        await using var driver = CreateDriver();

        var results = await driver.ReadAsync(
        [
            Tag("Y100", "Y100", PlcDataType.Bool),
            Tag("Y101", "Y101", PlcDataType.Bool),
            Tag("Y107", "Y107", PlcDataType.Bool),
            Tag("Y110", "Y110", PlcDataType.Bool),
            Tag("Y137", "Y137", PlcDataType.Bool),
        ], CancellationToken.None);

        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal([true, false, true, false, false], results.Select(r => r.Value));
        Assert.Equal([(FakeMc1eServer.Y, 0x40, 2)], _server.Requests);
    }

    [Fact]
    public async Task WordTypesAndString_DecodeLowWordFirst()
    {
        _server.Memory[0] = 0xFFFF; // Int -1
        _server.Memory[10] = 0xE240; // DInt 123456 = 0001E240H
        _server.Memory[11] = 0x0001;
        var real = BitConverter.SingleToUInt32Bits(12.5f);
        _server.Memory[20] = (ushort)real;
        _server.Memory[21] = (ushort)(real >> 16);
        _server.Memory[30] = 0x4241; // "AB"
        _server.Memory[31] = 0x0043; // "C" and a NUL
        await using var driver = CreateDriver();

        var results = await driver.ReadAsync(
        [
            Tag("I", "D0", PlcDataType.Int),
            Tag("L", "D10", PlcDataType.DInt),
            Tag("R", "D20", PlcDataType.Real),
            new ResolvedTag("S", Syntax.Parse("D30", PlcDataType.String, 4), PlcDataType.String, false, null, null, Length: 4),
        ], CancellationToken.None);

        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal([(short)-1, 123456, 12.5f, "ABC"], results.Select(r => r.Value));
    }

    [Fact]
    public async Task XNumbersAreOctal_X10IsTheNinthInput()
    {
        _server.SetBit(FakeMc1eServer.X, 8, true); // the ninth input
        await using var driver = CreateDriver();

        var results = await driver.ReadAsync([Tag("X10", "X10", PlcDataType.Bool), Tag("X7", "X7", PlcDataType.Bool)], CancellationToken.None);

        Assert.Equal([true, false], results.Select(r => r.Value));
    }

    [Fact]
    public async Task TouchingTagsShareARequest_AGapStartsAnother()
    {
        await using var driver = CreateDriver();

        await driver.ReadAsync([Tag("A", "D0", PlcDataType.Int), Tag("B", "D1", PlcDataType.Int)], CancellationToken.None);
        Assert.Equal([(FakeMc1eServer.D, 0, 2)], _server.Requests);

        await driver.ReadAsync([Tag("A", "D0", PlcDataType.Int), Tag("C", "D2", PlcDataType.Int)], CancellationToken.None);
        Assert.Equal([(FakeMc1eServer.D, 0, 2), (FakeMc1eServer.D, 0, 1), (FakeMc1eServer.D, 2, 1)], _server.Requests);
        Assert.Equal(2, driver.LastBatchRequests);
    }

    [Fact]
    public async Task TouchingWordsAboveTheManualsLimit_TakeTwoRequests()
    {
        await using var driver = CreateDriver();
        var registers = Enumerable.Range(0, 65).Select(i => Tag($"D{i}", $"D{i}", PlcDataType.Int));
        var relays = Enumerable.Range(0, 33).Select(i => Tag($"M{i * 16}", $"M{i * 16}", PlcDataType.Bool)); // 33 words

        var results = await driver.ReadAsync([.. registers, .. relays], CancellationToken.None);

        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal(
            [(FakeMc1eServer.D, 0, 64), (FakeMc1eServer.D, 64, 1), (FakeMc1eServer.M, 0, 32), (FakeMc1eServer.M, 512, 1)],
            _server.Requests);
    }

    [Fact]
    public async Task AnAddressCompleteCode_FailsOnlyTheRefusedTags()
    {
        _server.BadDevices[101] = 0;
        _server.Memory[100] = 7;
        _server.Memory[200] = 9;
        await using var driver = CreateDriver();

        var results = await driver.ReadAsync(
            [Tag("Good", "D100", PlcDataType.Int), Tag("Bad", "D101", PlcDataType.Int), Tag("Other", "D200", PlcDataType.Int)],
            CancellationToken.None);

        Assert.Equal((short)7, results[0].Value);
        var refused = Assert.IsType<PlcAccessDeniedException>(results[1].Error);
        Assert.Contains("0x56", refused.Message, StringComparison.Ordinal);
        Assert.Contains("D101", refused.Message, StringComparison.Ordinal);
        Assert.Equal((short)9, results[2].Value);
    }

    [Fact]
    public async Task APlcThatCannotProcess_FailsTheBlockWithItsAbnormalCode_WithoutFanningOut()
    {
        _server.FailNextWith(0x5B);
        await using var driver = CreateDriver();

        var results = await driver.ReadAsync([Tag("A", "D0", PlcDataType.Int), Tag("B", "D1", PlcDataType.Int)], CancellationToken.None);

        Assert.All(results, r => Assert.Contains("abnormal code 0x10", Assert.IsType<PlcAccessDeniedException>(r.Error).Message, StringComparison.Ordinal));
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task AReplyWithAnotherSubheader_DropsTheConnection_AndTheNextReadReconnects()
    {
        _server.Memory[0] = 7;
        _server.SendRawNext(0x80, 0x00, 0x07, 0x00); // a bit-read reply to a word read
        await using var driver = CreateDriver();

        var first = await driver.ReadAsync([Tag("A", "D0", PlcDataType.Int)], CancellationToken.None);
        var second = await driver.ReadAsync([Tag("A", "D0", PlcDataType.Int)], CancellationToken.None);

        Assert.IsType<PlcUnavailableException>(first[0].Error);
        Assert.Equal((short)7, second[0].Value);
        Assert.Equal(2, _server.Connections);
    }

    [Theory]
    [InlineData(new byte[] { 0x81, 0x5B, 0x10, 0x00 })] // PLC cannot process: abnormal code and 00H follow
    [InlineData(new byte[] { 0x81, 0x56 })] // wrong device: nothing follows
    public async Task AfterAnErrorReply_TheNextReadOnTheSameConnectionIsInStep(byte[] reply)
    {
        _server.Memory[0] = 7;
        _server.SendRawNext(reply);
        await using var driver = CreateDriver();

        var first = await driver.ReadAsync([Tag("A", "D0", PlcDataType.Int)], CancellationToken.None);
        var second = await driver.ReadAsync([Tag("A", "D0", PlcDataType.Int)], CancellationToken.None);

        Assert.IsType<PlcAccessDeniedException>(first[0].Error);
        Assert.Equal((short)7, second[0].Value);
        Assert.Equal(1, _server.Connections);
    }

    [Fact]
    public async Task A5BReplyWithoutItsTrailing00_IsRefusedAsInvalid()
    {
        _server.SendRawNext(0x81, 0x5B, 0x10, 0x81);
        await using var driver = CreateDriver();

        var results = await driver.ReadAsync([Tag("A", "D0", PlcDataType.Int)], CancellationToken.None);

        Assert.Contains("Invalid MC 1E reply", Assert.IsType<PlcUnavailableException>(results[0].Error).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoppedAdapter_FailsTheWholeBatchFromOneAttempt()
    {
        await using var driver = new MitsubishiMc1eDriver(Options.Create(new Mc1eOptions { Host = "127.0.0.1", Port = ClosedPort(), TimeoutMs = 1000 }));

        var results = await driver.ReadAsync(
            [Tag("A", "D0", PlcDataType.Int), Tag("B", "D100", PlcDataType.Int), Tag("C", "M0", PlcDataType.Bool)],
            CancellationToken.None);

        var first = Assert.IsType<PlcUnavailableException>(results[0].Error);
        Assert.All(results, r => Assert.Same(first, r.Error)); // the later blocks were not tried
    }

    [Fact]
    public async Task WritesThatCannotFit_AreRefusedBeforeAnyIo()
    {
        await using var driver = CreateDriver();

        var wrongType = await Record.ExceptionAsync(() => driver.WriteAsync(Syntax.Parse("M5", PlcDataType.Bool), PlcDataType.Int, (short)1, CancellationToken.None));
        // Built directly: the syntax already refuses "D7999 DInt", and this proves the driver's own fit check.
        var pastD7999 = await Record.ExceptionAsync(() => driver.WriteAsync(new Mc1eAddress(Mc1eDevice.D, 7999), PlcDataType.DInt, 1, CancellationToken.None));

        Assert.IsType<PlcValueException>(wrongType);
        Assert.IsType<PlcValueException>(pastD7999);
        Assert.Empty(_server.Frames);
        Assert.Equal(0, _server.Connections);
    }

    [Fact]
    public void TheCommandCodes_AreExactlyBatchReadAndBatchWrite()
    {
        var codes = typeof(Mc1eCommands)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (byte)f.GetRawConstantValue()!)
            .Order();

        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03 }, codes);
    }

    [Theory]
    [InlineData("X400", PlcDataType.Bool, null)] // octal: X377 is the last input
    [InlineData("X8", PlcDataType.Bool, null)]
    [InlineData("D8000", PlcDataType.Int, null)]
    [InlineData("M7680", PlcDataType.Bool, null)]
    [InlineData("D7999", PlcDataType.DInt, null)]
    [InlineData("D100", PlcDataType.LReal, null)]
    [InlineData("D100", PlcDataType.String, 129)] // one read reaches 64 registers
    [InlineData("M5", PlcDataType.Int, null)]
    public void AddressesOutsideTheFx3uRangesOrTypes_AreRefused(string address, PlcDataType type, int? length)
    {
        Assert.ThrowsAny<PlcException>(() => Syntax.Parse(address, type, length));
    }

    [Theory]
    [InlineData("X377", PlcDataType.Bool, null)]
    [InlineData("y0", PlcDataType.Bool, null)]
    [InlineData("M7679", PlcDataType.Bool, null)]
    [InlineData("D7998", PlcDataType.DInt, null)]
    [InlineData("D100", PlcDataType.String, 128)]
    public void AddressesWithinTheFx3uRanges_AreAccepted(string address, PlcDataType type, int? length)
    {
        Assert.Equal(address.ToUpperInvariant(), Syntax.Parse(address, type, length).Canonical);
    }

    private MitsubishiMc1eDriver CreateDriver(int timeoutMs = 2000) =>
        new(Options.Create(new Mc1eOptions { Host = "127.0.0.1", Port = _server.Port, TimeoutMs = timeoutMs }));

    private static ResolvedTag Tag(string id, string address, PlcDataType type) =>
        new(id, Syntax.Parse(address, type), type, Writable: false, Min: null, Max: null);

    private static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
