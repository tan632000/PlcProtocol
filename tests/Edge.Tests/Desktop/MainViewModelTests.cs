using System.Net;
using System.Text;
using System.Text.Json;
using IndustrialIot.EdgeDesktop.Core;
using Microsoft.Extensions.Time.Testing;
using static IndustrialIot.Edge.Tests.Desktop.StatusClientTests;

namespace IndustrialIot.Edge.Tests.Desktop;

/// <summary>
/// The main window's view model: the tree Channel → PLC with the state texts of the PLC table, the selected PLC's tags
/// with live values matched by TagId, the event log, the status bar and title, and the commands that open the dialogs
/// on the draft. Statuses are applied to the monitor view model as its poll loop would.
/// </summary>
public class MainViewModelTests
{
    private const string Password = "operator-password-1";
    private const int Port = 5099;

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Load_ShowsTheTreeAndTheSelectedPlcsTagsWithLiveValues()
    {
        var app = await StartAsync();
        app.Monitor.Apply(Status());

        var vm = app.Main;
        Assert.Equal(TreeNodeKind.AddChannel, vm.Tree[0].Kind);
        Assert.Equal("Bấm để thêm Channel", vm.Tree[0].Text);
        Assert.Equal(["Siemens (Siemens S7)", "Mitsu (Mitsubishi SLMP 3E)"], vm.Tree.Skip(1).Select(n => n.Text));
        Assert.Equal(["Line", "Press"], vm.Tree[1].Children.Select(n => n.Text));
        Assert.Equal("Chọn một PLC để xem tag", vm.GridPlaceholder);
        Assert.Empty(vm.Tags);

        vm.SelectedNode = Node(vm, "Line");

        Assert.Null(vm.GridPlaceholder);
        var speed = vm.Tags[0];
        Assert.Equal(("Speed", "DB1.DBW2", "Int", "Read Only", "12", "Tốt", BadgeKind.Ok, "10:00:00.000", ""),
            (speed.Name, speed.Address, speed.DataType, speed.Access, speed.Value, speed.Quality, speed.QualityKind, speed.Time, speed.Error));
        Assert.Equal(("Temp", "Read/Write", "21.5"), (vm.Tags[1].Name, vm.Tags[1].Access, vm.Tags[1].Value));

        // A PLC with LegacyTagIds keeps the bare tag name as its TagId.
        vm.SelectedNode = Node(vm, "Press");
        Assert.Equal("5", vm.Tags.Single().Value);
    }

    [Fact]
    public async Task Values_FollowEachStatus()
    {
        var app = await StartAsync();
        app.Monitor.Apply(Status());
        app.Main.SelectedNode = Node(app.Main, "Line");

        app.Monitor.Apply(Status(tags: [Tag("Line.Speed", "Line", "13"), Tag("Line.Temp", "Line", "22")]));

        Assert.Equal(["13", "22"], app.Main.Tags.Select(t => t.Value));
    }

    [Fact]
    public async Task PlcState_TextsOfThePlcTable()
    {
        var app = await StartAsync();
        Assert.Equal("…", Node(app.Main, "Line").State); // no status yet

        app.Monitor.Apply(Status(devices: [Device("Line", "connected"), Device("Press", "disabled"), Device("Fx", "unavailable")]));
        Assert.Equal(("Đang đọc", BadgeKind.Ok), (Node(app.Main, "Line").State, Node(app.Main, "Line").StateKind));
        Assert.Equal(("Đã tắt", BadgeKind.Idle), (Node(app.Main, "Press").State, Node(app.Main, "Press").StateKind));
        Assert.Equal(("Mất kết nối", BadgeKind.Bad), (Node(app.Main, "Fx").State, Node(app.Main, "Fx").StateKind));

        app.Monitor.Apply(Status(devices: [Device("Line", "waiting"), Device("Press", "connected"), Device("Fx", "connected")]));
        Assert.Equal("Chưa có dữ liệu", Node(app.Main, "Line").State);
    }

