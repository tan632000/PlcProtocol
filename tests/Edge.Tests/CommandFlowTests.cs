using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Messaging;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialIot.Edge.Tests;

/// <summary>Commands through a real (in-process, TLS) broker with every Edge service running.</summary>
public class CommandFlowTests
{
    private const string CommandsTopic = "iiot/test-edge/commands";
    private const string ResultsTopic = "iiot/test-edge/command-results";

    [Fact]
    public async Task SlmpCommands_ThroughTheRealExecutorAndDriver_WriteDOnly_AndNeverX()
    {
        await using var plc = new FakeSlmpServer();
        await using var edge = EdgeTestHost.Create(new Dictionary<string, string?>
        {
            ["Edge:Plc:Protocol"] = "Slmp",
            ["Edge:Plc:Port"] = plc.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Edge:Plc:TimeoutMs"] = "1000",
            ["Edge:Tags:0:TagId"] = "Press.Setpoint",
            ["Edge:Tags:0:Address"] = "D100",
            ["Edge:Tags:0:DataType"] = "Int",
            ["Edge:Tags:0:Writable"] = "true",
            ["Edge:Tags:1:TagId"] = "Press.Start",
            ["Edge:Tags:1:Address"] = "X0",
            ["Edge:Tags:1:DataType"] = "Bool",
        });
        await edge.Host.StartAsync();
        var executor = edge.Services.GetRequiredService<Commands.CommandExecutor>();

        var applied = await ExecuteAsync(executor, Command("1234") with { TagId = "Press.Setpoint" });
        var refused = await ExecuteAsync(executor, Command("true") with { TagId = "Press.Start" });

        Assert.Equal(CommandOutcome.Applied, applied.Outcome);
        Assert.Equal(1234, plc.Memory[100]);
        Assert.Equal((CommandOutcome.Rejected, CommandReasons.InputAreaNotWritable), (refused.Outcome, refused.Reason));
        Assert.Equal([(0xA8, 100, 1)], plc.Writes.Select(w => ((int)w.Code, w.Head, w.Points)));
        Assert.Empty(plc.UnexpectedFrames);
    }

    [Fact]
    public async Task AnFx5uWithWritesDisabled_RefusesWithoutAnyFrame()
    {
        await using var plc = new FakeSlmpServer();
        await using var edge = EdgeTestHost.Create(EdgeTestHost.MultiDevice(new Dictionary<string, string?>
        {
            ["Edge:Channels:0:Name"] = "Fx",
            ["Edge:Channels:0:Driver"] = "Slmp3E",
            ["Edge:Channels:0:Devices:0:Name"] = "Press",
            ["Edge:Channels:0:Devices:0:Model"] = "FX5U",
            ["Edge:Channels:0:Devices:0:Host"] = "127.0.0.1",
            ["Edge:Channels:0:Devices:0:Port"] = plc.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Edge:Channels:0:Devices:0:TimeoutMs"] = "1000",
            ["Edge:Channels:0:Devices:0:Tags:0:Name"] = "Setpoint",
            ["Edge:Channels:0:Devices:0:Tags:0:Address"] = "D100",
            ["Edge:Channels:0:Devices:0:Tags:0:DataType"] = "Int",
            ["Edge:Channels:0:Devices:0:Tags:0:Writable"] = "true",
        }));
        await edge.Host.StartAsync();
        var executor = edge.Services.GetRequiredService<Commands.CommandExecutor>();

        var result = await ExecuteAsync(executor, Command("1234") with { TagId = "Press.Setpoint" });

        Assert.Equal((CommandOutcome.Rejected, CommandReasons.DeviceWritesDisabled), (result.Outcome, result.Reason));
        Assert.Empty(plc.Frames);
    }

    private static async Task<CommandResult> ExecuteAsync(Commands.CommandExecutor executor, DeviceCommand command)
    {
        var disposition = await executor.ExecuteAsync(Encoding.UTF8.GetBytes(ContractJson.Serialize(command)), default);
        return ContractJson.Deserialize<CommandResult>(disposition.ResultJson!);
    }

    [Fact]
    public async Task Command_EndToEnd_Applied()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var driver = new FakePlcDriver();
        await using var edge = await StartEdgeAsync(broker, driver);

        var command = Command("42");
        await broker.PublishAsync(CommandsTopic, ContractJson.Serialize(command));

        await WaitUntilAsync(() => Results(broker).Count >= 1, "a result");

