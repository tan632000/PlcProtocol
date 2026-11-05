using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using IndustrialIot.EdgeDesktop.Core;
using static IndustrialIot.Edge.Tests.Desktop.StatusClientTests;

namespace IndustrialIot.Edge.Tests.Desktop;

/// <summary>
/// The Channel, PLC and Tag dialogs: they offer only what the channel's driver and the PLC's model allow, edit a copy
/// that reaches the draft only through OK/Áp dụng, check a tag against the Edge showing only that tag's errors, and
/// test a PLC with a password asked once (none sent when cancelled or empty).
/// </summary>
public class DialogViewModelTests
{
    private const string Password = "operator-password-1";

    private static ConfigDraft Draft() => new(new SettingsSnapshot(
        "rev-1",
        "file",
        new ChannelTree([
            new ChannelSettings("Siemens", "S7", [
                Device("S7A", "S7-1200", 102, writes: true, Tag("Speed", "DB1.DBW2"), Tag("Temp", "DB1.DBD4", "Real")),
                Device("S7B", "S7-1200", 102, writes: false)]),
            new ChannelSettings("Mitsubishi", "Slmp3E", [Device("FxA", "FX5U", 5000, writes: false, Tag("D100", "D100"))]),
            new ChannelSettings("Old", "Mc1E", [Device("Fx3", "FX3U", 5001, writes: false)])]),
        [
            new PlcModelInfo("S7-1200", "S7", 102, 0, 1, null, ["Bool", "Int", "Real", "String"], true),
            // Not in the Edge's catalog today: a second and a write-less model of one driver, to see a model change.
            new PlcModelInfo("S7-1500", "S7", 103, 0, 2, "hint 1500", ["Bool", "Int"], true),
            new PlcModelInfo("S7-RO", "S7", 104, 1, 3, null, ["Bool"], false),
            new PlcModelInfo("FX5U", "Slmp3E", null, null, null, "use the SLMP port set in GX Works3", ["Bool", "Int"], true),
            new PlcModelInfo("FX3U", "Mc1E", null, null, null, "use the MC protocol port", ["Bool", "Int"], false),
        ],
        [],
        false,
        true));

    private static DeviceSettings Device(string name, string model, int port, bool writes, params TagSettings[] tags) =>
        new(name, model, "10.0.0.1", port, null, null, 3000, 1000, true, writes, false, tags);

    private static TagSettings Tag(string name, string address, string type = "Int") =>
        new(name, address, type, null, false, null, null, null);

    [Fact]
    public void Device_OffersOnlyTheModelsOfItsChannelsDriver()
    {
        var draft = Draft();

        var vm = new DeviceDialogViewModel(draft, draft.Channels[1], null, Client(), new Prompts());

        Assert.Equal(["FX5U"], vm.Models.Select(m => m.Name));
        Assert.False(vm.IsS7);
    }

    [Fact]
    public void Device_New_GetsTheWebPagesDefaults_AndTheModelsPort()
    {
        var draft = Draft();

        var vm = new DeviceDialogViewModel(draft, draft.Channels[0], null, Client(), new Prompts());

        Assert.Equal("Plc5", vm.Name);
        Assert.Equal("S7-1200", vm.Model);
        Assert.Equal(("102", "0", "1"), (vm.PortText, vm.RackText, vm.SlotText));
        Assert.Equal(("3000", "1000"), (vm.TimeoutText, vm.PollText));
        Assert.True(vm.Enabled);
        Assert.False(vm.WritesEnabled);
        Assert.True(vm.IsS7);
    }

    [Fact]
    public void Device_ModelWithoutDefaultPort_LeavesPortEmpty_AndShowsTheHint()
    {
        var draft = Draft();

        var vm = new DeviceDialogViewModel(draft, draft.Channels[1], null, Client(), new Prompts());

        Assert.Equal("FX5U", vm.Model);
        Assert.Equal(("", "", ""), (vm.PortText, vm.RackText, vm.SlotText));
        Assert.Contains("GX Works3", vm.PortHint);
    }

