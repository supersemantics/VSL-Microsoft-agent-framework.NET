// Purchase auto-approval agent gated by VSL through SuperSemantics.Vsl.AgentFramework.
//
// The same agent as vsl-maf's examples/financial-agents/purchase_approval_agent.py:
//   - Invariant "purchase-hard-cap": the amount must never exceed $1000.
//   - PreNode "purchase-within-budget": budget / amount, a ratio where 1.0 is break-even, must be
//     above 1.1. Every decision is recorded with WriteGateDecision.
// Neither gate involves a model at the guarded call: F1 = true, F2 = None (Low). They block what
// they deny; they do not shape what the model produces.
//
// Runs offline: ScriptedClient stands in for the model and asks for the purchase in each request.
// For a real model, pass any IChatClient instead, for example an Azure OpenAI or Foundry client
// from its own package, and keep everything else as it is.

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SuperSemantics.Vsl;
using SuperSemantics.Vsl.AgentFramework;

const string AgentId = "purchase-agent";
const double HardCap = 1000.0;
const double DailyBudgetRemaining = 500.0;
const string BudgetGate = "purchase-within-budget";
const string CapGate = "purchase-hard-cap";

var noModelShaping = new AssuranceBasis(f1PreCommitment: true, F2Modification.None);
var ledger = new VerbaLedger(new JsonlLedgerStore("purchase-ledger.jsonl", fsync: true));
string decisionId = "";

var adapter = new MafAdapter(AgentId);
var capGate = adapter.CompileInvariant(new Invariant(
    CapGate,
    $"Amount must never exceed ${HardCap}.",
    candidate =>
    {
        var amount = VslToolArguments.GetDouble((IReadOnlyDictionary<string, object?>)candidate!, "amount");
        var holds = amount > 0 && amount <= HardCap;
        ledger.WriteGateDecision(AgentId, CapGate, allowed: holds, decisionId: decisionId);
        return Task.FromResult(holds);
    },
    noModelShaping));

var budgetGate = adapter.CompilePreNode(new PreNode(
    BudgetGate,
    "A purchase must leave headroom in the remaining daily budget.",
    candidate =>
    {
        var amount = VslToolArguments.GetDouble((IReadOnlyDictionary<string, object?>)candidate!, "amount");
        // 0.0 for a missing or bad amount, so the gate fails closed.
        var estimate = new GammaEstimate(amount > 0 ? DailyBudgetRemaining / amount : 0.0);
        ledger.WriteGateDecision(AgentId, BudgetGate, estimate: estimate, gammaThreshold: Thresholds.RobustGammaDefault, decisionId: decisionId);
        return Task.FromResult(estimate);
    },
    noModelShaping,
    gammaThreshold: Thresholds.RobustGammaDefault));

var approvePurchase = AIFunctionFactory.Create(
    (double amount, string vendor) => $"APPROVED: ${amount:F2} from {vendor}",
    "approve_purchase",
    "Approve a purchase transaction.");

foreach (var (request, amount, vendor) in new[]
{
    ("Approve a $250 purchase from Contoso.", 250.0, "Contoso"),      // within budget: approved
    ("Approve a $480 purchase from Fabrikam.", 480.0, "Fabrikam"),    // 500/480 = 1.04, not above 1.1: denied
    ("Approve a $5000 purchase from Northwind.", 5000.0, "Northwind"), // above the hard cap: blocked
})
{
    decisionId = Guid.NewGuid().ToString();
    IChatClient model = new ScriptedClient(amount, vendor);

    // UseVslGates runs the gates in the order listed before every approve_purchase call:
    // hard cap, then budget, then the tool (only if both pass).
    var agent = new ChatClientAgent(model, instructions: "You approve purchases. Always call approve_purchase.", name: "PurchaseAgent", tools: [approvePurchase])
        .AsBuilder()
        .UseVslGates([capGate, budgetGate], functionName: "approve_purchase")
        .Build();

    Console.WriteLine($"\nUser: {request}");
    try
    {
        var response = await agent.RunAsync(request);
        Console.WriteLine($"Agent: {response.Text}");
    }
    catch (VslGateDeniedException denied)
    {
        var hardCap = denied.Denial is InvariantViolationException;
        Console.WriteLine($"  {(hardCap ? "BLOCKED (hard cap)" : "DENIED (budget)")}: {denied.Denial.Reason}");
        // A denial is drift as far as Audit() is concerned: record what happened next.
        ledger.Write(hardCap ? LedgerEntryType.Terminal : LedgerEntryType.PreNode, AgentId, decisionId: decisionId,
            payload: new Dictionary<string, object?> { ["escalated_to"] = "finance-approvers", ["reason"] = denied.Denial.Reason });
    }
}

Console.WriteLine($"\nLedger intact: {ledger.VerifyIntegrity()}");

/// <summary>Stands in for a model: asks for approve_purchase once, then summarises the result.</summary>
sealed class ScriptedClient(double amount, string vendor) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = messages.Last().Contents.OfType<FunctionResultContent>().FirstOrDefault();
        return Task.FromResult(result is not null
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Done: {result.Result}"))
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "approve_purchase",
                new Dictionary<string, object?> { ["amount"] = amount, ["vendor"] = vendor })])));
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
