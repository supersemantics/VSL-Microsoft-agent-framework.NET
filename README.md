# VSL-Microsoft-agent-framework.NET

A [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/overview/) adapter for [VSL-Core.NET](https://github.com/supersemantics/VSL-Core.NET) (`SuperSemantics.Vsl`), the .NET port of vsl-core. It is the .NET counterpart of the Python adapter [vsl-maf](https://github.com/supersemantics/VSL-Microsoft-agent-framework) and behaves the same way.

- NuGet package `SuperSemantics.Vsl.AgentFramework` (`dotnet add package SuperSemantics.Vsl.AgentFramework`), namespace `SuperSemantics.Vsl.AgentFramework`.
- Targets .NET 10. Depends on `Microsoft.Agents.AI` (tested against 1.28.0) and `SuperSemantics.Vsl` 0.1.1; NuGet installs both with this package.
- `MafAdapter` passes `ConformanceSuite` with an empty result.
- MIT licensed.

Is:

- `MafAdapter`, which compiles a `PreNode` or `Invariant` into a gate, with the agent's identity on every denial.
- `UseVslGate` / `UseVslGates`, which run gates before a tool call (F1 pre-commitment) and make a denial abort the run fail-closed.

Is not:

- A replacement for `SuperSemantics.Vsl`: the constructs, the ledger and the governance vocabulary live there.
- Automatic interception. Gates are registered explicitly on the agent builder.

## Usage

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SuperSemantics.Vsl;
using SuperSemantics.Vsl.AgentFramework;

const double RefundLimit = 500.0;
const string RefundGate = "refund-within-policy";
var ledger = new VerbaLedger(new JsonlLedgerStore("ledger.jsonl", fsync: true));

var gate = new MafAdapter("refund-agent").CompilePreNode(new PreNode(
    RefundGate,
    "Refunds must stay comfortably inside the policy limit.",
    candidate =>
    {
        var amount = VslToolArguments.GetDouble((IReadOnlyDictionary<string, object?>)candidate!, "amount");
        // A ratio where 1.0 is break-even; 0.0 for a missing amount, so the gate fails closed.
        var estimate = new GammaEstimate(amount > 0 ? RefundLimit / amount : 0.0);
        ledger.WriteGateDecision("refund-agent", RefundGate, estimate: estimate, gammaThreshold: Thresholds.RobustGammaDefault);
        return Task.FromResult(estimate);
    },
    // No model at the guarded call: the gate only allows or blocks.
    new AssuranceBasis(f1PreCommitment: true, F2Modification.None)));

IChatClient model = /* any IChatClient: Azure OpenAI, Foundry, OpenAI, ... */;
var issueRefund = AIFunctionFactory.Create((double amount) => $"Refunded {amount}", "issue_refund");

AIAgent agent = new ChatClientAgent(model, instructions: "Use issue_refund for refund requests.", tools: [issueRefund])
    .AsBuilder()
    .UseVslGate(gate, functionName: "issue_refund")
    .Build();

try
{
    var response = await agent.RunAsync("Refund 900 to order 1234");
}
catch (VslGateDeniedException denied)
{
    Console.WriteLine($"Refund denied before it ran: {denied.Denial.Reason}");
}
```

The gate's candidate input defaults to `VslToolArguments.Of(context)`: the call's arguments as plain values. A model's JSON arguments are converted from `JsonElement`. `VslToolArguments.GetDouble` reads a number and returns 0.0 for a missing or unreadable value. To evaluate something else, pass `candidateInput: context => ...`.

## How a denial reaches your code

The Agent Framework turns an exception thrown by function middleware into a tool-error result and keeps going. The tool does not run, but the model is told "Error: Function failed.", may retry or carry on, other tools in the same batch still run, and `RunAsync` returns normally, so your code never learns about the denial. For a governance gate that is fail-open at the level of the run. Checked against Microsoft.Agents.AI 1.28.0 with a real `ChatClientAgent`.

So by default (`VslDenialMode.Abort`) a denial does three things:

1. The gate's middleware records the denial and sets `FunctionInvocationContext.Terminate`. The rest of the tool batch is skipped and the model is not called again.
2. The tool call gets a short "denied by a governance gate" result, never the tool's output.
3. The run wrapper that `UseVslGate` adds throws `VslGateDeniedException`. `denied.Denial` is the gate's `AutomationDeniedException` or `InvariantViolationException` (also the `InnerException`), and `denied.FunctionName` is the tool that did not run. For `RunStreamingAsync`, the exception is thrown when the stream ends.

`VslDenialMode.ReturnToModel` keeps the framework's default: the model gets a tool error and the run continues. The tool still never runs. Use it only when you want the model to explain the refusal, knowing your code is not told.

A gate that fails for another reason (its monitor throws) aborts the run with `VslGateFailedException` in `Abort` mode, so the tool never runs unguarded.

## Chaining gates: order differs from Python

`UseVslGates([capGate, budgetGate], functionName: "approve_purchase")` runs the gates in the order listed and stops at the first denial, the same order vsl-maf's `middleware=[...]` list gives in Python.

Separate `UseVslGate` calls follow the Agent Framework for .NET's own composition: function middleware registered later runs first (pinned by a test against 1.28.0). Every denial still blocks the tool, but order decides which gate reports the denial and which monitors run and record a decision. Use `UseVslGates` when order matters.

Register gates after any middleware that changes tool arguments, so the gate sees what the tool will get.

## Writing gate values honestly

`SuperSemantics.Vsl` never measures Gamma: your monitor computes `GammaHat` and the gate only compares it, passing when it is strictly above `max(threshold, 1 + delta)` (rules version `2026.10`).

- Build it as a ratio where 1.0 is break-even: a limit divided by the requested amount, a judge's score divided by its break-even score.
- Return 0.0 for missing or unreadable input, so the gate fails closed.
- Never pass a raw 0–1 confidence, or a confidence the model supplies in the tool's own arguments.
- Keep thresholds above 1.0 (`Thresholds.RobustGammaHighStakes`, 1.2, for payments, medical and legal actions). Use an `Invariant` for a rule that must never be broken.

## Recording decisions: the ledger doesn't write itself

Nothing in this package writes to a `VerbaLedger`; the conformance contract forbids an adapter from doing so. Record each gate's decision from inside its monitor or rule with `WriteGateDecision`, as in the usage example:

- For a PreNode, pass the estimate and the PreNode's threshold.
- For an Invariant, pass `allowed: holds`.
- Pass a `decisionId` to group a decision's records.

A denied gate's record flags drift, so write the decision's next entry from your `catch (VslGateDeniedException)` block. `examples/PurchaseApproval` does this.

The JSONL ledger is byte-compatible with Python vsl-core, so a ledger written by a .NET agent verifies with the Python tooling, and the other way round. The example's ledger was checked this way.

## Multi-agent workflows: handovers and Cluster Gamma

When one agent hands part of a decision to another (an agent exposed as a tool, or a workflow hand-off), record the hand-off and have the receiver name it in its first gate decision:

```csharp
var handover = ledger.WriteHandover("intake-agent", "refund-agent", decisionId, "customer asked for a refund");
// ...inside the refund agent's monitor:
ledger.WriteGateDecision("refund-agent", RefundGate, estimate: estimate, gammaThreshold: Thresholds.RobustGammaDefault,
    decisionId: decisionId, causedBy: handover.EntryId);
```

Gating every tool call does not make the composed system safe: in the Flash Crash, every participant's Gamma was above 1 and the Cluster's was 0. If several agents coordinate on one outcome, declare them as a `Cluster` and pass its `ClusterKey` on their records. The Cluster's `Invariants` compile and wire like any other.

## The F1/F2 assurance distinction

| Gate | F1 | F2 | Level |
|---|---|---|---|
| Amount cap or budget check before a tool (no model at the call) | true | `None` | Low |
| Gate before a hosted-model call whose system prompt the application writes | true | `Partial` | Medium |
| Gate before a self-hosted model where the application steers logits or activations | true | `Full` | High |

The level measures influence on the model, not enforcement: a Low gate still blocks every tool call it denies.

The basis is self-declared. `MafAdapter` carries one heuristic, the .NET counterpart of vsl-maf's source scan. It reads the compiled code of a monitor or rule (following async state machines, and one level of calls into the same assembly) and warns when a `Full` claim calls a hosted model client: `IChatClient`, `AIAgent`/`ChatClientAgent`, OpenAI, Azure OpenAI, Azure AI Inference/Projects/Agents, Anthropic, Google GenAI, Mistral or Cohere SDKs. Warnings go to `Trace` unless you pass `onWarning`. The check can miss indirect calls, and passing it doesn't confirm a `Full` claim.

A non-default `Fallback` also warns: the middleware raises once, immediately, with no retry loop.

## Build, test, run the example

To build this repository from source, clone it with submodules: [VSL-Core.NET](https://github.com/supersemantics/VSL-Core.NET) (`SuperSemantics.Vsl`) is a git submodule in `external/VSL-Core.NET`, pinned to `v0.1.1`. Using the package doesn't need any of this: NuGet installs `SuperSemantics.Vsl` with it.

```bash
git clone --recurse-submodules https://github.com/supersemantics/VSL-Microsoft-agent-framework.NET.git
cd VSL-Microsoft-agent-framework.NET
dotnet test
dotnet run --project examples/PurchaseApproval
```

In an existing clone, run `git submodule update --init` first.

The example runs offline with a scripted model. Pass a real `IChatClient` to run it against a model service.

## Layout

```
src/SuperSemantics.Vsl.AgentFramework/
├── MafAdapter.cs                  IVslAdapter: compiles PreNodes and Invariants
├── VslAgentBuilderExtensions.cs   UseVslGate, UseVslGates, VslDenialMode, the exceptions, VslToolArguments
└── HostedModelDetector.cs         the F2 heuristic
examples/PurchaseApproval/         hard cap + budget gate, recorded decisions, denials handled
external/VSL-Core.NET/             submodule: SuperSemantics.Vsl, pinned to v0.1.1
tests/                             conformance, a real ChatClientAgent with a scripted chat client
```

## License

MIT, see [LICENSE](https://github.com/supersemantics/VSL-Microsoft-agent-framework.NET/blob/main/LICENSE). Copyright Super Semantics.