    [Fact]
    public async Task PlcState_ATagNotGoodOfThatPlc_ShowsCóTagLỗi()
    {
        var app = await StartAsync();

        // Only Press has a bad tag: Press says "Có tag lỗi", Line keeps "Đang đọc".
        app.Monitor.Apply(Status(tags: [Tag("Line.Speed", "Line", "12"), Tag("Count", "Press", null, "bad")]));

        Assert.Equal(("Có tag lỗi", BadgeKind.Warn), (Node(app.Main, "Press").State, Node(app.Main, "Press").StateKind));
        Assert.Equal("Đang đọc", Node(app.Main, "Line").State);

        app.Monitor.Apply(Status(tags: [Tag("Line.Speed", "Line", "12", "uncertain"), Tag("Count", "Press", "5")]));
        Assert.Equal("Có tag lỗi", Node(app.Main, "Line").State);
        Assert.Equal("Đang đọc", Node(app.Main, "Press").State);
    }

    [Fact]
    public async Task DraftOnly_PlcShowsChưaLưu_AndItsTagsADash()
    {
        var app = await StartAsync(dialog: async request =>
        {
            var plc = (DeviceDialogViewModel)request.ViewModel;
            plc.Name = "New";
            plc.Host = "10.0.0.9";
            Assert.True(request.Ok());
            await Task.CompletedTask;
        });
        app.Monitor.Apply(Status());
        app.Main.SelectedNode = app.Main.Tree[1];

        await app.Main.AddDeviceAsync();

        Assert.Equal("Chưa lưu", Node(app.Main, "New").State);
    }

    [Fact]
    public async Task DraftOnly_TagShowsADash_NotAnotherTagsValue()
    {
        var app = await StartAsync(dialog: request =>
        {
            var tag = (TagDialogViewModel)request.ViewModel;
            tag.Name = "Extra";
            tag.Address = "DB1.DBW8";
            Assert.True(request.Ok());
            return Task.CompletedTask;
        });
        app.Monitor.Apply(Status());
        app.Main.SelectedNode = Node(app.Main, "Line");

        await app.Main.AddTagAsync();

        // The third row of the grid; the status's third tag is Press's Count (5).
        var extra = app.Main.Tags[2];
        Assert.Equal(("Extra", "—", "—", "—"), (extra.Name, extra.Value, extra.Quality, extra.Time));
        Assert.Equal(["12", "21.5"], app.Main.Tags.Take(2).Select(t => t.Value));
    }

    [Fact]
    public async Task EdgeLostAndBack_LoggedOnceEach_ValuesKept()
    {
        var app = await StartAsync();
        app.Monitor.Apply(Status());
        app.Main.SelectedNode = Node(app.Main, "Line");
        var before = app.Main.Log.Count;

        app.Monitor.ShowFailure("không kết nối được");
        app.Monitor.ShowFailure("quá 2 giây không trả lời");
        app.Monitor.ShowFailure("không kết nối được");

        Assert.Equal(before + 1, app.Main.Log.Count);
        Assert.Equal(("Edge", "Mất kết nối tới Edge"), (app.Main.Log[0].Source, app.Main.Log[0].Message));
        Assert.True(app.Main.EdgeFieldIsError);
        Assert.Equal(app.Monitor.Banner, app.Main.EdgeField);
        Assert.Equal("12", app.Main.Tags[0].Value);

        app.Monitor.Apply(Status());
        app.Monitor.Apply(Status());

        Assert.Equal(before + 2, app.Main.Log.Count);
        Assert.Equal("Đã kết nối lại Edge", app.Main.Log[0].Message);
        Assert.False(app.Main.EdgeFieldIsError);
        Assert.Equal("Edge: e1", app.Main.EdgeField);
    }