    [Fact]
    public void Device_ChoosingAnotherModel_FillsItsDefaults_AndDropsWritesItDoesNotAllow()
    {
        var draft = Draft();
        var s7a = draft.Channels[0].Devices[0];
        var vm = new DeviceDialogViewModel(draft, draft.Channels[0], s7a, Client(), new Prompts());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("102", vm.PortText); // opening an edit keeps the saved values
        Assert.True(vm.WritesEnabled);
        vm.Model = "S7-1500";
        Assert.Equal(("103", "0", "2", "hint 1500"), (vm.PortText, vm.RackText, vm.SlotText, vm.PortHint));
        Assert.True(vm.WritesEnabled);
        vm.Model = "S7-RO";

        Assert.Equal(("104", "1", "3"), (vm.PortText, vm.RackText, vm.SlotText));
        Assert.False(vm.WritesEnabled);
        Assert.False(vm.WritesAllowed);
        Assert.Contains(nameof(vm.WritesAllowed), changed);
        Assert.Contains(nameof(vm.CanEnableWrites), changed);
    }

    [Fact]
    public void Device_WritesCanBeSwitchedOff_WhereAllowed()
    {
        var draft = Draft();
        var s7a = draft.Channels[0].Devices[0];
        var vm = new DeviceDialogViewModel(draft, draft.Channels[0], s7a, Client(), new Prompts()) { WritesEnabled = false };

        Assert.True(vm.Apply());

        Assert.False(s7a.WritesEnabled);
    }

    [Fact]
    public void Device_Fx3u_CannotEnableWrites_AndSaysWhy()
    {
        var draft = Draft();
        var vm = new DeviceDialogViewModel(draft, draft.Channels[2], null, Client(), new Prompts());

        vm.WritesEnabled = true;

        Assert.False(vm.CanEnableWrites);
        Assert.False(vm.WritesEnabled);
        Assert.Equal("FX3U chưa cho phép ghi (chưa kiểm chứng trên PLC thật)", vm.WritesNote);
        Assert.Null(new DeviceDialogViewModel(draft, draft.Channels[0], null, Client(), new Prompts()).WritesNote);
    }

    [Fact]
    public void Device_Apply_AddsOrUpdatesTheNodeItWasOpenedFor()
    {
        var draft = Draft();
        var s7b = draft.Channels[0].Devices[1];
        var edit = new DeviceDialogViewModel(draft, draft.Channels[0], s7b, Client(), new Prompts()) { Host = "10.0.0.9" };
        draft.RemoveDevice(draft.Channels[0].Devices[0]);

        Assert.True(edit.Apply());

        Assert.Same(s7b, Assert.Single(draft.Channels[0].Devices));
        Assert.Equal("10.0.0.9", s7b.Host);
        var add = new DeviceDialogViewModel(draft, draft.Channels[0], null, Client(), new Prompts()) { Host = "10.0.0.10", PortText = "" };
        Assert.True(add.Apply());
        Assert.Equal([s7b.Name, add.Name], draft.Channels[0].Devices.Select(d => d.Name));
        Assert.Null(draft.Channels[0].Devices[1].Port);
    }

    [Fact]
    public void Device_LegacyTagIds_ShownOnlyWhenSet_AndUntickingWarns()
    {
        var draft = Draft();
        var device = draft.Channels[0].Devices[0];
        device.LegacyTagIds = true;
        var vm = new DeviceDialogViewModel(draft, draft.Channels[0], device, Client(), new Prompts());

        Assert.True(vm.ShowLegacyTagIds);
        Assert.Null(vm.LegacyTagIdsWarning);
        vm.LegacyTagIds = false;
        Assert.Contains("TagId mới", vm.LegacyTagIdsWarning);
        Assert.False(new DeviceDialogViewModel(draft, draft.Channels[0], null, Client(), new Prompts()).ShowLegacyTagIds);
    }

    [Fact]
    public void Channel_DriverCanChangeOnlyWithoutPlc_AndNamesItsModels()
    {
        var draft = Draft();
        var siemens = new ChannelDialogViewModel(draft, draft.Channels[0]);
        var fresh = new ChannelDialogViewModel(draft, null);

        Assert.False(siemens.CanChangeDriver);
        Assert.False(siemens.PickDriver("Slmp3E"));
        Assert.Equal("S7", siemens.Driver);
        Assert.True(fresh.CanChangeDriver);
        Assert.True(fresh.PickDriver("Mc1E"));
        Assert.Equal("Mitsubishi MC 1E", fresh.DriverDisplay);
        Assert.Equal("Hỗ trợ dòng PLC: FX3U", fresh.DriverNote);
        Assert.Equal(["Siemens S7", "Mitsubishi SLMP 3E", "Mitsubishi MC 1E"], ChannelDialogViewModel.Drivers.Select(d => d.Display));
        Assert.Equal("Hỗ trợ dòng PLC: S7-1200, S7-1500, S7-RO", ChannelDialogViewModel.NoteFor(draft, "S7"));
    }

