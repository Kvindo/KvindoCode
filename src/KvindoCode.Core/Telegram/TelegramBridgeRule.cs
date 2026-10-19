namespace KvindoCode.Core.Telegram;

/// <summary>
/// Which Telegram messages the COORDINATOR bridge answers. Pure, so the rule can be tested without a network session.
/// </summary>
/// <remarks>
/// The bridge reads Telegram as the USER's own account, which makes two things easy to get wrong — both were, and both
/// produced "coordinator seems to miss my message" (2026-10-19):
/// <list type="bullet">
/// <item>Only DIRECT chats were scanned, so a tag written in a group or supergroup was never seen. The chat's KIND is
/// therefore not part of this rule at all: the caller scans every dialog.</item>
/// <item>Messages the account sent were skipped as "not from me" — but the human asking IS that account, so every
/// tagged message was skipped. Direction is not a reason to ignore a message; the only thing to skip is the bridge's
/// OWN reply, which would otherwise be answered again for ever.</item>
/// </list>
/// </remarks>
public static class TelegramBridgeRule
{
    /// <summary>Should this message be handed to the COORDINATOR?</summary>
    /// <param name="text">The message text.</param>
    /// <param name="tag">The configured tag, e.g. <c>@kc</c>. Empty means the bridge is off.</param>
    /// <param name="isOwnReply">True when the id was recorded as posted by this bridge (see the caller's reply set).</param>
    public static bool ShouldAnswer(string? text, string? tag, bool isOwnReply)
    {
        if (isOwnReply) return false;                       // never answer what the bridge itself posted
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = (tag ?? "").Trim();
        if (t.Length == 0) return false;                    // no tag configured: the bridge is off
        return text.Contains(t, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The question, with the tag removed — empty when the message was nothing but the tag.</summary>
    public static string QuestionOf(string text, string? tag)
    {
        var t = (tag ?? "").Trim();
        var q = t.Length == 0 ? text : text.Replace(t, "", StringComparison.OrdinalIgnoreCase);
        return q.Trim();
    }
}
