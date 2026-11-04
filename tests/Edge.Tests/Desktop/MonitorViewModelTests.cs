using System.Collections.Specialized;
using System.Text.Json;
using IndustrialIot.EdgeDesktop.Core;
using Microsoft.Extensions.Time.Testing;

namespace IndustrialIot.Edge.Tests.Desktop;

/// <summary>The texts and rules of <c>src/Edge/Monitoring/monitor.html</c>, rendered by the desktop view model.</summary>
public class MonitorViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SinglePlc_ShowsStateDetailAndCycle()
    {
        var vm = NewViewModel();

        vm.Apply(Single(lastCycle: Now.AddMilliseconds(-1500)));

        Assert.Equal("e1", vm.EdgeId);
        Assert.Equal("Đọc tốt", vm.PlcText);
        Assert.Equal(BadgeKind.Ok, vm.PlcKind);
        Assert.Equal("S7 · 192.168.0.1:102 · Rack 0 / Slot 1", vm.PlcDetail);
        Assert.Equal("09:59:58.500", vm.LastCycle);
        Assert.Equal("1.5 giây trước · đọc 12.5 ms · chu kỳ 1000 ms", vm.PollDetail);
    }

    [Fact]
    public void SinglePlc_WithoutRackSlot_AndNoCycleYet()
    {
        var vm = NewViewModel();

        vm.Apply(Single(lastCycle: null, plc: new PlcInfo("Slmp", "10.0.0.5", 5000, null, null, 3000, "waiting")));

        Assert.Equal("Chưa có dữ liệu", vm.PlcText);
        Assert.Equal(BadgeKind.Idle, vm.PlcKind);
        Assert.Equal("Slmp · 10.0.0.5:5000", vm.PlcDetail);
        Assert.Equal("—", vm.LastCycle);
        Assert.Equal("Chu kỳ 1000 ms", vm.PollDetail);
    }

    [Theory]
    [InlineData("ok", "Đọc tốt", BadgeKind.Ok)]
    [InlineData("partial", "Có tag lỗi", BadgeKind.Warn)]
    [InlineData("unavailable", "Mất kết nối", BadgeKind.Bad)]
    [InlineData("waiting", "Chưa có dữ liệu", BadgeKind.Idle)]
    [InlineData("other", "other", BadgeKind.Idle)]
    public void SinglePlc_StateWords(string state, string text, BadgeKind kind)
    {
        var vm = NewViewModel();

        vm.Apply(Single(lastCycle: Now, plc: Plc(state)));

        Assert.Equal(text, vm.PlcText);
        Assert.Equal(kind, vm.PlcKind);
    }

    [Fact]
    public void SinglePlc_IsStale_OnlyPastThreeIntervalsPlusTimeout_OnTheServersClock()
    {
        // 3 × 1000 ms + 3000 ms timeout = 6000 ms.
        var atLimit = NewViewModel();
        atLimit.Apply(Single(lastCycle: Now.AddMilliseconds(-6000)));
        Assert.Equal("Đọc tốt", atLimit.PlcText);

        var past = NewViewModel();
        past.Apply(Single(lastCycle: Now.AddMilliseconds(-6001)));
        Assert.Equal("Dữ liệu cũ", past.PlcText);
        Assert.Equal(BadgeKind.Warn, past.PlcKind);

        // The app's own clock is a day ahead: only the server's nowUtc counts.
        var skewed = NewViewModel(new FakeTimeProvider(Now.AddDays(1)));
        skewed.Apply(Single(lastCycle: Now.AddMilliseconds(-100)));
        Assert.Equal("Đọc tốt", skewed.PlcText);
    }

    [Fact]
    public void MultiPlc_CountsUnavailable_AndIsNeverStale()
    {
        var vm = NewViewModel();

        vm.Apply(Multi(lastCycle: Now.AddHours(-5), Device("A", "connected"), Device("B", "unavailable")));

        Assert.Equal("1 PLC mất kết nối", vm.PlcText);
        Assert.Equal(BadgeKind.Bad, vm.PlcKind);
        Assert.Equal("2 PLC", vm.PlcDetail);
        Assert.Equal("18000.0 giây trước · từng PLC ở bảng dưới", vm.PollDetail);

        vm.Apply(Multi(lastCycle: Now.AddHours(-5), Device("A", "connected"), Device("B", "disabled")));
        Assert.Equal("Đọc tốt", vm.PlcText);
        Assert.Equal(BadgeKind.Ok, vm.PlcKind);
    }

    [Fact]
    public void MultiPlc_NoCycleYet_HasNoPollDetail()
    {
        var vm = NewViewModel();

        vm.Apply(Multi(lastCycle: null, Device("A", "waiting")));

        Assert.Equal("—", vm.LastCycle);
        Assert.Equal("", vm.PollDetail);
    }

    [Fact]
    public void DeviceRows_UseTheWebsWordsAndFormats()
    {
        var vm = NewViewModel();

        vm.Apply(Multi(
            lastCycle: Now,
            new DeviceInfo("A", "S7-1200", "10.0.0.1", 102, "connected", Now.AddMilliseconds(-250), null, 12.5, 2),
            new DeviceInfo("B", "FX5U", "10.0.0.2", 5000, "unavailable", null, "timeout", null, null),
            Device("C", "waiting"),
            Device("D", "disabled"),
            Device("E", "strange")));

        var a = vm.Devices[0];
        Assert.Equal(new DeviceRow("A", "S7-1200", "10.0.0.1:102", "Đang đọc", BadgeKind.Ok, "09:59:59.750", "12.5 ms", "2", ""), a);
        Assert.Equal(new DeviceRow("B", "FX5U", "10.0.0.2:5000", "Mất kết nối", BadgeKind.Bad, "—", "—", "—", "timeout"), vm.Devices[1]);
        Assert.Equal(["Chưa có dữ liệu", "Đã tắt", "strange"], vm.Devices.Skip(2).Select(d => d.State));
        Assert.Equal([BadgeKind.Idle, BadgeKind.Idle, BadgeKind.Idle], vm.Devices.Skip(2).Select(d => d.StateKind));
    }

    [Fact]
    public void TagRows_FormatValuesQualityAndAccess()
    {
        var vm = NewViewModel();

        vm.Apply(Multi(lastCycle: Now, [Device("A", "connected")],
        [
            Tag("A.Run", "Bool", "true", "good", writable: true),
            Tag("A.Stop", "Bool", "false", "bad", error: "read failed"),
            Tag("A.Speed", "Int", "250", "uncertain"),
            Tag("A.Temp", "Real", "25.5", "good"),
            Tag("A.Name", "String", "\"Line 1\"", "good"),
            Tag("A.None", "Int", null, null),
            Tag("A.Odd", "Int", "1", "weird"),
        ]));

        Assert.Equal(["TRUE", "FALSE", "250", "25.5", "Line 1", "—", "1"], vm.Tags.Select(t => t.Value));
        Assert.Equal(["Tốt", "Lỗi", "Không chắc", "Tốt", "Tốt", "—", "weird"], vm.Tags.Select(t => t.Quality));
        Assert.Equal(
            [BadgeKind.Ok, BadgeKind.Bad, BadgeKind.Warn, BadgeKind.Ok, BadgeKind.Ok, BadgeKind.Idle, BadgeKind.Idle],
            vm.Tags.Select(t => t.QualityKind));
        Assert.Equal(["Cho phép", "Chỉ đọc"], vm.Tags.Take(2).Select(t => t.Access));
        Assert.Equal(["", "read failed"], vm.Tags.Take(2).Select(t => t.Error));
        Assert.Equal(new TagRow("A.Run", "A", "DB1.DBX0.0", "Bool", "TRUE", "Tốt", BadgeKind.Ok, "10:00:00.000", "Cho phép", ""), vm.Tags[0]);
        Assert.Equal("—", vm.Tags[5].Time);
    }

    [Fact]
    public void MqttAndBuffer_Cards()
    {
        var vm = NewViewModel();

        vm.Apply(Single(lastCycle: Now) with { Mqtt = new MqttInfo("broker.local", 8883, true), BufferPending = 1234567 });
        Assert.Equal("Đã kết nối", vm.MqttText);
        Assert.Equal(BadgeKind.Ok, vm.MqttKind);
        Assert.Equal("broker.local:8883", vm.MqttDetail);
        Assert.Equal("1.234.567", vm.Pending);

        vm.Apply(Single(lastCycle: Now) with { Mqtt = new MqttInfo("broker.local", 8883, false), BufferPending = null });
        Assert.Equal("Chưa kết nối", vm.MqttText);
        Assert.Equal(BadgeKind.Warn, vm.MqttKind);
        Assert.Equal("?", vm.Pending);
    }

    [Fact]
    public void Failure_ShowsTheBanner_KeepsTheRows_AndASuccessClearsIt()
    {
        var vm = NewViewModel();
        vm.Apply(Multi(lastCycle: Now, Device("A", "connected")));

        vm.ShowFailure("không kết nối được");

        Assert.Equal("Không lấy được dữ liệu từ phần mềm Edge (không kết nối được). Service IndustrialIot.Edge có đang chạy không?", vm.Banner);
        Assert.Single(vm.Devices);

        vm.Apply(Multi(lastCycle: Now, Device("A", "connected")));
        Assert.Null(vm.Banner);
    }

    [Fact]
    public void Updated_IsTheAppsLocalTimeOfTheLastSuccess()
    {
        var vm = NewViewModel(new FakeTimeProvider(Now.AddSeconds(7)));

        vm.Apply(Single(lastCycle: Now));

        Assert.Equal("Cập nhật 10:00:07", vm.Updated);
    }

    [Fact]
    public void SameRows_RaiseNoCollectionChange_AndAChangedRowIsReplacedInPlace()
    {
        var vm = NewViewModel();
        vm.Apply(Multi(lastCycle: Now, Device("A", "connected"), Device("B", "connected")));
        var changes = new List<NotifyCollectionChangedAction>();
        vm.Devices.CollectionChanged += (_, e) => changes.Add(e.Action);

        vm.Apply(Multi(lastCycle: Now, Device("A", "connected"), Device("B", "connected")));
        Assert.Empty(changes);

        vm.Apply(Multi(lastCycle: Now, Device("A", "connected"), Device("B", "unavailable")));
        Assert.Equal([NotifyCollectionChangedAction.Replace], changes);

        changes.Clear();
        vm.Apply(Multi(lastCycle: Now, Device("A", "connected")));
        Assert.Equal([NotifyCollectionChangedAction.Remove], changes);
    }

    [Fact]
    public async Task Run_FailsThenRecovers_EveryChangeThroughThePostCallback()
    {
        var time = new FakeTimeProvider(Now);
        var vm = NewViewModel(time);
        var answers = new Queue<Func<HttpResponseMessage>>(
        [
            () => throw new HttpRequestException("refused"),
            () => StatusClientTests.Json(StatusClientTests.MinimalStatus),
        ]);
        var handler = new StatusClientTests.RecordingHandler((_, _) => Task.FromResult(answers.Dequeue()()));
        using var client = new StatusClient(5099, handler);
        var posted = new Queue<Action>();
        using var cts = new CancellationTokenSource();

        var run = vm.RunAsync(client, posted.Enqueue, cts.Token);

        await WaitUntilAsync(() => posted.Count == 1);
        Assert.Null(vm.Banner); // nothing changes until the UI runs the posted action
        posted.Dequeue()();
        Assert.Contains("không kết nối được", vm.Banner, StringComparison.Ordinal);

        Assert.Single(handler.Requests); // the next poll waits 1 s after this one finished
        // The loop may still be about to start its 1 s timer: advance in steps until the next poll has posted.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (posted.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(10);
        }

        Assert.Equal(2, handler.Requests.Count);
        posted.Dequeue()();
        Assert.Null(vm.Banner);
        Assert.Equal("e1", vm.EdgeId);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Cancel_DuringARequest_ChangesNothing()
    {
        var vm = NewViewModel();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StatusClientTests.RecordingHandler(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return StatusClientTests.Json(StatusClientTests.MinimalStatus);
        });
        using var client = new StatusClient(5099, handler, TimeSpan.FromMinutes(1));
        var posted = new List<Action>();
        using var cts = new CancellationTokenSource();

        var run = vm.RunAsync(client, posted.Add, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(posted);
        Assert.Null(vm.Banner);
    }

    [Fact]
    public async Task Cancel_BeforeThePostedActionRuns_ChangesNothing()
    {
        var vm = NewViewModel();
        var handler = new StatusClientTests.RecordingHandler((_, _) => Task.FromResult(StatusClientTests.Json(StatusClientTests.MinimalStatus)));
        using var client = new StatusClient(5099, handler);
        var posted = new Queue<Action>();
        using var cts = new CancellationTokenSource();

        var run = vm.RunAsync(client, posted.Enqueue, cts.Token);
        await WaitUntilAsync(() => posted.Count == 1);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        posted.Dequeue()();

        Assert.Equal("", vm.EdgeId);
        Assert.Null(vm.Banner);
    }

    private static MonitorViewModel NewViewModel(FakeTimeProvider? time = null)
    {
        time ??= new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return new MonitorViewModel(time);
    }

    private static PlcInfo Plc(string state = "ok") => new("S7", "192.168.0.1", 102, 0, 1, 3000, state);

    private static EdgeStatus Single(DateTimeOffset? lastCycle, PlcInfo? plc = null) => new(
        "e1", Now, 1000, lastCycle, 12.5, plc ?? Plc(), [Device("Plc1", "connected")], new MqttInfo("broker", 8883, false), 0, [],
        new SettingsApplyInfo("r", null, "applied", null));

    private static EdgeStatus Multi(DateTimeOffset? lastCycle, params DeviceInfo[] devices) => Multi(lastCycle, devices, []);

    private static EdgeStatus Multi(DateTimeOffset? lastCycle, DeviceInfo[] devices, TagInfo[] tags) => new(
        "e1", Now, null, lastCycle, null, null, devices, new MqttInfo("broker", 8883, false), 0, tags,
        new SettingsApplyInfo("r", null, "applied", null));

    private static DeviceInfo Device(string name, string state) => new(name, "S7-1200", "10.0.0.1", 102, state, null, null, null, null);

    private static TagInfo Tag(string tagId, string type, string? valueJson, string? quality, bool writable = false, string? error = null) => new(
        tagId, "A", "DB1.DBX0.0", type, writable,
        valueJson is null ? null : JsonDocument.Parse(valueJson).RootElement.Clone(),
        quality, valueJson is null ? null : Now, error);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }
}
