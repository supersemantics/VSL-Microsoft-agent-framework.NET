using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SuperSemantics.Vsl.Conformance;

namespace SuperSemantics.Vsl.AgentFramework.Tests;

/// <summary>
/// End-to-end through a real <see cref="ChatClientAgent"/> and the Agent Framework's own
/// function-invocation loop. A scripted chat client asks for tool calls; no model service or
/// credentials are involved.
/// </summary>
public class RealAgentTests
{
    private const double PurchaseLimit = 1000.0;
    private static readonly AssuranceBasis NoModelShaping = new(true, F2Modification.None);

    private sealed class Tools
    {
        public List<double> Purchases { get; } = [];
        public int Notes { get; set; }

        public AIFunction Approve => AIFunctionFactory.Create((double amount) => { Purchases.Add(amount); return $"APPROVED {amount}"; }, "approve_purchase");
        public AIFunction Note => AIFunctionFactory.Create((string text) => { Notes++; return "noted"; }, "log_note");
    }

    /// <summary>Asks for the scripted calls once, then reports what came back.</summary>
    private sealed class ScriptedClient(params FunctionCallContent[] calls) : IChatClient
    {
        public int ModelCalls { get; private set; }
        public List<FunctionResultContent> ToolResults { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ModelCalls++;
            var results = messages.Last().Contents.OfType<FunctionResultContent>().ToList();
            if (results.Count > 0)
            {
                ToolResults.AddRange(results);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. calls])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static FunctionCallContent Buy(object amount) => new("c1", "approve_purchase", new Dictionary<string, object?> { ["amount"] = amount });

    private static FunctionCallContent NoteCall() => new("c2", "log_note", new Dictionary<string, object?> { ["text"] = "after" });

    private static CompiledGate HeadroomGate(VerbaLedger? ledger = null, string identity = "purchase-agent") =>
        new MafAdapter(identity).CompilePreNode(new PreNode(
            "purchase-within-limit",
            "Purchases stay comfortably inside the limit.",
            candidate =>
            {
                var amount = VslToolArguments.GetDouble((IReadOnlyDictionary<string, object?>)candidate!, "amount");
                var estimate = new GammaEstimate(amount > 0 ? PurchaseLimit / amount : 0.0);
                ledger?.WriteGateDecision(identity, "purchase-within-limit", estimate: estimate, gammaThreshold: Thresholds.RobustGammaDefault);
                return Task.FromResult(estimate);
            },
            NoModelShaping));

    private static CompiledGate HardCapGate() =>
        new MafAdapter().CompileInvariant(new Invariant(
            "purchase-hard-cap",
            "Never above the cap.",
            candidate => Task.FromResult(VslToolArguments.GetDouble((IReadOnlyDictionary<string, object?>)candidate!, "amount") is > 0 and <= PurchaseLimit),
            NoModelShaping));

    private static AIAgent Agent(IChatClient client, Tools tools, Func<AIAgentBuilder, AIAgentBuilder> gates) =>
        gates(new ChatClientAgent(client, instructions: "Approve purchases.", tools: [tools.Approve, tools.Note]).AsBuilder()).Build();

    [Fact]
    public async Task The_adapter_is_conformant()
    {
        Assert.Empty(await ConformanceSuite.RunAsync(new MafAdapter()));
    }

    [Fact]
    public async Task An_allowed_call_runs_the_tool()
    {
        var tools = new Tools();
        var client = new ScriptedClient(Buy(250.0));
        var response = await Agent(client, tools, b => b.UseVslGate(HeadroomGate(), "approve_purchase")).RunAsync("buy");
        Assert.Equal([250.0], tools.Purchases);
        Assert.Equal("done", response.Text);
    }

    [Fact]
    public async Task A_denial_aborts_the_run_and_the_tool_never_runs()
    {
        var tools = new Tools();
        var client = new ScriptedClient(Buy(5000.0));
        var agent = Agent(client, tools, b => b.UseVslGate(HeadroomGate(), "approve_purchase"));
        var denied = await Assert.ThrowsAsync<VslGateDeniedException>(() => agent.RunAsync("buy"));
        Assert.Empty(tools.Purchases);
        Assert.Equal(1, client.ModelCalls); // the model was never handed a result to carry on from
        Assert.Equal("purchase-agent", denied.Denial.IdentityKey);
        Assert.Equal("approve_purchase", denied.FunctionName);
        Assert.Same(denied.Denial, denied.InnerException);
    }

