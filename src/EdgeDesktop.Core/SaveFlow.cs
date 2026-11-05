namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>
/// Lưu &amp; áp dụng, Tải lại, the close guard and changes made elsewhere for the main view model (plan D-04, D-06,
/// D-08). Busy from the first check to the end of the apply wait, so nothing is sent twice; every refusal keeps the
/// draft. The password lives only in the local variable of one attempt and goes only into that one PUT body.
/// </summary>
internal sealed class SaveFlow(MainViewModel main, SettingsClient client, IUserPrompts prompts, MonitorViewModel monitor, TimeProvider time)
{
    public static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(60);

    private const string Source = "Lưu";

    // Set while this app waits for the Edge to answer its save; completed by the first status that does.
    private TaskCompletionSource<SettingsApplyInfo>? _answer;
    private string? _applyId;
    private string? _previousApplyId;

    private bool _loading;
    private bool _unconfirmed;

    // The outside revision already reported, so each change is reported once.
    private string? _noticedRevision;

    public bool IsBusy { get; private set; }

    /// <summary>The Edge is applying this app's save and its monitor server restarts: failed polls are expected.</summary>
    public bool IsWaiting => _answer is not null;

    public async Task<bool> SaveAsync()
    {
        if (main.Draft is { IsDirty: true, IsStale: true } && !IsBusy)
        {
            main.AddLog(Source, "Bản nháp đã cũ (cấu hình trên Edge đã đổi): Tải lại trước khi lưu");
            return false;
        }

        if (IsBusy || main.Draft is not { IsDirty: true, IsStale: false } draft)
        {
            return false;
        }

        SetBusy(true);
        try
        {
            return await SaveCoreAsync(draft).ConfigureAwait(true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Tải lại từ Edge: the running settings into a new draft, after a confirmation when there are edits.</summary>
    public async Task ReloadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (main.Draft is { IsDirty: true } && !await prompts.ConfirmAsync("Bỏ các thay đổi chưa lưu và tải lại cấu hình từ Edge?").ConfigureAwait(true))
        {
            return;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Whether the window may close: Đợi / Thoát không chờ while busy, Lưu / Không lưu / Huỷ with edits.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (IsBusy)
        {
            return await prompts.AskExitWhileBusyAsync().ConfigureAwait(true);
        }

        if (main.Draft is not { IsDirty: true })
        {
            return true;
        }

        return await prompts.AskCloseAsync().ConfigureAwait(true) switch
        {
            CloseChoice.Save => await SaveAsync().ConfigureAwait(true), // closes only once applied
            CloseChoice.DontSave => true,
            _ => false,
        };
    }

    /// <summary>Each applied status: the answer to a waiting save, or else a change made elsewhere (D-08).</summary>
    public void OnStatus(EdgeStatus status)
    {
        if (status.SettingsApply is not { } apply)
        {
            return;
        }

        if (_answer is { } answer)
        {
            // A status read before the PUT still names the previous apply; any other finished apply is an answer.
            if (apply.State != "applying" && apply.ApplyId is { } id && (id == _applyId || id != _previousApplyId))
            {
                answer.TrySetResult(apply);
            }

            return;
        }

        if (_loading || main.Draft is not { } draft)
        {
            return;
        }

        if (apply.Revision == draft.Revision)
        {
            _noticedRevision = null; // back to the draft's revision: a later change is news again
            return;
        }

        if (apply.Revision == _noticedRevision)
        {
            return;
        }

        _noticedRevision = apply.Revision;
        if (_unconfirmed)
        {
            _unconfirmed = false;
            main.AddLog(Source, "Edge đã áp dụng một cấu hình mới, có thể là lần lưu vừa rồi");
        }
        else if (!draft.IsDirty && !IsBusy && !main.DialogOpen)
        {
            _ = ReloadChangedElsewhereAsync();
        }
        else
        {
            // A reload now would drop the edits, or move an open dialog's edit onto another PLC.
            main.AddLog("Cấu hình", "Cấu hình trên Edge đã đổi ở nơi khác: bản nháp được giữ, Tải lại để lấy cấu hình đang chạy");
        }
    }

    private async Task<bool> SaveCoreAsync(ConfigDraft draft)
    {
        _unconfirmed = false;

        // Saved, such a channel would vanish: the Edge lists channels by their PLCs.
        if (draft.ChannelsWithoutDevices is { Count: > 0 } empty)
        {
            foreach (var channel in empty)
            {
                main.AddLog(Source, $"Channel {channel.Name} chưa có PLC");
            }

            main.Select(new ErrorLocation(empty[0], null, null, null));
            return false;
        }

        var check = await client.ValidateAsync(draft).ConfigureAwait(true);
        if (check.Refusal is { } checkRefusal)
        {
            main.AddLog(Source, $"Không lưu được: {RefusalMessages.Describe(checkRefusal)}");
            return false;
        }

        if (check.Errors.Count > 0)
        {
            main.ShowErrors(draft, check.Errors, Source);
            return false;
        }

        string? previous;
        SaveResult result;
        while (true)
        {
            var password = await prompts.AskPasswordAsync("Lưu & áp dụng").ConfigureAwait(true);
            if (string.IsNullOrEmpty(password))
            {
                return false;
            }

            // Read right before the PUT: another apply may have finished while the password was typed.
            previous = monitor.LastStatus?.SettingsApply?.ApplyId;
            result = await client.SaveAsync(draft, password).ConfigureAwait(true);

            // Asked again only after a wrong password; any other refusal ends the save (each try counts toward the lockout).
            if (result.Refusal is not { Kind: RefusalKind.WrongPassword })
            {
                break;
            }

            main.AddLog(Source, "Sai mật khẩu");
        }

        if (result.Refusal is { } refusal)
        {
            await RefusedAsync(draft, refusal).ConfigureAwait(true);
            return false;
        }

        main.AddLog(Source, "Đã gửi cấu hình, chờ Edge áp dụng");
        var apply = await WaitAsync(result.ApplyId!, previous).ConfigureAwait(true);
        if (apply is null)
        {
            _unconfirmed = true;
            main.AddLog(Source, "Edge chưa xác nhận lần lưu");
            return false;
        }

        if (apply.ApplyId != result.ApplyId)
        {
            draft.MarkStale();
            _noticedRevision = apply.Revision;
            main.AddLog(Source, "Không xác nhận được lần lưu vừa rồi (Edge đã áp dụng một lần lưu khác): Tải lại để xem cấu hình đang chạy");
            return false;
        }

        if (apply.State != "applied")
        {
            main.AddLog(Source, $"Edge không áp dụng được, cấu hình cũ vẫn chạy: {apply.Error ?? apply.State}");
            return false;
        }

        main.AddLog(Source, "Đã áp dụng");
        _noticedRevision = apply.Revision; // our own change, even if the reload below fails
        await LoadAsync().ConfigureAwait(true);
        return true;
    }

    private async Task RefusedAsync(ConfigDraft draft, ApiRefusal refusal)
    {
        switch (refusal.Kind)
        {
            case RefusalKind.Invalid:
                main.ShowErrors(draft, refusal.Errors, Source);
                break;
            case RefusalKind.StaleRevision:
                var message = RefusalMessages.Describe(refusal);
                main.AddLog(Source, message);
                if (await prompts.AskReloadStaleAsync(message).ConfigureAwait(true))
                {
                    await LoadAsync().ConfigureAwait(true);
                }
                else
                {
                    draft.MarkStale(); // Giữ để xem: kept for reading, Lưu disabled until Tải lại
                }

                break;
            default:
                main.AddLog(Source, $"Không lưu được: {RefusalMessages.Describe(refusal)}");
                break;
        }
    }

    /// <summary>The finished apply that answers this save, or null after <see cref="ApplyTimeout"/>.</summary>
    private async Task<SettingsApplyInfo?> WaitAsync(string applyId, string? previousApplyId)
    {
        _applyId = applyId;
        _previousApplyId = previousApplyId;
        using var stop = new CancellationTokenSource();
        var timeout = Task.Delay(ApplyTimeout, time, stop.Token); // started before the wait shows, so it counts from here
        var answer = new TaskCompletionSource<SettingsApplyInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = answer;
        main.Refresh();
        try
        {
            var first = await Task.WhenAny(answer.Task, timeout).ConfigureAwait(true);
            return first == answer.Task ? await answer.Task.ConfigureAwait(true) : null;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(true);
            _answer = null;
            main.Refresh();
        }
    }

    private async Task ReloadChangedElsewhereAsync()
    {
        if (await LoadAsync().ConfigureAwait(true))
        {
            main.AddLog("Cấu hình", "Cấu hình đã được đổi ở nơi khác, đã tải lại");
        }
    }

    /// <summary>Busy while the settings are read, so no edit is made on a draft about to be replaced.</summary>
    private async Task<bool> LoadAsync()
    {
        var wasBusy = IsBusy;
        SetBusy(true);
        _loading = true;
        try
        {
            var before = main.Draft;
            await main.LoadAsync().ConfigureAwait(true);
            _unconfirmed = false;
            return main.Draft != before;
        }
        finally
        {
            _loading = false;
            if (!wasBusy)
            {
                SetBusy(false);
            }
        }
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        main.Refresh();
    }
}