    [Fact]
    public void Channel_Apply_AddsANewChannel_OrRenamesTheOpenedOne()
    {
        var draft = Draft();
        var add = new ChannelDialogViewModel(draft, null);
        Assert.Equal("Channel4", add.Name);

        Assert.True(add.Apply());
        var rename = new ChannelDialogViewModel(draft, draft.Channels[0]) { Name = "S7 line" };
        Assert.True(rename.Apply());

        Assert.Equal(["S7 line", "Mitsubishi", "Old", "Channel4"], draft.Channels.Select(c => c.Name));
        Assert.Equal(2, draft.Channels[0].Devices.Count);
    }

    [Theory]
    [InlineData("PortText", "abc", "Port")]
    [InlineData("TimeoutText", "", "TimeoutMs")]
    [InlineData("PollText", "1.5", "PollIntervalMs")]
    [InlineData("RackText", "-1", "Rack")]
    public void Device_NonDigits_AreRefusedLocally(string property, string text, string field)
    {
        var draft = Draft();
        var vm = new DeviceDialogViewModel(draft, draft.Channels[0], draft.Channels[0].Devices[0], Client(), new Prompts());
        typeof(DeviceDialogViewModel).GetProperty(property)!.SetValue(vm, text);

        Assert.False(vm.Apply());

        Assert.Equal("phải là số", vm.FieldErrors[field]);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void Tag_NonNumericLimits_AreRefusedLocally()
    {
        var draft = Draft();
        var vm = new TagDialogViewModel(draft, draft.Channels[0].Devices[0], null, Client()) { Name = "X", Address = "DB1.DBW8", Writable = true, MinText = "1,5" };

        Assert.False(vm.Apply());

        Assert.Equal("phải là số", vm.FieldErrors["Min"]);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void Tag_OffersTheModelsDataTypes_LengthOnlyForString_LimitsOnlyWithWrites()
    {
        var draft = Draft();
        var vm = new TagDialogViewModel(draft, draft.Channels[0].Devices[0], null, Client());

        Assert.Equal(["Bool", "Int", "Real", "String"], vm.DataTypes);
        Assert.False(vm.IsString);
        vm.DataType = "String";
        Assert.True(vm.IsString);
        Assert.False(vm.ShowLimits);
        vm.Writable = true;
        Assert.True(vm.ShowLimits);
    }

    [Fact]
    public void Tag_OnAnFx3u_CannotBeWritable()
    {
        var draft = Draft();
        var vm = new TagDialogViewModel(draft, draft.Channels[2].Devices[0], null, Client());

        vm.Writable = true;

        Assert.False(vm.CanWrite);
        Assert.False(vm.Writable);
    }

    [Fact]
    public async Task Tag_Check_ShowsOnlyItsOwnErrors()
    {
        var draft = Draft();
        string? sent = null;
        var handler = new RecordingHandler(async (request, _) =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return Reply(HttpStatusCode.OK, """
                {"ok":false,"errors":[
                  {"path":"Edge:Channels:0:Devices:0:Tags:0:Name","message":"other tag"},
                  {"path":"Edge:Channels:0:Devices:0:Tags:1:Address","message":"bad address"},
                  {"path":"Edge:Channels:1:Name","message":"other channel"}]}
                """);
        });
        var device = draft.Channels[0].Devices[0];
        var vm = new TagDialogViewModel(draft, device, device.Tags[1], Client(handler)) { Address = "XYZ" };

        await vm.CheckAsync();

        Assert.Equal("bad address", vm.FieldErrors["Address"]);
        Assert.Single(vm.FieldErrors);
        Assert.NotEqual("Hợp lệ", vm.Message);
        Assert.Equal("XYZ", JsonNode.Parse(sent!)!["settings"]!["Channels"]![0]!["Devices"]![0]!["Tags"]![1]!["Address"]!.GetValue<string>());
        Assert.Equal("DB1.DBD4", device.Tags[1].Address);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public async Task Tag_Check_WithoutOwnErrors_IsValid_AndANewTagIsChecked()
    {
        var draft = Draft();
        string? sent = null;
        var handler = new RecordingHandler(async (request, _) =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return Reply(HttpStatusCode.OK, """{"ok":false,"errors":[{"path":"Edge:Channels:1:Name","message":"other"}]}""");
        });
        var vm = new TagDialogViewModel(draft, draft.Channels[0].Devices[0], null, Client(handler)) { Name = "New", Address = "DB1.DBW8" };

        await vm.CheckAsync();

        Assert.Equal("Hợp lệ", vm.Message);
        Assert.Empty(vm.FieldErrors);
        Assert.Equal(3, JsonNode.Parse(sent!)!["settings"]!["Channels"]![0]!["Devices"]![0]!["Tags"]!.AsArray().Count);
        Assert.Equal(2, draft.Channels[0].Devices[0].Tags.Count);
    }

    [Fact]
    public void Tag_Apply_InAddMode_AddsAndClearsForTheNext_InEditMode_Updates()
    {
        var draft = Draft();
        var device = draft.Channels[0].Devices[0];
        var add = new TagDialogViewModel(draft, device, null, Client()) { Name = "A", Address = "DB1.DBW8", DataType = "Int" };

        Assert.True(add.Apply());
        Assert.False(add.CloseAfterApply);
        Assert.Equal(("", ""), (add.Name, add.Address));
        Assert.Equal("Int", add.DataType);
        var edit = new TagDialogViewModel(draft, device, device.Tags[0], Client()) { Address = "DB1.DBW20" };
        Assert.True(edit.Apply());
        Assert.True(edit.CloseAfterApply);

        Assert.Equal(["Speed", "Temp", "A"], device.Tags.Select(t => t.Name));
        Assert.Equal("DB1.DBW20", device.Tags[0].Address);
    }

    [Fact]
    public void Tag_Apply_InEditMode_GoesToTheSameTag_AfterAnotherWasRemoved()
    {
        var draft = Draft();
        var device = draft.Channels[0].Devices[0];
        var temp = device.Tags[1];
        var vm = new TagDialogViewModel(draft, device, temp, Client()) { Address = "DB1.DBD40" };
        draft.RemoveTag(device.Tags[0]);

        Assert.True(vm.Apply());

        Assert.Same(temp, Assert.Single(device.Tags));
        Assert.Equal("DB1.DBD40", temp.Address);
    }

    [Fact]
    public async Task Tag_Check_ATagLevelError_IsTheMessage()
    {
        var draft = Draft();
        var handler = new RecordingHandler((_, _) => Task.FromResult(Reply(HttpStatusCode.OK,
            """{"ok":false,"errors":[{"path":"Edge:Channels:0:Devices:0:Tags:0","message":"must not be empty"}]}""")));
        var device = draft.Channels[0].Devices[0];
        var vm = new TagDialogViewModel(draft, device, device.Tags[0], Client(handler));

        await vm.CheckAsync();

        Assert.Equal("must not be empty", vm.Message);
        Assert.Empty(vm.FieldErrors);
    }

    [Fact]
    public void Cancel_LeavesTheDraftUnchanged()
    {
        var draft = Draft();
        var device = draft.Channels[0].Devices[0];

        _ = new DeviceDialogViewModel(draft, draft.Channels[0], device, Client(), new Prompts()) { Host = "x", WritesEnabled = false };
        _ = new TagDialogViewModel(draft, device, device.Tags[0], Client()) { Name = "x" };
        _ = new ChannelDialogViewModel(draft, draft.Channels[0]) { Name = "x" };

        Assert.False(draft.IsDirty);
        Assert.Equal(("10.0.0.1", true, "Speed", "Siemens"), (device.Host, device.WritesEnabled, device.Tags[0].Name, draft.Channels[0].Name));
    }

    [Fact]
    public async Task Test_AsksThePasswordOnce_AndSendsTheDialogsValues()
    {
        var draft = Draft();
        string? sent = null;
        var handler = new RecordingHandler(async (request, _) =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return Reply(HttpStatusCode.OK, """{"result":"unreachable","elapsedMs":3001}""");
        });
        var prompts = new Prompts(Password);
        var vm = new DeviceDialogViewModel(draft, draft.Channels[0], draft.Channels[0].Devices[0], Client(handler), prompts)
        {
            Host = "10.0.0.7",
            SlotText = "2",
        };

        await vm.TestConnectionAsync();

        Assert.Equal(1, prompts.PasswordAsks);
        var body = JsonNode.Parse(sent!)!;
        Assert.Equal(Password, body["password"]!.GetValue<string>());
        Assert.Equal("10.0.0.7", body["proposed"]!["host"]!.GetValue<string>());
        Assert.Equal(2, body["proposed"]!["slot"]!.GetValue<int>());
        Assert.Equal("S7", body["proposed"]!["driver"]!.GetValue<string>());
        Assert.Equal("Không kết nối được tới PLC (3001 ms)", vm.TestMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Test_WithACancelledOrEmptyPassword_SendsNothing(string? answer)
    {
        var draft = Draft();
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("nothing may be sent"));
        var vm = new DeviceDialogViewModel(draft, draft.Channels[0], draft.Channels[0].Devices[0], Client(handler), new Prompts(answer));

        await vm.TestConnectionAsync();

        Assert.Empty(handler.Requests);
        Assert.Null(vm.TestMessage);
    }

    [Theory]
    [InlineData(409, """{"error":"endpoint-in-use"}""", "PLC này đang chạy với cấu hình khác: lưu trước rồi test")]
    [InlineData(409, """{"error":"test-running"}""", "Một lần test khác đang chạy")]
    [InlineData(429, """{"error":"rate-limited"}""", "Đã test quá 10 lần trong một phút, đợi một chút")]
    [InlineData(429, """{"error":"locked","retryAfterSeconds":42}""", "Sai mật khẩu quá nhiều lần, thử lại sau 42 giây")]
    [InlineData(401, """{"error":"wrong-password"}""", "Sai mật khẩu")]
    [InlineData(403, """{"error":"password-not-set"}""",
        "Chưa đặt mật khẩu settings: chạy IndustrialIot.Edge.exe set-settings-password --service (admin)")]
    [InlineData(200, """{"result":"ok","elapsedMs":12}""", "Kết nối được (12 ms)")]
    public async Task Test_ShowsEachOutcome(int status, string json, string message)
    {
        var draft = Draft();
        var handler = new RecordingHandler((_, _) => Task.FromResult(Reply((HttpStatusCode)status, json)));
        var vm = new DeviceDialogViewModel(draft, draft.Channels[0], draft.Channels[0].Devices[0], Client(handler), new Prompts(Password));

        await vm.TestConnectionAsync();

        Assert.Equal(message, vm.TestMessage);
    }

    [Fact]
    public async Task Test_ProposedFieldErrors_LandOnTheDialogsFields()
    {
        var draft = Draft();
        var handler = new RecordingHandler((_, _) => Task.FromResult(Reply(HttpStatusCode.BadRequest,
            """{"ok":false,"errors":[{"path":"proposed:port","message":"is required"},{"path":"proposed:timeoutMs","message":"too big"}]}""")));
        var vm = new DeviceDialogViewModel(draft, draft.Channels[0], draft.Channels[0].Devices[0], Client(handler), new Prompts(Password));

        await vm.TestConnectionAsync();

        Assert.Equal("is required", vm.FieldErrors["Port"]);
        Assert.Equal("too big", vm.FieldErrors["TimeoutMs"]);
    }

    [Fact]
    public void DeleteTexts_NameWhatGoesWithIt()
    {
        var draft = Draft();
        var siemens = draft.Channels[0];

        Assert.Equal("Xoá Channel Siemens? Kèm theo 2 PLC và 2 tag.", Confirmations.Delete(siemens));
        Assert.Equal("Xoá PLC S7A? Kèm theo 2 tag.", Confirmations.Delete(siemens.Devices[0]));
        Assert.Equal("Xoá PLC S7B?", Confirmations.Delete(siemens.Devices[1]));
        Assert.Equal("Xoá tag Speed?", Confirmations.Delete(siemens.Devices[0].Tags[0]));
    }

    private static SettingsClient Client(RecordingHandler? handler = null) =>
        new(5099, handler ?? new RecordingHandler((_, _) => throw new InvalidOperationException("no request expected")));

    private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class Prompts(string? password = null) : IUserPrompts
    {
        public int PasswordAsks { get; private set; }

        public Task<string?> AskPasswordAsync(string purpose)
        {
            PasswordAsks++;
            return Task.FromResult(password);
        }

        public Task<bool> ConfirmAsync(string message) => Task.FromResult(true);

        public Task<bool> AskReloadStaleAsync(string message) => throw new NotSupportedException();

        public Task<CloseChoice> AskCloseAsync() => throw new NotSupportedException();

        public Task<bool> AskExitWhileBusyAsync() => throw new NotSupportedException();
    }
}
