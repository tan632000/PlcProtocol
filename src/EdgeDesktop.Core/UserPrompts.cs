using System.Globalization;

namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>What the view models ask the operator; the window implements it with dialogs, the tests with fakes.</summary>
public interface IUserPrompts
{
    /// <summary>The settings password typed for <paramref name="purpose"/>; null or empty means cancelled.</summary>
    Task<string?> AskPasswordAsync(string purpose);

    Task<bool> ConfirmAsync(string message);
}

/// <summary>The confirmation texts, naming what goes with what is deleted.</summary>
public static class Confirmations
{
    public static string Delete(DraftChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var tags = channel.Devices.Sum(d => d.Tags.Count);
        return channel.Devices.Count == 0
            ? $"Xoá Channel {channel.Name}?"
            : $"Xoá Channel {channel.Name}? Kèm theo {channel.Devices.Count} PLC và {tags} tag.";
    }

    public static string Delete(DraftDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Tags.Count == 0 ? $"Xoá PLC {device.Name}?" : $"Xoá PLC {device.Name}? Kèm theo {device.Tags.Count} tag.";
    }

    public static string Delete(DraftTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return $"Xoá tag {tag.Name}?";
    }
}

/// <summary>The operator's text for each refusal of the settings API (plan D-04 and D-05).</summary>
public static class RefusalMessages
{
    public static string Describe(ApiRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return refusal.Kind switch
        {
            RefusalKind.EmptyPassword => "Chưa nhập mật khẩu",
            RefusalKind.WrongPassword => "Sai mật khẩu",
            RefusalKind.PasswordNotSet =>
                "Chưa đặt mật khẩu settings: chạy IndustrialIot.Edge.exe set-settings-password --service (admin)",
            RefusalKind.Locked =>
                $"Sai mật khẩu quá nhiều lần, thử lại sau {(refusal.RetryAfterSeconds ?? 60).ToString(CultureInfo.InvariantCulture)} giây",
            RefusalKind.StaleRevision => "Cấu hình trên Edge đã đổi ở nơi khác",
            RefusalKind.ApplyInProgress => "Edge đang áp dụng một lần lưu khác, thử lại sau",
            RefusalKind.Invalid => "Có ô chưa hợp lệ",
            RefusalKind.TooLarge => "Cấu hình quá lớn",
            RefusalKind.EndpointInUse => "PLC này đang chạy với cấu hình khác: lưu trước rồi test",
            RefusalKind.TestRunning => "Một lần test khác đang chạy",
            RefusalKind.RateLimited => "Đã test quá 10 lần trong một phút, đợi một chút",
            _ => refusal.Detail ?? "không kết nối được",
        };
    }
}