    [Fact]
    public async Task PlcStateChange_LoggedOncePerTransition()
    {
        var app = await StartAsync();
        app.Monitor.Apply(Status());
        var before = app.Main.Log.Count;

        app.Monitor.Apply(Status(devices: [Device("Line", "unavailable"), Device("Press", "connected"), Device("Fx", "connected")]));
        app.Monitor.Apply(Status(devices: [Device("Line", "unavailable"), Device("Press", "connected"), Device("Fx", "connected")]));

        Assert.Equal(before + 1, app.Main.Log.Count);
        Assert.Equal(("PLC Line", "Đang đọc → Mất kết nối", "10:00:00"),
            (app.Main.Log[0].Source, app.Main.Log[0].Message, app.Main.Log[0].Time));
    }

    [Fact]
    public async Task Log_NewestFirst_AtMost500()
    {
        var app = await StartAsync();
        app.Monitor.Apply(Status());

        for (var i = 0; i < 300; i++)
        {
            app.Monitor.ShowFailure("không kết nối được");
            app.Monitor.Apply(Status());
        }

        Assert.Equal(500, app.Main.Log.Count);
        Assert.Equal("Đã kết nối lại Edge", app.Main.Log[0].Message);
        Assert.Equal("Mất kết nối tới Edge", app.Main.Log[1].Message);
    }

    [Fact]
    public async Task StatusBar_AndTitle()
    {
        var app = await StartAsync();
        var vm = app.Main;
        Assert.Equal("IIoT Edge Monitor", vm.Title);

        app.Monitor.Apply(Status());

        Assert.Equal("IIoT Edge Monitor — e1", vm.Title);
        Assert.Equal("Edge: e1", vm.EdgeField);
        Assert.False(vm.EdgeFieldIsError);
        Assert.Equal("127.0.0.1:5099", vm.Endpoint);
        Assert.Equal("MQTT: Đã kết nối", vm.MqttField);
        Assert.Equal("Buffer: 1.234", vm.BufferField);
        Assert.Equal("Chu kỳ gần nhất: 09:59:59.500", vm.CycleField);
        Assert.Equal("Lệnh ghi từ Cloud: bật", vm.CommandsField);
        Assert.Equal("Đã lưu", vm.SaveField);

        vm.SelectedNode = Node(vm, "Line");
        await vm.DeleteAsync();

        Assert.Equal("IIoT Edge Monitor — e1 *", vm.Title);
        Assert.Equal("Có thay đổi chưa lưu", vm.SaveField);
    }