    [Fact]
    public async Task Abort_also_stops_the_rest_of_the_batch()
    {
        var tools = new Tools();
        var client = new ScriptedClient(Buy(5000.0), NoteCall());
        var agent = Agent(client, tools, b => b.UseVslGate(HeadroomGate(), "approve_purchase"));
        await Assert.ThrowsAsync<VslGateDeniedException>(() => agent.RunAsync("buy"));
        Assert.Empty(tools.Purchases);
        Assert.Equal(0, tools.Notes);
    }

    [Fact]
    public async Task Return_to_model_hands_the_model_an_error_and_the_run_continues()
    {
        var tools = new Tools();
        var client = new ScriptedClient(Buy(5000.0));
        var response = await Agent(client, tools, b => b.UseVslGate(HeadroomGate(), "approve_purchase", onDenial: VslDenialMode.ReturnToModel)).RunAsync("buy");
        Assert.Empty(tools.Purchases);
        Assert.Equal("done", response.Text);
        Assert.IsAssignableFrom<AutomationDeniedException>(Assert.Single(client.ToolResults).Exception);
    }

    [Fact]
    public async Task An_invariant_denial_keeps_its_specific_type()
    {
        var tools = new Tools();
        var agent = Agent(new ScriptedClient(Buy(5000.0)), tools, b => b.UseVslGate(HardCapGate()));
        var denied = await Assert.ThrowsAsync<VslGateDeniedException>(() => agent.RunAsync("buy"));
        Assert.IsType<InvariantViolationException>(denied.Denial);
        Assert.Empty(tools.Purchases);
    }

    [Fact]
    public async Task UseVslGates_runs_gates_in_the_listed_order_and_stops_at_the_first_denial()
    {
        var tools = new Tools();
        var order = new List<string>();
        CompiledGate Recording(string name, CompiledGate inner) => async input => { order.Add(name); await inner(input); };
        var agent = Agent(new ScriptedClient(Buy(5000.0)), tools, b =>
            b.UseVslGates([Recording("cap", HardCapGate()), Recording("budget", HeadroomGate())], "approve_purchase"));
        var denied = await Assert.ThrowsAsync<VslGateDeniedException>(() => agent.RunAsync("buy"));
        Assert.IsType<InvariantViolationException>(denied.Denial);
        Assert.Equal(["cap"], order);
        Assert.Empty(tools.Purchases);
    }

    [Fact]
    public async Task Separate_UseVslGate_calls_run_in_the_frameworks_order_last_registered_first()
    {
        // Pins the Agent Framework for .NET behaviour the docs describe; if a release changes it,
        // this test says so.
        var order = new List<string>();
        CompiledGate Recording(string name) => _ => { order.Add(name); return Task.CompletedTask; };
        await Agent(new ScriptedClient(Buy(250.0)), new Tools(), b => b.UseVslGate(Recording("first")).UseVslGate(Recording("second"))).RunAsync("buy");
        Assert.Equal(["second", "first"], order);
    }

    [Fact]
    public async Task A_failing_gate_fails_closed()
    {
        var tools = new Tools();
        CompiledGate broken = _ => throw new InvalidOperationException("judge service down");
        var agent = Agent(new ScriptedClient(Buy(250.0)), tools, b => b.UseVslGate(broken));
        var failed = await Assert.ThrowsAsync<VslGateFailedException>(() => agent.RunAsync("buy"));
        Assert.IsType<InvalidOperationException>(failed.InnerException);
        Assert.Empty(tools.Purchases);
    }

    [Fact]
    public async Task Other_tools_pass_through_a_scoped_gate()
    {
        var tools = new Tools();
        var agent = Agent(new ScriptedClient(NoteCall()), tools, b => b.UseVslGate(HeadroomGate(), "approve_purchase"));
        await agent.RunAsync("note");
        Assert.Equal(1, tools.Notes);
    }

    [Fact]
    public async Task Json_arguments_from_a_model_are_read_as_plain_values()
    {
        var tools = new Tools();
        using var json = JsonDocument.Parse("""{"amount": 5000}""");
        var agent = Agent(new ScriptedClient(Buy(json.RootElement.GetProperty("amount").Clone())), tools, b => b.UseVslGate(HeadroomGate(), "approve_purchase"));
        await Assert.ThrowsAsync<VslGateDeniedException>(() => agent.RunAsync("buy"));
        Assert.Empty(tools.Purchases);
    }

