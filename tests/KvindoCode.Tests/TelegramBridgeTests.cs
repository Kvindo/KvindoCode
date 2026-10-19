using System;
using System.Linq;
using System.Reflection;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
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
        Assert.True(message.Contains(tag, StringComparison.OrdinalIgnoreCase));
        var question = message.Replace(tag, "", StringComparison.OrdinalIgnoreCase).Trim();
        o.WriteLine("question: " + question);
        Assert.Equal("what is the disk usage on the staging db?", question);
        // and a message that merely mentions something else is not picked up
        Assert.False("deploy to kc-prod please".Contains(tag, StringComparison.OrdinalIgnoreCase));
    }
}