    [Fact]
    public async Task Commands_EnabledOnlyWhereTheyApply()
    {
        var notLoaded = new App(Handler(settingsStatus: HttpStatusCode.ServiceUnavailable), null);
        await notLoaded.Main.LoadAsync();
        Assert.Equal((false, false, false, false, false, false, false),
            (notLoaded.Main.CanAddChannel, notLoaded.Main.CanAddDevice, notLoaded.Main.CanAddTag, notLoaded.Main.CanEdit,
                notLoaded.Main.CanDelete, notLoaded.Main.CanSave, notLoaded.Main.CanCheck));
        Assert.StartsWith("Không tải được cấu hình", notLoaded.Main.Log[0].Message, StringComparison.Ordinal);

        var app = await StartAsync();
        var vm = app.Main;
        Assert.Equal((true, false, false, false, false, false, true), Rules(vm));

        vm.SelectedNode = vm.Tree[0];
        Assert.Equal((true, false, false, false, false, false, true), Rules(vm));

        vm.SelectedNode = vm.Tree[1];
        Assert.Equal((true, true, false, true, true, false, true), Rules(vm));

        vm.SelectedNode = Node(vm, "Line");
        Assert.Equal((true, true, true, true, true, false, true), Rules(vm));
        Assert.True(vm.CanTestConnection);

        vm.SelectedTag = vm.Tags[0];
        Assert.True(vm.CanEdit && vm.CanDelete);

        vm.SelectedNode = vm.Tree[1];
        Assert.Null(vm.SelectedTag);
        Assert.False(vm.CanTestConnection);

        await vm.DeleteAsync();
        Assert.True(vm.CanSave);

        vm.Draft!.MarkStale();
        vm.SelectedNode = null;
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task AddChannel_FromTheFirstNode_ChangesOnlyTheDraft()
    {
        var app = await StartAsync(dialog: request =>
        {
            var channel = (ChannelDialogViewModel)request.ViewModel;
            channel.Name = "Old";
            Assert.True(channel.PickDriver("Mc1E"));
            Assert.True(request.Ok());
            return Task.CompletedTask;
        });

        await app.Main.ActivateAsync(app.Main.Tree[0]);

        Assert.Equal("Old (Mitsubishi MC 1E)", app.Main.Tree[3].Text);
        Assert.Equal("Old", app.Main.Draft!.Channels[2].Name);
        Assert.True(app.Main.Draft.IsDirty);
        Assert.Single(app.Handler.Requests); // only the GET
    }

    [Fact]
    public async Task Cancel_LeavesTheDraftClean()
    {
        var app = await StartAsync(dialog: request =>
        {
            ((DeviceDialogViewModel)request.ViewModel).Host = "10.9.9.9";
            return Task.CompletedTask; // closed with Huỷ
        });

        await app.Main.ActivateAsync(Node(app.Main, "Line"));

        Assert.False(app.Main.Draft!.IsDirty);
        Assert.Equal("10.0.0.1", app.Main.Draft.Channels[0].Devices[0].Host);
        Assert.EndsWith("Monitor", app.Main.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditPlcAndTag_ChangeThatNode()
    {
        var app = await StartAsync(dialog: request =>
        {
            switch (request.ViewModel)
            {
                case DeviceDialogViewModel plc:
                    plc.Host = "10.0.0.7";
                    break;
                case TagDialogViewModel tag:
                    Assert.NotNull(request.Check);
                    tag.Address = "DB2.DBW2";
                    break;
            }

            Assert.True(request.Ok());
            return Task.CompletedTask;
        });
        var vm = app.Main;
        vm.SelectedNode = Node(vm, "Line");

        await vm.EditAsync();
        vm.SelectedTag = vm.Tags[0];
        await vm.EditAsync();

        var line = vm.Draft!.Channels[0].Devices[0];
        Assert.Equal("10.0.0.7", line.Host);
        Assert.Equal("DB2.DBW2", line.Tags[0].Address);
        Assert.Equal("DB2.DBW2", vm.Tags[0].Address);
        Assert.Equal(3, vm.Draft.Channels.Sum(c => c.Devices.Count));
    }

    [Fact]
    public async Task Delete_AsksTheConfirmation_AndRemovesOnlyOnYes()
    {
        var app = await StartAsync(confirm: false);
        var vm = app.Main;
        vm.SelectedNode = vm.Tree[1];

        await vm.DeleteAsync();

        Assert.Equal(["Xoá Channel Siemens? Kèm theo 2 PLC và 3 tag."], app.Prompts.Confirmations);
        Assert.Equal(2, vm.Draft!.Channels.Count);
        Assert.False(vm.Draft.IsDirty);

        app.Prompts.Answer = true;
        vm.SelectedNode = Node(vm, "Line");
        vm.SelectedTag = vm.Tags[1];
        await vm.DeleteAsync();

        Assert.Equal("Xoá tag Temp?", app.Prompts.Confirmations[^1]);
        Assert.Equal(["Speed"], vm.Draft.Channels[0].Devices[0].Tags.Select(t => t.Name));
        Assert.Equal(["Speed"], vm.Tags.Select(t => t.Name));
        Assert.Null(vm.SelectedTag);

        await vm.DeleteAsync(); // the PLC node is still selected
        Assert.Equal("Xoá PLC Line? Kèm theo 1 tag.", app.Prompts.Confirmations[^1]);
        Assert.Equal(["Press"], vm.Tree[1].Children.Select(n => n.Text));
        Assert.Null(vm.SelectedNode);
        Assert.Equal("Chọn một PLC để xem tag", vm.GridPlaceholder);
    }

    [Fact]
    public async Task ANodeNoLongerInTheDraft_IsLogged_NotThrown()
    {
        App? app = null;
        app = await StartAsync(dialog: request =>
        {
            // The PLC disappears from the draft while its dialog is open.
            var draft = app!.Main.Draft!;
            draft.RemoveDevice(draft.Channels[0].Devices[0]);
            Assert.True(request.Ok());
            return Task.CompletedTask;
        });
        app.Main.SelectedNode = Node(app.Main, "Line");

        await app.Main.EditAsync();

        Assert.Equal(("Cấu hình", "Mục này không còn trong cấu hình nháp, thay đổi bị bỏ qua"),
            (app.Main.Log[0].Source, app.Main.Log[0].Message));
    }

    [Fact]
    public async Task EditOfATagNoLongerInTheDraft_IsLogged_NotThrown()
    {
        var app = await StartAsync();
        var vm = app.Main;
        vm.SelectedNode = Node(vm, "Line");
        vm.SelectedTag = vm.Tags[0];
        vm.Draft!.RemoveTag(vm.Draft.Channels[0].Devices[0].Tags[0]);

        await vm.EditAsync();

        Assert.Equal("Mục này không còn trong cấu hình nháp, thay đổi bị bỏ qua", vm.Log[0].Message);
        Assert.Equal(0, app.Dialogs);
    }

    [Fact]
    public async Task TestConnection_EachResultLogged()
    {
        var app = await StartAsync(dialog: async request =>
        {
            Assert.NotNull(request.TestConnection);
            await request.TestConnection();
            await request.TestConnection();
        });
        app.Main.SelectedNode = Node(app.Main, "Line");
        var before = app.Main.Log.Count;

        await app.Main.TestConnectionAsync();

        Assert.Equal(before + 2, app.Main.Log.Count);
        Assert.All(app.Main.Log.Take(2), e => Assert.Equal(("PLC Line", "Test kết nối: Kết nối được (12 ms)"), (e.Source, e.Message)));
        Assert.False(app.Main.Draft!.IsDirty);
    }

    [Fact]
    public async Task Check_LogsEachErrorAtItsNodeOrVerbatim_OrHợpLệ()
    {
        var app = await StartAsync(validate: """
            {"ok":false,"errors":[
              {"path":"Edge:Channels:0:Devices:0:Tags:1:Address","message":"địa chỉ sai"},
              {"path":"Edge:Mqtt:Host","message":"thiếu host"}]}
            """);

        await app.Main.CheckAsync();

        Assert.Equal(
            ["Edge:Mqtt:Host: thiếu host", "Channel Siemens / PLC Line / tag Temp (Address): địa chỉ sai"],
            app.Main.Log.Take(2).Select(e => e.Message));
        Assert.All(app.Main.Log.Take(2), e => Assert.Equal("Kiểm tra", e.Source));

        app.Validate = """{"ok":true,"errors":[]}""";
        await app.Main.CheckAsync();
        Assert.Equal("Hợp lệ", app.Main.Log[0].Message);
    }

    [Fact]
    public async Task Monitor_KeepsTheLastStatus_AndRaisesStatusApplied()
    {
        var monitor = new MonitorViewModel(Time());
        var raised = new List<EdgeStatus?>();
        monitor.StatusApplied += (_, _) => raised.Add(monitor.LastStatus);
        Assert.Null(monitor.LastStatus);

        var status = Status();
        monitor.Apply(status);

        Assert.Same(status, monitor.LastStatus);
        Assert.Equal([status], raised);
        Assert.Equal("e1", monitor.EdgeId); // every text is already set when the event runs

        monitor.ShowFailure("không kết nối được");
        Assert.Same(status, monitor.LastStatus);
        Assert.Single(raised);
    }

    [Fact]
    public async Task Activate_APlcWhileOneOfItsTagsIsSelected_OpensThePlcDialog()
    {
        var opened = new List<DialogViewModel>();
        var app = await StartAsync(dialog: request =>
        {
            opened.Add(request.ViewModel);
            return Task.CompletedTask;
        });
        var vm = app.Main;
        var line = Node(vm, "Line");
        vm.SelectedNode = line;
        vm.SelectedTag = vm.Tags[0];

        await vm.ActivateAsync(line);

        Assert.IsType<DeviceDialogViewModel>(Assert.Single(opened));
    }

    [Fact]
    public async Task Rebuild_KeepsTheSelectionMarkedInTheTree_AndCollapsedChannels()
    {
        var app = await StartAsync(dialog: request =>
        {
            ((DeviceDialogViewModel)request.ViewModel).Host = "10.0.0.8";
            Assert.True(request.Ok());
            return Task.CompletedTask;
        });
        var vm = app.Main;
        vm.Tree[2].IsExpanded = false;
        var before = Node(vm, "Line");
        vm.SelectedNode = before;
        Assert.True(before.IsSelected);

        await vm.EditAsync();

        var after = Node(vm, "Line");
        Assert.NotSame(before, after);
        Assert.Same(after, vm.SelectedNode);
        Assert.True(after.IsSelected);
        Assert.Equal([true, false], vm.Tree.Skip(1).Select(n => n.IsExpanded));
        Assert.Single(vm.Tree.Concat(vm.Tree.SelectMany(n => n.Children)), n => n.IsSelected);
    }

    [Fact]
    public async Task SelectedTag_FollowsItsRowAcrossValueRefreshes()
    {
        var app = await StartAsync();
        app.Monitor.Apply(Status());
        app.Main.SelectedNode = Node(app.Main, "Line");
        app.Main.SelectedTag = app.Main.Tags[1];

        app.Monitor.Apply(Status(tags: [Tag("Line.Speed", "Line", "12"), Tag("Line.Temp", "Line", "30")]));

        Assert.Same(app.Main.Tags[1], app.Main.SelectedTag);
        Assert.Equal("30", app.Main.SelectedTag!.Value);
    }

    [Fact]
    public async Task LegacyDraftOnlyTag_DoesNotShowTheValueOfAnotherPlcsTagOfThatName()
    {
        var app = await StartAsync(dialog: request =>
        {
            var tag = (TagDialogViewModel)request.ViewModel;
            tag.Name = "Total";
            tag.Address = "DB1.DBW6";
            Assert.True(request.Ok());
            return Task.CompletedTask;
        });
        // A tag "Total" of another PLC (as if it kept legacy TagIds) has the same TagId as Press's drafted one.
        app.Monitor.Apply(Status(tags: [Tag("Count", "Press", "5"), Tag("Total", "Fx", "9")]));
        app.Main.SelectedNode = Node(app.Main, "Press");

        await app.Main.AddTagAsync();

        Assert.Equal(["5", "—"], app.Main.Tags.Select(t => t.Value));
    }

    private static (bool, bool, bool, bool, bool, bool, bool) Rules(MainViewModel vm) =>
        (vm.CanAddChannel, vm.CanAddDevice, vm.CanAddTag, vm.CanEdit, vm.CanDelete, vm.CanSave, vm.CanCheck);

    private static TreeNode Node(MainViewModel vm, string device) =>
        vm.Tree.SelectMany(c => c.Children).Single(n => n.Device?.Name == device);

    private static async Task<App> StartAsync(Func<DialogRequest, Task>? dialog = null, bool confirm = true, string? validate = null)
    {
        var app = new App(null, dialog) { Validate = validate ?? """{"ok":true,"errors":[]}""" };
        app.Prompts.Answer = confirm;
        await app.Main.LoadAsync();
        return app;
    }

    private static FakeTimeProvider Time()
    {
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }

    private static SettingsSnapshot Snapshot() => new(
        "rev-1",
        "file",
        new ChannelTree([
            new ChannelSettings("Siemens", "S7", [
                new DeviceSettings("Line", "S7-1200", "10.0.0.1", 102, 0, 1, 3000, 1000, true, true, false, [
                    new TagSettings("Speed", "DB1.DBW2", "Int", null, false, null, null, null),
                    new TagSettings("Temp", "DB1.DBD4", "Real", null, true, null, null, null)]),
                new DeviceSettings("Press", "S7-1200", "10.0.0.2", 102, 0, 1, 3000, 1000, true, false, true, [
                    new TagSettings("Count", "DB1.DBW0", "Int", null, false, null, null, null)])]),
            new ChannelSettings("Mitsu", "Slmp3E", [
                new DeviceSettings("Fx", "FX5U", "10.0.0.3", 5000, null, null, 3000, 1000, true, false, false, [
                    new TagSettings("D100", "D100", "Int", null, false, null, null, null)])])]),
        [
            new PlcModelInfo("S7-1200", "S7", 102, 0, 1, null, ["Bool", "Int", "Real"], true),
            new PlcModelInfo("FX5U", "Slmp3E", null, null, null, "hint", ["Bool", "Int"], true),
            new PlcModelInfo("FX3U", "Mc1E", null, null, null, "hint", ["Bool", "Int"], false),
        ],
        [],
        true,
        true);

    private static EdgeStatus Status(DeviceInfo[]? devices = null, TagInfo[]? tags = null) => new(
        "e1",
        Now,
        null,
        Now.AddMilliseconds(-500),
        null,
        null,
        devices ?? [Device("Line", "connected"), Device("Press", "connected"), Device("Fx", "connected")],
        new MqttInfo("broker", 8883, true),
        1234,
        tags ?? [Tag("Line.Speed", "Line", "12"), Tag("Line.Temp", "Line", "21.5"), Tag("Count", "Press", "5"), Tag("Fx.D100", "Fx", "7")],
        new SettingsApplyInfo("rev-1", null, "applied", null));

    private static DeviceInfo Device(string name, string state) => new(name, "S7-1200", "10.0.0.1", 102, state, Now, null, 5, 1);

    private static TagInfo Tag(string tagId, string device, string? valueJson, string quality = "good") => new(
        tagId, device, "x", "Int", false,
        valueJson is null ? null : JsonDocument.Parse(valueJson).RootElement.Clone(),
        quality, Now, null);

    private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static RecordingHandler Handler(HttpStatusCode settingsStatus = HttpStatusCode.OK, Func<string>? validate = null) =>
        new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/api/settings" => Reply(settingsStatus, JsonSerializer.Serialize(Snapshot(), JsonSerializerOptions.Web)),
            "/api/settings/validate" => Reply(HttpStatusCode.OK, validate!()),
            "/api/settings/test-connection" => Reply(HttpStatusCode.OK, """{"result":"ok","elapsedMs":12.4}"""),
            _ => Reply(HttpStatusCode.NotFound, "{}"),
        }));

