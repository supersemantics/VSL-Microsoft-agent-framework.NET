using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SuperSemantics.Vsl.Conformance;

namespace SuperSemantics.Vsl.AgentFramework;

/// <summary>What happens to the run when a VSL gate denies a tool call.</summary>
public enum VslDenialMode
{
    /// <summary>
    /// The default. The tool does not run, the function-invocation loop stops (no other tool in the
    /// batch runs and the model is not called again), and the run throws
    /// <see cref="VslGateDeniedException"/> to the caller.
    /// </summary>
    Abort,

    /// <summary>
    /// The tool does not run; the Agent Framework hands the model a tool error and the run continues.
    /// Your code is not told about the denial.
    /// </summary>
    ReturnToModel,
}

/// <summary>A VSL gate denied a tool call and the run was aborted, fail-closed.</summary>
public sealed class VslGateDeniedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public VslGateDeniedException(AutomationDeniedException denial, string? functionName)
        : base($"VSL gate denied tool call{(functionName is null ? "" : $" '{functionName}'")}: {denial?.Message}", denial)
    {
        ArgumentNullException.ThrowIfNull(denial);
        Denial = denial;
        FunctionName = functionName;
    }

    /// <summary>The gate's own denial: an <see cref="AutomationDeniedException"/>, or the more specific
    /// <see cref="InvariantViolationException"/>. Also the <see cref="Exception.InnerException"/>.</summary>
    public AutomationDeniedException Denial { get; }

    /// <summary>The tool that was not run.</summary>
    public string? FunctionName { get; }
}

/// <summary>A VSL gate failed for a reason other than a denial (its monitor threw), so the tool was not
/// run unguarded and the run was aborted. The cause is the <see cref="Exception.InnerException"/>.</summary>
public sealed class VslGateFailedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public VslGateFailedException(string? functionName, Exception cause)
        : base($"VSL gate for '{functionName}' failed; the tool was not run unguarded.", cause)
    {
        FunctionName = functionName;
    }

    /// <summary>The tool that was not run.</summary>
    public string? FunctionName { get; }
}

/// <summary>Registers VSL gates on an Agent Framework agent.</summary>
public static class VslAgentBuilderExtensions
{
    // The current run's outcome, shared between the run wrapper and the function middleware below it.
    private static readonly AsyncLocal<RunOutcome?> CurrentRun = new();

