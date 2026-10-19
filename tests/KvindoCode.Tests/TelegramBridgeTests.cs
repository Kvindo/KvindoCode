using System;
using System.Linq;
using System.Reflection;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Telegram;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The Telegram → COORDINATOR bridge: off unless a tag is configured, and it names the tag it looks for.</summary>
/// <remarks>Asked 2026-10-19: "i want setting that will send my messages in telegram that contain @kc be sent to
/// coordinator so that i can ask agent from telegram chat and receive response here as reply to my message".</remarks>
public sealed class TelegramBridgeTests(ITestOutputHelper o)
{
    [Fact]
    public void The_bridge_is_off_by_default()
    {
        var s = new AppSettings();
        o.WriteLine($"tag='{s.TelegramCoordinatorTag}' poll={s.TelegramCoordinatorPollSeconds}s");
        Assert.Equal("", s.TelegramCoordinatorTag);        // nothing polls Telegram unless it is switched on
        Assert.InRange(s.TelegramCoordinatorPollSeconds, 5, 300);
    }

    [Fact]
    public void The_tag_matches_case_insensitively_and_is_stripped_from_the_question()
    {
        const string tag = "@kc";
        const string message = "@KC what is the disk usage on the staging db?";
        Assert.True(TelegramBridgeRule.ShouldAnswer(message, tag, isOwnReply: false));
        var question = TelegramBridgeRule.QuestionOf(message, tag);
        o.WriteLine("question: " + question);
        Assert.Equal("what is the disk usage on the staging db?", question);
        // and a message that merely mentions something else is not picked up
        Assert.False(TelegramBridgeRule.ShouldAnswer("deploy to kc-prod please", tag, false));
    }

    /// <summary>
    /// The human's OWN message in a group is answered; the bridge's own reply is not.
    /// </summary>
    /// <remarks>
    /// This is the case that was broken twice over (2026-10-19: "I wrote @kc in group 'Флудилка' but coordinator seems to
    /// miss my message", then "now i configured it, still doesnt respond"). The poller read DIRECT chats only and skipped
    /// messages the account had sent — but the bridge runs as the user, so the person asking is the sender and every
    /// tagged message was dropped. Neither the chat's kind nor the direction may be a reason to ignore a message; the
    /// ONLY thing to skip is the bridge's own reply, which would otherwise be answered again for ever.
    /// </remarks>
    [Fact]
    public void A_group_message_from_the_human_is_answered_and_our_own_reply_is_not()
    {
        const string tag = "@kc";
        const string asked = "@kc are you here?";
        o.WriteLine($"human's own group message -> {TelegramBridgeRule.ShouldAnswer(asked, tag, isOwnReply: false)}");
        Assert.True(TelegramBridgeRule.ShouldAnswer(asked, tag, isOwnReply: false));
        Assert.Equal("are you here?", TelegramBridgeRule.QuestionOf(asked, tag));

        // the bridge posts AS the user, so its own reply comes back from Telegram looking exactly like the question —
        // the recorded id is the only thing that tells them apart
        Assert.False(TelegramBridgeRule.ShouldAnswer("Yes, I am here.", tag, isOwnReply: true));
        // ...and a reply that happens to contain the tag is still skipped
        Assert.False(TelegramBridgeRule.ShouldAnswer("@kc yes, I am here.", tag, isOwnReply: true));
    }

    [Fact]
    public void An_empty_tag_or_message_is_never_answered()
    {
        Assert.False(TelegramBridgeRule.ShouldAnswer("@kc hello", "", false));
        Assert.False(TelegramBridgeRule.ShouldAnswer("   ", "@kc", false));
        Assert.False(TelegramBridgeRule.ShouldAnswer(null, "@kc", false));
        Assert.Equal("", TelegramBridgeRule.QuestionOf("@kc", "@kc"));      // the tag alone is not a question
    }
}
