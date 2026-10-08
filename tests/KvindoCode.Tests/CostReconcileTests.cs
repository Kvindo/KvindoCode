using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The response header's cost can differ from what the gateway really billed; the session must follow the ledger.</summary>
public sealed class CostReconcileTests
{
    sealed class LedgerClient(double headerCost, double? ledgerCost) : ILlmClient
    {
        public int LedgerCalls;
        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult(new List<ModelInfo>());
        public Task<LlmResult> StreamAsync(LlmRequest request, LlmCallbacks? callbacks, CancellationToken ct)
        {
            callbacks?.OnText?.Invoke("ok");
            return Task.FromResult(new LlmResult { Content = "ok", FinishReason = "stop", Usage = new LlmUsage(1000, 5, 0), CostRub = headerCost, RequestId = "req-1" });
        }
        public Task<IReadOnlyDictionary<string, double>> LedgerPricesAsync(IReadOnlyCollection<string> requestIds, CancellationToken ct)
        {
            LedgerCalls++;
            IReadOnlyDictionary<string, double> r = ledgerCost is { } c && requestIds.Contains("req-1") ? new Dictionary<string, double> { ["req-1"] = c } : new Dictionary<string, double>();
            return Task.FromResult(r);
        }
    }

    static async Task<bool> WaitFor(Func<bool> cond, int ms = 20000)
    {
        for (var t = 0; t < ms / 50; t++) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    [Fact]
    public async Task A_call_the_gateway_billed_at_zero_is_not_counted_at_the_header_price()
    {
        using var sb = new Sandbox();
        var llm = new LedgerClient(headerCost: 1.19, ledgerCost: 0.0);
        using var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());

        await session.RunTurnAsync("hi", default);

        Assert.True(await WaitFor(() => session.CostRub < 0.001), $"cost stayed at {session.CostRub}");   // corrected from 1.19 to the ledger's 0
    }

    [Fact]
    public async Task A_call_billed_higher_than_the_header_is_raised_to_the_ledger_price()
    {
        using var sb = new Sandbox();
        var llm = new LedgerClient(headerCost: 0.5, ledgerCost: 2.0);
        using var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());

        await session.RunTurnAsync("hi", default);

        Assert.True(await WaitFor(() => Math.Abs(session.CostRub - 2.0) < 0.001), $"cost is {session.CostRub}");
    }

    [Fact]
    public async Task When_the_ledger_has_no_entry_the_header_cost_is_kept()
    {
        using var sb = new Sandbox();
        var llm = new LedgerClient(headerCost: 1.19, ledgerCost: null);
        using var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());

        await session.RunTurnAsync("hi", default);
        await WaitFor(() => llm.LedgerCalls > 0, 15000);

        Assert.True(llm.LedgerCalls > 0, "the ledger was never consulted");
        Assert.Equal(1.19, session.CostRub, 3);
    }
}