    [Fact]
    public async Task A_streaming_run_throws_when_the_stream_ends()
    {
        var tools = new Tools();
        var agent = Agent(new ScriptedClient(Buy(5000.0)), tools, b => b.UseVslGate(HeadroomGate(), "approve_purchase"));
        await Assert.ThrowsAsync<VslGateDeniedException>(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("buy"))
            {
            }
        });
        Assert.Empty(tools.Purchases);
    }

    [Fact]
    public async Task Gate_decisions_are_recorded_by_the_application()
    {
        var ledger = new VerbaLedger();
        await Agent(new ScriptedClient(Buy(250.0)), new Tools(), b => b.UseVslGate(HeadroomGate(ledger), "approve_purchase")).RunAsync("buy");
        await Assert.ThrowsAsync<VslGateDeniedException>(() =>
            Agent(new ScriptedClient(Buy(5000.0)), new Tools(), b => b.UseVslGate(HeadroomGate(ledger), "approve_purchase")).RunAsync("buy"));
        var payloads = ledger.Store.AllEntries().Select(e => e.Payload).ToList();
        Assert.Equal([true, false], payloads.Select(p => (bool)p["allowed"]!));
        Assert.True(ledger.VerifyIntegrity());
    }
}

public class AdapterTests
{
    private static readonly AssuranceBasis FullClaim = new(true, F2Modification.Full);

    private static async Task<GammaEstimate> JudgeWithAChatClient(IChatClient judge, object? candidate)
    {
        var verdict = await judge.GetResponseAsync("Is this purchase reasonable? " + candidate);
        return new GammaEstimate(verdict.Text == "yes" ? 2.0 : 0.0);
    }

    [Fact]
    public void A_full_claim_on_a_hosted_model_judge_warns()
    {
        IChatClient judge = null!;
        var warnings = new List<string>();
        new MafAdapter(onWarning: warnings.Add).CompilePreNode(new PreNode("judge", "LLM judge", c => JudgeWithAChatClient(judge, c), FullClaim));
        Assert.Contains(warnings, w => w.Contains("hosted model API", StringComparison.Ordinal) && w.Contains("Microsoft.Extensions.AI.", StringComparison.Ordinal));
    }

    [Fact]
    public void An_async_lambda_calling_a_chat_client_directly_is_followed_into_its_state_machine()
    {
        IChatClient judge = null!;
        var warnings = new List<string>();
        new MafAdapter(onWarning: warnings.Add).CompilePreNode(new PreNode("judge", "d", async c =>
        {
            var verdict = await judge.GetResponseAsync("judge " + c);
            return new GammaEstimate(verdict.Text.Length);
        }, FullClaim));
        Assert.Single(warnings);
    }

    [Fact]
    public void No_warning_without_a_model_or_without_a_full_claim()
    {
        IChatClient judge = null!;
        var warnings = new List<string>();
        var adapter = new MafAdapter(onWarning: warnings.Add);
        adapter.CompilePreNode(new PreNode("cap", "d", _ => Task.FromResult(new GammaEstimate(2.0)), FullClaim));
        adapter.CompilePreNode(new PreNode("judge", "d", c => JudgeWithAChatClient(judge, c), new AssuranceBasis(true, F2Modification.Partial)));
        Assert.Empty(warnings);
    }

    [Fact]
    public void A_non_default_fallback_warns_that_it_is_not_consumed()
    {
        var warnings = new List<string>();
        new MafAdapter(onWarning: warnings.Add).CompilePreNode(new PreNode("p", "d", _ => Task.FromResult(new GammaEstimate(2.0)),
            new AssuranceBasis(true, F2Modification.None), fallback: new Fallback(MaxRetries: 5)));
        Assert.Contains(warnings, w => w.Contains("does not consume Fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void Identity_key_must_be_given()
    {
        Assert.Throws<ArgumentException>(() => new MafAdapter(""));
    }

    [Fact]
    public void GetDouble_fails_closed_on_missing_or_bad_values()
    {
        var args = new Dictionary<string, object?> { ["a"] = 2L, ["b"] = "3.5", ["c"] = "abc", ["d"] = double.NaN };
        Assert.Equal(2.0, VslToolArguments.GetDouble(args, "a"));
        Assert.Equal(3.5, VslToolArguments.GetDouble(args, "b"));
        Assert.Equal(0.0, VslToolArguments.GetDouble(args, "c"));
        Assert.Equal(0.0, VslToolArguments.GetDouble(args, "d"));
        Assert.Equal(0.0, VslToolArguments.GetDouble(args, "missing"));
    }
}
