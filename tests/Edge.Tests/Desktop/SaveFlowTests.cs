using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using IndustrialIot.EdgeDesktop.Core;
using Microsoft.Extensions.Time.Testing;
using static IndustrialIot.Edge.Tests.Desktop.StatusClientTests;

namespace IndustrialIot.Edge.Tests.Desktop;

/// <summary>
/// Lưu &amp; áp dụng, Tải lại, closing the window and changes made elsewhere (plan D-04, D-06, D-08): the busy state,
/// the client checks, validate, the password, PUT, the wait for this save's apply through the monitor restart, every
/// refusal, the close guard and the outside changes. The Edge is a scripted handler; statuses are applied to the
/// monitor view model as its poll loop would.
/// </summary>
public class SaveFlowTests
{
    private const string Password = "operator-password-1";
    private const string Waiting = "Đang áp dụng (Edge khởi động lại)…";
    private const string Ours = "a-ours";

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Applied_ReloadsAndLogs_WithPollFailuresDuringTheWaitNeitherRedNorLogged()
    {
        var app = await DirtyAsync();

        var save = app.Main.SaveAsync();
        await Until(() => app.Main.SaveField == Waiting);
        Assert.True(app.Main.IsBusy);
        var put = app.Edge.Requests.Single(r => r.Method == "PUT");
        using (var body = JsonDocument.Parse(put.Body))
        {
            Assert.Equal("rev-1", body.RootElement.GetProperty("revision").GetString());
            Assert.Equal(Password, body.RootElement.GetProperty("password").GetString());
        }

        // The monitor server restarts with the Edge: its polls fail for a while.
        app.Monitor.ShowFailure("không kết nối được");
        Assert.False(app.Main.EdgeFieldIsError);
        Assert.Equal("Edge: e1", app.Main.EdgeField);
        app.Monitor.Apply(Status("rev-1", Ours, "applying"));
        app.Edge.Revision = "rev-2";
        app.Monitor.Apply(Status("rev-2", Ours, "applied"));

        Assert.True(await save);
        Assert.False(app.Main.IsBusy);
        Assert.Equal(("rev-2", false), (app.Main.Draft!.Revision, app.Main.Draft.IsDirty));
        Assert.Equal("Đã lưu", app.Main.SaveField);
        Assert.Equal(["GET", "POST", "PUT", "GET"], app.Edge.Requests.Select(r => r.Method));
        Assert.Contains(app.Main.Log, e => e.Message == "Đã áp dụng");
        Assert.DoesNotContain(app.Main.Log, e => e.Source == "Edge");
        Assert.DoesNotContain(app.Main.Log, e => e.Message.Contains("nơi khác", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RolledBack_LogsTheEdgesError_AndKeepsTheDraftDirty()
    {
        var app = await DirtyAsync();
        var draft = app.Main.Draft;

        var save = app.Main.SaveAsync();
        await Until(() => app.Main.SaveField == Waiting);
        app.Monitor.Apply(Status("rev-1", Ours, "rolledBack", "Edge không khởi động được với cấu hình mới"));

        Assert.False(await save);
        Assert.Same(draft, app.Main.Draft);
        Assert.True(draft!.IsDirty);
        Assert.True(app.Main.CanSave);
        Assert.Contains(app.Main.Log, e => e.Message.Contains("Edge không khởi động được với cấu hình mới", StringComparison.Ordinal));
        Assert.Single(app.Edge.Requests, r => r.Method == "GET");
    }

    [Fact]
    public async Task AForeignApplyId_KeepsTheDraft_AndMarksItStale()
    {
        var app = await DirtyAsync();
        app.Monitor.Apply(Status("rev-1", "a-old", "applied"));
        var draft = app.Main.Draft;

        var save = app.Main.SaveAsync();
        await Until(() => app.Main.SaveField == Waiting);

        // A status read before the PUT still names the previous apply: not an answer.
        app.Monitor.Apply(Status("rev-1", "a-old", "applied"));
        Assert.True(app.Main.IsBusy);

        app.Monitor.Apply(Status("rev-3", "a-other", "applied"));

        Assert.False(await save);
        Assert.Same(draft, app.Main.Draft);
        Assert.True(draft!.IsStale);
        Assert.False(app.Main.CanSave);
        Assert.Single(app.Edge.Requests, r => r.Method == "GET");
        Assert.Single(app.Main.Log, e => e.Message ==
            "Không xác nhận được lần lưu vừa rồi (Edge đã áp dụng một lần lưu khác): Tải lại để xem cấu hình đang chạy");
        Assert.DoesNotContain(app.Main.Log, e => e.Message.Contains("nơi khác,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnApplyThatEndedWhileThePasswordWasTyped_IsNotTakenForTheAnswer()
    {
        var app = await DirtyAsync();
        app.Prompts.OnPassword = () => app.Monitor.Apply(Status("rev-1", "a-web", "rolledBack", "lỗi"));

        var save = app.Main.SaveAsync();
        await Until(() => app.Main.SaveField == Waiting);

        // A poll read before our PUT arrives after it: it still names the web page's apply.
        app.Monitor.Apply(Status("rev-1", "a-web", "rolledBack", "lỗi"));
        Assert.True(app.Main.IsBusy);
        app.Monitor.Apply(Status("rev-2", Ours, "applied"));

        Assert.True(await save);
        Assert.DoesNotContain(app.Main.Log, e => e.Message.Contains("một lần lưu khác", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Applied_ButTheReloadFails_IsNotReportedAsAChangeElsewhere()
    {
        var app = await DirtyAsync();

        var save = app.Main.SaveAsync();
        await Until(() => app.Main.SaveField == Waiting);
        app.Edge.FailGets = true;
        app.Monitor.Apply(Status("rev-2", Ours, "applied"));
        Assert.True(await save);
        app.Monitor.Apply(Status("rev-2", Ours, "applied"));

        Assert.Contains(app.Main.Log, e => e.Message.StartsWith("Không tải được cấu hình", StringComparison.Ordinal));
        Assert.DoesNotContain(app.Main.Log, e => e.Message.Contains("ở nơi khác", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoAnswerIn60s_LogsUnconfirmed_ThenTheNextRevisionChangeSaysItMayBeThatSave()
    {
        var app = await DirtyAsync();

        var save = app.Main.SaveAsync();
        await Until(() => app.Main.SaveField == Waiting);
        app.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(app.Main.IsBusy);
        app.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(await save);
        Assert.False(app.Main.IsBusy);
        Assert.True(app.Main.Draft!.IsDirty);
        Assert.Contains(app.Main.Log, e => e.Message == "Edge chưa xác nhận lần lưu");

        app.Monitor.Apply(Status("rev-2", Ours, "applied"));
        app.Monitor.Apply(Status("rev-2", Ours, "applied"));

        Assert.Single(app.Main.Log, e => e.Message == "Edge đã áp dụng một cấu hình mới, có thể là lần lưu vừa rồi");
        Assert.DoesNotContain(app.Main.Log, e => e.Message.Contains("đã đổi ở nơi khác", StringComparison.Ordinal)
            || e.Message.Contains("được đổi ở nơi khác", StringComparison.Ordinal));
        Assert.Equal("rev-1", app.Main.Draft.Revision);
    }

    public static TheoryData<HttpStatusCode, string, string> Refusals => new()
    {
        { HttpStatusCode.Forbidden, """{"error":"password-not-set"}""",
            "Chưa đặt mật khẩu settings: chạy IndustrialIot.Edge.exe set-settings-password --service (admin)" },
        { HttpStatusCode.TooManyRequests, """{"error":"locked","retryAfterSeconds":30}""",
            "Sai mật khẩu quá nhiều lần, thử lại sau 30 giây" },
        { HttpStatusCode.Conflict, """{"error":"apply-in-progress"}""", "Edge đang áp dụng một lần lưu khác, thử lại sau" },
        { HttpStatusCode.RequestEntityTooLarge, """{"error":"too-large"}""", "Cấu hình quá lớn" },
        { HttpStatusCode.InternalServerError, "{}", "HTTP 500" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EachRefusal_ShowsItsMessage_KeepsTheDraft_AndAsksThePasswordOnce(HttpStatusCode status, string json, string message)
    {
        var app = await DirtyAsync();
        app.Edge.Puts.Enqueue(() => Reply(status, json));
        var draft = app.Main.Draft;

        Assert.False(await Ended(app.Main.SaveAsync()));

        Assert.Same(draft, app.Main.Draft);
        Assert.Equal((true, false), (draft!.IsDirty, draft.IsStale));
        Assert.False(app.Main.IsBusy);
        Assert.Equal(1, app.Prompts.PasswordAsks); // never again after 429 locked: each try counts against the lockout
        Assert.Contains(app.Main.Log, e => e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoConnection_ShowsKhôngKếtNốiĐược_AndKeepsTheDraft()
    {
        var app = await DirtyAsync();
        app.Edge.Puts.Enqueue(() => throw new HttpRequestException("refused"));

        Assert.False(await Ended(app.Main.SaveAsync()));

        Assert.True(app.Main.Draft!.IsDirty);
        Assert.Contains(app.Main.Log, e => e.Message.Contains("không kết nối được", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrongPassword_IsShown_AndAskedAgain()
    {
        var app = await DirtyAsync();
        app.Edge.Puts.Enqueue(() => Reply(HttpStatusCode.Unauthorized, """{"error":"wrong-password"}"""));
        app.Prompts.Passwords.Enqueue("typo");

        var save = app.Main.SaveAsync();
        await Until(() => app.Main.SaveField == Waiting);
        app.Monitor.Apply(Status("rev-2", Ours, "applied"));

        Assert.True(await save);
        Assert.Equal(2, app.Prompts.PasswordAsks);
        Assert.Contains(app.Main.Log, e => e.Message == "Sai mật khẩu");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ACancelledOrEmptyPassword_SendsNothing(string? typed)
    {
        var app = await DirtyAsync();
        app.Prompts.Passwords.Enqueue(typed);

        Assert.False(await Ended(app.Main.SaveAsync()));

        Assert.DoesNotContain(app.Edge.Requests, r => r.Method == "PUT");
        Assert.False(app.Main.IsBusy);
        Assert.True(app.Main.Draft!.IsDirty);
    }

    [Fact]
    public async Task ValidationErrors_LogEach_SelectTheFirstMappableNode_AndSendNothingElse()
    {
        var app = await DirtyAsync();
        app.Edge.Validate = """
            {"ok":false,"errors":[
              {"path":"Edge:Mqtt:Host","message":"thiếu host"},
              {"path":"Edge:Channels:0:Devices:1:Tags:0:Address","message":"địa chỉ sai"},
              {"path":"Edge:Channels:0:Devices:0:Name","message":"trùng tên"}]}
            """;

        Assert.False(await Ended(app.Main.SaveAsync()));

        Assert.Equal(0, app.Prompts.PasswordAsks);
        Assert.DoesNotContain(app.Edge.Requests, r => r.Method == "PUT");
        Assert.Contains(app.Main.Log, e => e.Message == "Edge:Mqtt:Host: thiếu host");
        Assert.Contains(app.Main.Log, e => e.Message == "Channel Siemens / PLC Press / tag Count (Address): địa chỉ sai");
        Assert.Contains(app.Main.Log, e => e.Message == "Channel Siemens / PLC Line (Name): trùng tên");
        Assert.Equal("Press", app.Main.SelectedNode?.Device?.Name);
        Assert.Equal("Count", app.Main.SelectedTag?.Name);
        Assert.False(app.Main.IsBusy);
    }

    [Fact]
    public async Task ARejectedPut_WithErrors_IsShownAsValidationErrors()
    {
        var app = await DirtyAsync();
        app.Edge.Puts.Enqueue(() => Reply(HttpStatusCode.BadRequest,
            """{"errors":[{"path":"Edge:Channels:0:Devices:0","message":"trùng host"}]}"""));

        Assert.False(await Ended(app.Main.SaveAsync()));

        Assert.Contains(app.Main.Log, e => e.Message == "Channel Siemens / PLC Line: trùng host");
        Assert.Equal("Line", app.Main.SelectedNode?.Device?.Name);
        Assert.True(app.Main.Draft!.IsDirty);
    }

    [Fact]
    public async Task AChannelWithoutPlc_IsRefusedBeforeValidate()
    {
        var app = await StartAsync(dialog: request =>
        {
            var channel = (ChannelDialogViewModel)request.ViewModel;
            channel.Name = "Empty";
            Assert.True(request.Ok());
            return Task.CompletedTask;
        });
        await app.Main.AddChannelAsync();

        Assert.False(await Ended(app.Main.SaveAsync()));

        Assert.Contains(app.Main.Log, e => e.Message == "Channel Empty chưa có PLC");
        Assert.Equal("Empty", app.Main.SelectedNode?.Channel?.Name);
        Assert.Equal(["GET"], app.Edge.Requests.Select(r => r.Method));
        Assert.Equal(0, app.Prompts.PasswordAsks);
    }

    [Fact]
    public async Task StaleRevision_TảiLại_DropsTheDraft()
    {
        var app = await DirtyAsync();
        app.Edge.Puts.Enqueue(() => Reply(HttpStatusCode.Conflict, """{"error":"stale-revision"}"""));
        app.Edge.Revision = "rev-5";
        app.Prompts.ReloadStale = true;

        Assert.False(await Ended(app.Main.SaveAsync()));

        Assert.Equal(["Cấu hình trên Edge đã đổi ở nơi khác"], app.Prompts.StaleAsks);
        Assert.Equal(("rev-5", false, false), (app.Main.Draft!.Revision, app.Main.Draft.IsDirty, app.Main.Draft.IsStale));
    }

    [Fact]
    public async Task StaleRevision_GiữĐểXem_KeepsTheDraftForReading_AndDisablesLưuUntilTảiLại()
    {
        var app = await DirtyAsync();
        app.Edge.Puts.Enqueue(() => Reply(HttpStatusCode.Conflict, """{"error":"stale-revision"}"""));
        app.Prompts.ReloadStale = false;
        var draft = app.Main.Draft;

        Assert.False(await Ended(app.Main.SaveAsync()));

        Assert.Same(draft, app.Main.Draft);
        Assert.Equal((true, true), (draft!.IsDirty, draft.IsStale));
        Assert.False(app.Main.CanSave);
        var requests = app.Edge.Requests.Count;
        Assert.False(await Ended(app.Main.SaveAsync()));
        Assert.Equal(requests, app.Edge.Requests.Count);

        await app.Main.ReloadAsync();

        Assert.Equal((false, false), (app.Main.Draft!.IsDirty, app.Main.Draft.IsStale));
    }

    [Fact]
    public async Task Close_Lưu_OnAStaleDraft_SaysTảiLạiFirst_AndStaysOpen()
    {
        var app = await DirtyAsync();
        app.Edge.Puts.Enqueue(() => Reply(HttpStatusCode.Conflict, """{"error":"stale-revision"}"""));
        app.Prompts.ReloadStale = false;
        Assert.False(await Ended(app.Main.SaveAsync()));
        app.Prompts.Close = CloseChoice.Save;

        Assert.False(await app.Main.ConfirmCloseAsync());

        Assert.Contains(app.Main.Log, e => e.Message == "Bản nháp đã cũ (cấu hình trên Edge đã đổi): Tải lại trước khi lưu");
    }

    [Fact]
    public async Task Busy_DisablesEveryCommand_AndNothingRunsUntilItEnds()
    {
        var app = await DirtyAsync();
        app.Main.SelectedNode = Node(app.Main, "Line");
        app.Main.SelectedTag = app.Main.Tags[0];
        Assert.True(app.Main.CanSave);

        var save = app.Main.SaveAsync();
        await Until(() => app.Main.SaveField == Waiting);

        Assert.Equal((false, false, false, false, false, false, false, false, false), Rules(app.Main));
        var requests = app.Edge.Requests.Count;
        Assert.False(await Ended(app.Main.SaveAsync()));
        await app.Main.AddChannelAsync();
        await app.Main.AddDeviceAsync();
        await app.Main.AddTagAsync();
        await app.Main.EditAsync();
        await app.Main.ActivateAsync(app.Main.Tree[0]);
        await app.Main.DeleteAsync();
        await app.Main.CheckAsync();
        await app.Main.TestConnectionAsync();
        await app.Main.ReloadAsync();
        Assert.Equal(requests, app.Edge.Requests.Count);
        Assert.Equal(0, app.Dialogs);
        Assert.Empty(app.Prompts.Confirmations);
        Assert.Equal(1, app.Prompts.PasswordAsks);

        app.Monitor.Apply(Status("rev-2", Ours, "applied"));
        Assert.True(await save);
        Assert.True(app.Main is { IsBusy: false, CanAddChannel: true, CanCheck: true, CanReload: true });
    }

    [Fact]
    public async Task Close_WhenSaved_ClosesWithoutAsking()
    {
        var app = await StartAsync();

        Assert.True(await app.Main.ConfirmCloseAsync());
        Assert.Equal(0, app.Prompts.CloseAsks);
    }

    [Theory]
    [InlineData(CloseChoice.DontSave, true)]
    [InlineData(CloseChoice.Cancel, false)]
    public async Task Close_WithUnsavedChanges_AsksLưuKhôngLưuHuỷ(CloseChoice choice, bool closes)
    {
        var app = await DirtyAsync();
        app.Prompts.Close = choice;

        Assert.Equal(closes, await app.Main.ConfirmCloseAsync());
        Assert.Equal(1, app.Prompts.CloseAsks);
        Assert.DoesNotContain(app.Edge.Requests, r => r.Method == "PUT");
    }

    [Theory]
    [InlineData("applied", true)]
    [InlineData("rolledBack", false)]
    public async Task Close_Lưu_SavesAndClosesOnlyWhenApplied(string state, bool closes)
    {
        var app = await DirtyAsync();
        app.Prompts.Close = CloseChoice.Save;

        var close = app.Main.ConfirmCloseAsync();
        await Until(() => app.Main.SaveField == Waiting);
        app.Monitor.Apply(Status("rev-2", Ours, state, state == "rolledBack" ? "lỗi" : null));

        Assert.Equal(closes, await close);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Close_WhileBusy_AsksĐợiOrThoátKhôngChờ(bool exit)
    {
        var app = await DirtyAsync();
        var save = app.Main.SaveAsync();
        await Until(() => app.Main.SaveField == Waiting);
        app.Prompts.ExitWhileBusy = exit;

        Assert.Equal(exit, await app.Main.ConfirmCloseAsync());
        Assert.Equal((1, 0), (app.Prompts.BusyCloseAsks, app.Prompts.CloseAsks));

        app.Monitor.Apply(Status("rev-2", Ours, "applied"));
        Assert.True(await save);
    }

    [Fact]
    public async Task TảiLại_WhenDirty_AsksFirst()
    {
        var app = await DirtyAsync();
        app.Prompts.Answer = false;

        await app.Main.ReloadAsync();
        Assert.True(app.Main.Draft!.IsDirty);
        Assert.Single(app.Edge.Requests, r => r.Method == "GET");

        app.Prompts.Answer = true;
        await app.Main.ReloadAsync();
        Assert.False(app.Main.Draft!.IsDirty);
        Assert.Equal(2, app.Edge.Requests.Count(r => r.Method == "GET"));
    }

    [Fact]
    public async Task TảiLại_WhenClean_DoesNotAsk()
    {
        var app = await StartAsync();

        await app.Main.ReloadAsync();

        Assert.Empty(app.Prompts.Confirmations);
        Assert.Equal(2, app.Edge.Requests.Count(r => r.Method == "GET"));
    }

    [Fact]
    public async Task ChangedElsewhere_Clean_ReloadsSilently()
    {
        var app = await StartAsync();
        app.Monitor.Apply(Status("rev-1", null, "applied"));
        app.Edge.Revision = "rev-2";

        app.Monitor.Apply(Status("rev-2", "a-web", "applied"));

        // The entry is written after busy ends: wait for it, not for busy.
        await Until(() => app.Main.Log.FirstOrDefault()?.Message == "Cấu hình đã được đổi ở nơi khác, đã tải lại");
        Assert.Equal("rev-2", app.Main.Draft?.Revision);
        Assert.Single(app.Main.Log, e => e.Message == "Cấu hình đã được đổi ở nơi khác, đã tải lại");
        Assert.Empty(app.Prompts.Confirmations);
    }

    [Fact]
    public async Task ChangedElsewhere_Dirty_WarnsOnce_AndKeepsTheDraft()
    {
        var app = await DirtyAsync();
        var draft = app.Main.Draft;
        app.Edge.Revision = "rev-2";

        app.Monitor.Apply(Status("rev-2", "a-web", "applied"));
        app.Monitor.Apply(Status("rev-2", "a-web", "applied"));

        Assert.Same(draft, app.Main.Draft);
        Assert.Single(app.Edge.Requests, r => r.Method == "GET");
        Assert.Single(app.Main.Log, e => e.Message.StartsWith("Cấu hình trên Edge đã đổi ở nơi khác", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedElsewhere_Dirty_WarnsAgainAfterTheEdgeCameBackToTheDraftsRevision()
    {
        var app = await DirtyAsync();

        app.Monitor.Apply(Status("rev-2", "a-web", "applied"));
        app.Monitor.Apply(Status("rev-1", "a-web2", "applied"));
        app.Monitor.Apply(Status("rev-2", "a-web3", "applied"));

        Assert.Equal(2, app.Main.Log.Count(e => e.Message.StartsWith("Cấu hình trên Edge đã đổi ở nơi khác", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ChangedElsewhere_WhileADialogIsOpen_WarnsAndDoesNotReload()
    {
        SaveApp? app = null;
        app = await StartAsync(dialog: _ =>
        {
            app!.Edge.Revision = "rev-2";
            app.Monitor.Apply(Status("rev-2", "a-web", "applied"));
            return Task.CompletedTask;
        });
        var draft = app.Main.Draft;

        await app.Main.AddChannelAsync(); // opened, then cancelled

        Assert.Same(draft, app.Main.Draft);
        Assert.Single(app.Edge.Requests, r => r.Method == "GET");
        Assert.Single(app.Main.Log, e => e.Message.StartsWith("Cấu hình trên Edge đã đổi ở nơi khác", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedElsewhere_WhileBusy_WarnsAndDoesNotReload()
    {
        var app = await DirtyAsync();
        var draft = app.Main.Draft;
        app.Prompts.Passwords.Enqueue(null);
        app.Prompts.OnPassword = () =>
        {
            app.Edge.Revision = "rev-2";
            app.Monitor.Apply(Status("rev-2", "a-web", "applied"));
        };

        Assert.False(await Ended(app.Main.SaveAsync()));

        Assert.Same(draft, app.Main.Draft);
        Assert.Single(app.Edge.Requests, r => r.Method == "GET");
        Assert.Single(app.Main.Log, e => e.Message.StartsWith("Cấu hình trên Edge đã đổi ở nơi khác", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePassword_IsOnlyInThePutBody_NeverInTheLogOrAProperty()
    {
        var app = await DirtyAsync();
        app.Edge.Puts.Enqueue(() => Reply(HttpStatusCode.Unauthorized, """{"error":"wrong-password"}"""));
        app.Prompts.Passwords.Enqueue("first-try-password");

        var save = app.Main.SaveAsync();
        await Until(() => app.Main.SaveField == Waiting);
        app.Monitor.Apply(Status("rev-2", Ours, "applied"));
        Assert.True(await save);

        foreach (var secret in new[] { Password, "first-try-password" })
        {
            Assert.Single(app.Edge.Requests, r => r.Body.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(app.Main.Log, e => (e.Source + e.Message).Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, Texts(app.Main));
            Assert.DoesNotContain(secret, Texts(app.Main.Draft!));
        }
    }

    private static List<string> Texts(object target) =>
        [.. target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Select(p => p.GetValue(target))
            .OfType<string>()];

    private static (bool, bool, bool, bool, bool, bool, bool, bool, bool) Rules(MainViewModel vm) =>
        (vm.CanSave, vm.CanAddChannel, vm.CanAddDevice, vm.CanAddTag, vm.CanEdit, vm.CanDelete, vm.CanCheck, vm.CanTestConnection, vm.CanReload);

    private static TreeNode Node(MainViewModel vm, string device) =>
        vm.Tree.SelectMany(c => c.Children).Single(n => n.Device?.Name == device);

    /// <summary>Waits for work the view model continues on its own (the test cannot await it directly).</summary>
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "timed out waiting for the view model");
    }

    /// <summary>A save expected to end without the apply wait: one that reaches it would hang on the fake clock.</summary>
    private static Task<bool> Ended(Task<bool> save) => save.WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task<SaveApp> StartAsync(Func<DialogRequest, Task>? dialog = null)
    {
        var app = new SaveApp(dialog);
        await app.Main.LoadAsync();
        app.Monitor.Apply(Status("rev-1", null, "applied"));
        return app;
    }

    /// <summary>Loaded, with the tag Speed deleted: a dirty draft ready to save.</summary>
    private static async Task<SaveApp> DirtyAsync()
    {
        var app = await StartAsync();
        app.Main.SelectedNode = Node(app.Main, "Line");
        app.Main.SelectedTag = app.Main.Tags.Single(t => t.Name == "Speed");
        await app.Main.DeleteAsync();
        app.Main.SelectedNode = null;
        app.Prompts.Confirmations.Clear();
        Assert.True(app.Main.Draft!.IsDirty);
        return app;
    }

    private static SettingsSnapshot Snapshot(string revision) => new(
        revision,
        "file",
        new ChannelTree([
            new ChannelSettings("Siemens", "S7", [
                new DeviceSettings("Line", "S7-1200", "10.0.0.1", 102, 0, 1, 3000, 1000, true, false, false, [
                    new TagSettings("Speed", "DB1.DBW2", "Int", null, false, null, null, null),
                    new TagSettings("Temp", "DB1.DBD4", "Real", null, false, null, null, null)]),
                new DeviceSettings("Press", "S7-1200", "10.0.0.2", 102, 0, 1, 3000, 1000, true, false, false, [
                    new TagSettings("Count", "DB1.DBW0", "Int", null, false, null, null, null)])])]),
        [new PlcModelInfo("S7-1200", "S7", 102, 0, 1, null, ["Bool", "Int", "Real"], true)],
        [],
        false,
        true);

    private static EdgeStatus Status(string revision, string? applyId, string state, string? error = null) => new(
        "e1",
        Now,
        null,
        Now.AddMilliseconds(-500),
        null,
        null,
        [Device("Line"), Device("Press")],
        new MqttInfo("broker", 8883, true),
        0,
        [],
        new SettingsApplyInfo(revision, applyId, state, error));

    private static DeviceInfo Device(string name) => new(name, "S7-1200", "10.0.0.1", 102, "connected", Now, null, 5, 1);

    private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed record Request(string Method, string Path, string Body);

    /// <summary>The Edge's settings API: GET serves <see cref="Revision"/>; PUT takes the queued replies, then accepts.</summary>
    private sealed class ScriptedEdge
    {
        private readonly List<Request> _requests = [];

        public string Revision { get; set; } = "rev-1";

        public string Validate { get; set; } = """{"ok":true,"errors":[]}""";

        public Queue<Func<HttpResponseMessage>> Puts { get; } = new();

        public bool FailGets { get; set; }

        public IReadOnlyList<Request> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public RecordingHandler Handler() => new(async (request, _) =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            var path = request.RequestUri!.AbsolutePath;
            lock (_requests)
            {
                _requests.Add(new Request(request.Method.Method, path, body));
            }

            return (request.Method.Method, path) switch
            {
                ("GET", "/api/settings") when FailGets => Reply(HttpStatusCode.ServiceUnavailable, "{}"),
                ("GET", "/api/settings") => Reply(HttpStatusCode.OK, JsonSerializer.Serialize(Snapshot(Revision), JsonSerializerOptions.Web)),
                ("POST", "/api/settings/validate") => Reply(HttpStatusCode.OK, Validate),
                ("PUT", "/api/settings") => Puts.TryDequeue(out var reply)
                    ? reply()
                    : Reply(HttpStatusCode.Accepted, $$"""{"applyId":"{{Ours}}"}"""),
                _ => Reply(HttpStatusCode.NotFound, "{}"),
            };
        });
    }

    private sealed class SaveApp
    {
        public SaveApp(Func<DialogRequest, Task>? dialog)
        {
            Time = new FakeTimeProvider(Now);
            Time.SetLocalTimeZone(TimeZoneInfo.Utc);
            Monitor = new MonitorViewModel(Time);
            var opener = new Opener(dialog ?? (_ => Task.CompletedTask), () => Dialogs++);
            Main = new MainViewModel(Monitor, new SettingsClient(5099, Edge.Handler()), Prompts, opener, Time, 5099);
        }

        public ScriptedEdge Edge { get; } = new();

        public FakeTimeProvider Time { get; }

        public MonitorViewModel Monitor { get; }

        public MainViewModel Main { get; }

        public Prompts Prompts { get; } = new();

        public int Dialogs { get; private set; }
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
        /// <summary>Typed passwords, in order; once empty, the right one.</summary>
        public Queue<string?> Passwords { get; } = new();

        public Action? OnPassword { get; set; }

        public int PasswordAsks { get; private set; }

        public bool Answer { get; set; } = true;

        public List<string> Confirmations { get; } = [];

        public bool ReloadStale { get; set; }

        public List<string> StaleAsks { get; } = [];

        public CloseChoice Close { get; set; } = CloseChoice.Cancel;

        public int CloseAsks { get; private set; }

        public bool ExitWhileBusy { get; set; }

        public int BusyCloseAsks { get; private set; }

        public Task<string?> AskPasswordAsync(string purpose)
        {
            PasswordAsks++;
            OnPassword?.Invoke();
            return Task.FromResult(Passwords.TryDequeue(out var typed) ? typed : Password);
        }

        public Task<bool> ConfirmAsync(string message)
        {
            Confirmations.Add(message);
            return Task.FromResult(Answer);
        }

        public Task<bool> AskReloadStaleAsync(string message)
        {
            StaleAsks.Add(message);
            return Task.FromResult(ReloadStale);
        }

        public Task<CloseChoice> AskCloseAsync()
        {
            CloseAsks++;
            return Task.FromResult(Close);
        }

        public Task<bool> AskExitWhileBusyAsync()
        {
            BusyCloseAsks++;
            return Task.FromResult(ExitWhileBusy);
        }
    }
}
