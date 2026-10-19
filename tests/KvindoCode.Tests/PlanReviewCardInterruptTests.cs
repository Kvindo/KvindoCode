using System;
using System.Collections.Generic;
using System.Linq;
using KvindoCode.App.Views;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A plan-review card must not keep spinning after the turn that owned it has ended.
/// </summary>
/// <remarks>
/// Reported 2026-10-19: "Looks like stop doesnt work while 'Reviewing plan (round 2/2)…'". The turn really did stop —
/// measured, cancellation ends it in ~20 ms — but nothing ever FINISHED the review card, so it kept its pulsing dot and
/// "the reviewer is criticising the plan…" for ever. The screen said the review was still running while the session was
/// idle, which reads exactly like a Stop that did nothing.
/// </remarks>
public sealed class PlanReviewCardInterruptTests
{
    // AvaloniaFact: the card's pulse animation needs the platform's animation clock, which only exists once the
    // headless application is set up.
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_running_review_card_is_closed_when_the_turn_ends()
    {
        var transcript = new TranscriptView();
        // a review round starts and is still running
        transcript.Handle(new PlanReviewEvent(2, 2, "partial critique", true, false));
        Assert.True(transcript.IsReviewingPlan, "the card should be running right after the round started");

        // the user presses Stop: the turn ends without the reviewer ever finishing
        transcript.Handle(new TurnEndEvent("interrupted"));

        Assert.False(transcript.IsReviewingPlan, "the review card is still running after the turn ended — it will pulse for ever");
        var status = string.Join(" | ", transcript.ReviewCardsForTest.Select(c => c.StatusForTest));
        Assert.Contains("interrupted", status);
    }
}