    /// <summary>
    /// Runs <paramref name="gate"/> before every matching tool call, strictly before the tool's side
    /// effect (F1 pre-commitment).
    /// </summary>
    /// <param name="builder">The agent builder (<c>agent.AsBuilder()</c>).</param>
    /// <param name="gate">A gate compiled by <see cref="MafAdapter"/> (or any conformant adapter).</param>
    /// <param name="functionName">Gate only this tool; other tools pass through. Null gates every tool call.</param>
    /// <param name="candidateInput">What the gate evaluates. Defaults to
    /// <see cref="VslToolArguments.Of"/>: the call's arguments as plain values.</param>
    /// <param name="onDenial">See <see cref="VslDenialMode"/>. The default aborts the run.</param>
    /// <remarks>
    /// <para>Why the default needs more than a throw: the Agent Framework turns an exception from function
    /// middleware into a tool-error result and keeps going, so the model is told "Error: Function failed."
    /// and the run returns normally. In <see cref="VslDenialMode.Abort"/> the middleware instead records
    /// the denial, sets <see cref="FunctionInvocationContext.Terminate"/> (which also stops the rest of the
    /// batch) and the run wrapper this method adds throws it to the caller.</para>
    /// <para>Order: the Agent Framework for .NET (1.28.0) runs function middleware registered later
    /// first, so two separate <c>UseVslGate</c> calls run in reverse order. Every denial still blocks
    /// the tool, but order decides which gate reports the denial and which monitors run. To chain gates
    /// in a fixed order, use <see cref="UseVslGates"/>.</para>
    /// <para>Register gates after any middleware that changes tool arguments, so the gate sees the
    /// arguments the tool will get.</para>
    /// </remarks>
    public static AIAgentBuilder UseVslGate(
        this AIAgentBuilder builder,
        CompiledGate gate,
        string? functionName = null,
        Func<FunctionInvocationContext, object?>? candidateInput = null,
        VslDenialMode onDenial = VslDenialMode.Abort)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return builder.UseVslGates([gate], functionName, candidateInput, onDenial);
    }

    /// <summary>
    /// Runs <paramref name="gates"/> in the order listed before every matching tool call, stopping at
    /// the first denial: the same chaining order vsl-maf's middleware list gives in Python. Otherwise
    /// identical to <see cref="UseVslGate"/>.
    /// </summary>
    public static AIAgentBuilder UseVslGates(
        this AIAgentBuilder builder,
        IReadOnlyList<CompiledGate> gates,
        string? functionName = null,
        Func<FunctionInvocationContext, object?>? candidateInput = null,
        VslDenialMode onDenial = VslDenialMode.Abort)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(gates);
        if (gates.Count == 0 || gates.Any(g => g is null))
        {
            throw new ArgumentException("Give at least one gate, and no null gates.", nameof(gates));
        }

        if (!Enum.IsDefined(onDenial))
        {
            throw new ArgumentOutOfRangeException(nameof(onDenial));
        }

        var chain = gates.ToArray();
        var extract = candidateInput ?? (ctx => VslToolArguments.Of(ctx));
        return builder
            .Use(async (agent, context, next, cancellationToken) =>
            {
                var toolName = context.Function?.Name;
                if (functionName is not null && toolName != functionName)
                {
                    return await next(context, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    var candidate = extract(context);
                    foreach (var gate in chain)
                    {
                        await gate(candidate).ConfigureAwait(false);
                    }
                }
                catch (AutomationDeniedException denial) when (onDenial == VslDenialMode.Abort)
                {
                    return Abort(context, new VslGateDeniedException(denial, toolName));
                }
                catch (Exception error) when (onDenial == VslDenialMode.Abort && error is not OperationCanceledException)
                {
                    return Abort(context, new VslGateFailedException(toolName, error));
                }

                // Only reached if the gate returned normally (F1 pre-commitment).
                return await next(context, cancellationToken).ConfigureAwait(false);
            })
            .Use(RunAsync, RunStreamingAsync);
    }

    private static string Abort(FunctionInvocationContext context, Exception outcome)
    {
        context.Terminate = true;
        var run = CurrentRun.Value;
        if (run is null)
        {
            // Not inside a run this package wrapped: there is no way to surface the denial other than
            // throwing, which the Agent Framework turns into a tool error. The tool still does not run.
            throw outcome;
        }

        run.Failure ??= outcome;
        return "This tool call was denied by a governance gate and was not run.";
    }

    private static async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent inner, CancellationToken cancellationToken)
    {
        var outer = CurrentRun.Value;
        var run = new RunOutcome();
        CurrentRun.Value = run;
        try
        {
            var response = await inner.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
            if (run.Failure is not null)
            {
                throw run.Failure;
            }

            return response;
        }
        finally
        {
            CurrentRun.Value = outer;
        }
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent inner,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // An AsyncLocal set inside an async iterator does not survive from one MoveNextAsync to the
        // next, so set it again before every step of the inner stream: the function middleware runs
        // inside those steps.
        var run = new RunOutcome();
        var updates = inner.RunStreamingAsync(messages, session, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                CurrentRun.Value = run;
                if (!await updates.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                yield return updates.Current;
            }
        }
        finally
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }

        if (run.Failure is not null)
        {
            throw run.Failure;
        }
    }

    private sealed class RunOutcome
    {
        public Exception? Failure { get; set; }
    }
}

/// <summary>Tool-call arguments as plain values.</summary>
public static class VslToolArguments
{
    /// <summary>
    /// The call's arguments as a dictionary of plain values. A model's JSON arguments arrive as
    /// <see cref="JsonElement"/>; they are converted to strings, booleans, <see cref="long"/>,
    /// <see cref="double"/>, lists and dictionaries, so a monitor can read them directly.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Of(FunctionInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (context.Arguments is null)
        {
            return result;
        }

        foreach (var (key, value) in context.Arguments)
        {
            result[key] = value is JsonElement element ? PythonJson.FromElement(element) : value;
        }

        return result;
    }

    /// <summary>One argument as a double: a number, or a numeric string; 0.0 when missing or unreadable,
    /// so a ratio monitor built on it fails closed.</summary>
    public static double GetDouble(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.TryGetValue(name, out var value) ? value switch
        {
            double d when double.IsFinite(d) => d,
            float f when float.IsFinite(f) => f,
            long l => l,
            int i => i,
            decimal m => (double)m,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) => parsed,
            _ => 0.0,
        } : 0.0;
    }
}
