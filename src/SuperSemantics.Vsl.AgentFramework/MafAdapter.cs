using System.Diagnostics;
using SuperSemantics.Vsl.Conformance;

namespace SuperSemantics.Vsl.AgentFramework;

/// <summary>
/// The VSL adapter for Microsoft Agent Framework. Compiles a <see cref="PreNode"/> or
/// <see cref="Invariant"/> into a <see cref="CompiledGate"/> for
/// <see cref="VslAgentBuilderExtensions.UseVslGate"/> to run before a tool call, or to call inline
/// before any guarded action. Passes <see cref="ConformanceSuite"/>.
/// </summary>
/// <remarks>
/// The compiled gate itself knows nothing about the Agent Framework: it is the same gate
/// <see cref="PlainReferenceAdapter"/> produces, plus the agent's identity on every denial. It never
/// writes to a ledger; recording decisions is the application's job.
/// </remarks>
public sealed class MafAdapter : IVslAdapter
{
    private static readonly Fallback DefaultFallback = new();
    private readonly Action<string> _warn;

    /// <summary>Creates the adapter.</summary>
    /// <param name="identityKey">Recorded on every denial; pass the agent's own identity.</param>
    /// <param name="onWarning">Receives the adapter's warnings (a non-default Fallback, an F2 claim the
    /// code cannot support). Defaults to <see cref="Trace.TraceWarning(string)"/>.</param>
    public MafAdapter(string identityKey = "vsl-maf", Action<string>? onWarning = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(identityKey);
        IdentityKey = identityKey;
        _warn = onWarning ?? (message => Trace.TraceWarning(message));
    }

    /// <summary>The identity recorded on denials.</summary>
    public string IdentityKey { get; }

    /// <inheritdoc />
    public CompiledGate CompilePreNode(PreNode preNode)
    {
        ArgumentNullException.ThrowIfNull(preNode);
        if (preNode.Fallback != DefaultFallback)
        {
            _warn(
                $"PreNode '{preNode.Name}' sets a non-default Fallback ({preNode.Fallback}), but the Agent Framework middleware "
                + "does not consume Fallback: a denial is raised once, immediately, with no retry loop. Implement retries "
                + "yourself around the compiled gate if needed.");
        }

        WarnIfFullClaimReachesHostedModel($"PreNode '{preNode.Name}'", preNode.AssuranceBasis, preNode.Monitor);
        var identityKey = IdentityKey;
        return async candidateInput =>
        {
            var estimate = await preNode.Monitor(candidateInput).ConfigureAwait(false);
            if (!estimate.Sufficient(preNode.GammaThreshold))
            {
                throw new AutomationDeniedException($"Pre-node '{preNode.Name}' insufficient Gamma", identityKey);
            }
        };
    }

    /// <inheritdoc />
    public CompiledGate CompileInvariant(Invariant invariant)
    {
        ArgumentNullException.ThrowIfNull(invariant);
        WarnIfFullClaimReachesHostedModel($"Invariant '{invariant.Name}'", invariant.AssuranceBasis, invariant.Rule);
        var identityKey = IdentityKey;
        return async candidateInput =>
        {
            if (!await invariant.Rule(candidateInput).ConfigureAwait(false))
            {
                var terminal = invariant.OnViolation?.Name;
                var reason = $"Invariant '{invariant.Name}' violated";
                if (terminal is not null)
                {
                    reason += $" -- entering terminal state '{terminal}'";
                }

                throw new InvariantViolationException(reason, identityKey, invariant.Name, terminal);
            }
        };
    }

    private void WarnIfFullClaimReachesHostedModel(string name, AssuranceBasis basis, Delegate code)
    {
        if (basis.F2Modification != F2Modification.Full)
        {
            return;
        }

        var reached = HostedModelDetector.FindHostedModelCall(code);
        if (reached is null)
        {
            return;
        }

        _warn(
            $"{name} claims F2Modification.Full, but its monitor/rule appears to call a hosted model API ({reached}). "
            + "A hosted, black-box model call cannot modify the energy function, so Full is not achievable this way; "
            + "the honest ceiling is F2Modification.Partial (Medium). This is a heuristic check, not proof: it can miss "
            + "indirect calls, and passing it does not confirm a Full claim either.");
    }
}
