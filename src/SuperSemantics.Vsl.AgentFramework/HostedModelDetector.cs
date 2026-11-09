using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SuperSemantics.Vsl.AgentFramework;

/// <summary>
/// A heuristic, like vsl-maf's source scan: reads the compiled code of a monitor or rule (and, one
/// level down, the methods it calls in its own assembly) and reports the first call into a hosted
/// model client. Async lambdas are followed into their state machine. Can miss deeper indirection,
/// reflection and interface calls resolved at run time.
/// </summary>
internal static class HostedModelDetector
{
    // Namespaces and types of hosted, black-box model clients.
    private static readonly string[] HostedPrefixes =
    [
        "OpenAI.",
        "Azure.AI.OpenAI.",
        "Azure.AI.Inference.",
        "Azure.AI.Projects.",
        "Azure.AI.Agents.",
        "Anthropic.",
        "Google.GenAI.",
        "Mistral.",
        "Cohere.",
        "Microsoft.Extensions.AI.IChatClient",
        "Microsoft.Extensions.AI.ChatClientExtensions",
        "Microsoft.Agents.AI.AIAgent",
        "Microsoft.Agents.AI.ChatClientAgent",
    ];

    private static readonly Dictionary<short, OpCode> OpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    public static string? FindHostedModelCall(Delegate code)
    {
        ArgumentNullException.ThrowIfNull(code);
        try
        {
            return Scan(code.Method, depth: 0, new HashSet<MethodBase>());
        }
#pragma warning disable CA1031 // A heuristic must never break gate compilation.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static string? Scan(MethodBase method, int depth, HashSet<MethodBase> seen)
    {
        if (!seen.Add(method))
        {
            return null;
        }

        var bodies = new List<MethodBase> { method };
        if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } async)
        {
            bodies.Add(async.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!);
        }

        foreach (var body in bodies)
        {
            foreach (var callee in CalledMembers(body))
            {
                var type = callee.DeclaringType;
                var full = type?.FullName ?? "";
                if (HostedPrefixes.Any(p => full.StartsWith(p, StringComparison.Ordinal) || $"{full}.".StartsWith(p, StringComparison.Ordinal)))
                {
                    return $"{full}.{callee.Name}";
                }

                if (callee is MethodBase next && depth < 1 && next.Module == method.Module && next.GetMethodBody() is not null)
                {
                    var found = Scan(next, depth + 1, seen);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<MemberInfo> CalledMembers(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        Type[]? typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var i = 0;
        while (i < il.Length)
        {
            OpCode op;
            if (il[i] == 0xFE && i + 1 < il.Length)
            {
                op = OpCodes[unchecked((short)(0xFE00 | il[i + 1]))];
                i += 2;
            }
            else
            {
                op = OpCodes[il[i]];
                i += 1;
            }

            var operandSize = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                _ => 4,
            };

            if (op.OperandType == OperandType.InlineMethod)
            {
                MemberInfo? member = null;
                try
                {
                    member = method.Module.ResolveMember(BitConverter.ToInt32(il, i), typeArgs, methodArgs);
                }
                catch (ArgumentException)
                {
                }

                if (member is not null)
                {
                    yield return member;
                }
            }

            i += operandSize;
        }
    }
}
