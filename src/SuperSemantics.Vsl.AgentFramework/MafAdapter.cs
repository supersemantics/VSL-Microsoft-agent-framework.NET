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
    /// <param name="onWarning">Receives the adapter's warnings (a non-default Fallback, which the
    /// middleware does not consume). Defaults to <see cref="Trace.TraceWarning(string)"/>.</param>
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
}