    private sealed class App
    {
        public App(RecordingHandler? handler, Func<DialogRequest, Task>? dialog)
        {
            Handler = handler ?? StatusHandler(() => Validate);
            Monitor = new MonitorViewModel(Time());
            var opener = new Opener(dialog ?? (_ => Task.CompletedTask), () => Dialogs++);
            Main = new MainViewModel(Monitor, new SettingsClient(Port, Handler), Prompts, opener, Time(), Port);
        }

        public RecordingHandler Handler { get; }

        public MonitorViewModel Monitor { get; }

        public MainViewModel Main { get; }

        public Prompts Prompts { get; } = new();

        public string Validate { get; set; } = "";

        public int Dialogs { get; private set; }

        private static RecordingHandler StatusHandler(Func<string> validate) => MainViewModelTests.Handler(validate: validate);
    }

    private sealed class Opener(Func<DialogRequest, Task> script, Action opened) : IDialogOpener
    {
        public Task OpenAsync(DialogRequest request)
        {
            opened();
            return script(request);
        }
    }

    private sealed class Prompts : IUserPrompts
    {
        public bool Answer { get; set; } = true;

        public List<string> Confirmations { get; } = [];

        public Task<string?> AskPasswordAsync(string purpose) => Task.FromResult<string?>(Password);

        public Task<bool> ConfirmAsync(string message)
        {
            Confirmations.Add(message);
            return Task.FromResult(Answer);
        }

        public Task<bool> AskReloadStaleAsync(string message) => throw new NotSupportedException();

        public Task<CloseChoice> AskCloseAsync() => throw new NotSupportedException();

        public Task<bool> AskExitWhileBusyAsync() => throw new NotSupportedException();
    }
}