        // At-least-once: the replay loop may re-send a result the handler is still publishing; every copy is identical.
        AssertIdenticalResultPayloads(broker);
        var result = Assert.Single(Results(broker).DistinctBy(r => r.CommandId));
        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);

        var write = Assert.Single(driver.Writes);
        Assert.Equal("DB1.DBW2", write.Address.Canonical);
        Assert.Equal((short)42, write.Value);
    }

    [Fact]
    public async Task OfflineQueuedCommand_AnsweredAfterStart()
    {
        await using var broker = await TestMqttBroker.StartAsync();

        // A first run creates the persistent session and its subscription.
        await using (var first = await StartEdgeAsync(broker, new FakePlcDriver()))
        {
            await WaitUntilAsync(() => first.Services.GetRequiredService<MqttConnection>().IsConnected, "first connection");
        }

        var command = Command("7");
        await broker.PublishAsync(CommandsTopic, ContractJson.Serialize(command)); // queued while the Edge is offline

        var driver = new FakePlcDriver();
        await using var second = await StartEdgeAsync(broker, driver);

        await WaitUntilAsync(() => Results(broker).Any(r => r.CommandId == command.CommandId), "result of the queued command");
        AssertIdenticalResultPayloads(broker);
        Assert.Equal(CommandOutcome.Applied, Results(broker).First(r => r.CommandId == command.CommandId).Outcome);
        Assert.Single(driver.Writes);
    }

    [Fact]
    public async Task ResultPublishFailure_RepublishedAfterReconnect()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var driver = new FakePlcDriver();
        await using var edge = await StartEdgeAsync(broker, driver);
        var connection = edge.Services.GetRequiredService<MqttConnection>();
        await WaitUntilAsync(() => connection.IsConnected, "connection");

        var command = Command("9");
        driver.Block();
        await broker.PublishAsync(CommandsTopic, ContractJson.Serialize(command));
        await driver.GateEntered.WaitAsync(TimeSpan.FromSeconds(10));

        // The broker goes away while the PLC write is in progress, so the result cannot be published.
        await broker.StopServerAsync();
        await WaitUntilAsync(() => !connection.IsConnected, "Edge notices the outage");
        driver.Release();
        await Task.Delay(500);

        await broker.StartServerAsync();
        await WaitUntilAsync(() => Results(broker).Any(r => r.CommandId == command.CommandId), "result after reconnect");

        var payloads = broker.Received.Where(m => m.Topic == ResultsTopic).Select(m => Encoding.UTF8.GetString(m.Payload)).ToList();
        Assert.All(payloads, p => Assert.Equal(payloads[0], p)); // at-least-once, identical payloads
        Assert.Equal(CommandOutcome.Applied, Results(broker)[0].Outcome);
        Assert.Single(driver.Writes);
    }

    private static async Task<EdgeTestHost> StartEdgeAsync(TestMqttBroker broker, FakePlcDriver driver)
    {
        var settings = broker.EdgeSettings();
        settings["Edge:Tags:0:TagId"] = "Speed";
        settings["Edge:Tags:0:Address"] = "DB1.DBW2";
        settings["Edge:Tags:0:DataType"] = "Int";
        settings["Edge:Tags:0:Writable"] = "true";
        settings["Edge:Poll:IntervalMs"] = "3600000"; // keep telemetry out of the way

        var edge = EdgeTestHost.Create(settings, services => services.UseDriver(driver), runAgentServices: true);
        await edge.Host.StartAsync();

        // Signalled only after the subscriptions are in place (IsConnected is already true before SUBACK).
        await edge.Services.GetRequiredService<MqttConnection>().WaitForConnectionAsync(default).WaitAsync(TimeSpan.FromSeconds(15));
        return edge;
    }

    private static DeviceCommand Command(string valueJson) => new()
    {
        SchemaVersion = 1,
        CommandId = Guid.NewGuid(),
        EdgeId = "test-edge",
        TagId = "Speed",
        TargetValue = JsonDocument.Parse(valueJson).RootElement.Clone(),
        IssuedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        IssuedBy = "test-operator",
    };

    /// <summary>Duplicates of a result are allowed (at-least-once), but only as byte-identical copies per command.</summary>
    private static void AssertIdenticalResultPayloads(TestMqttBroker broker)
    {
        var payloads = broker.Received.Where(m => m.Topic == ResultsTopic).Select(m => Encoding.UTF8.GetString(m.Payload)).ToList();
        foreach (var copies in payloads.GroupBy(p => ContractJson.Deserialize<CommandResult>(p).CommandId))
        {
            Assert.All(copies, p => Assert.Equal(copies.First(), p));
        }
    }

    private static List<CommandResult> Results(TestMqttBroker broker) => broker.Received
        .Where(m => m.Topic == ResultsTopic)
        .Select(m => ContractJson.Deserialize<CommandResult>(Encoding.UTF8.GetString(m.Payload)))
        .ToList();

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), $"timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }
}
