using System.Reflection;
using System.Runtime.CompilerServices;
using Chalk.Client;

namespace Chalk.Integration.Tests;

/// <summary>
/// What objects weigh on the heap, found by walking them (F223): each object at the size the runtime
/// allocates for it, each array at its header and elements, through every field. The walk stops at
/// what a maintained state shares with the engine and with other views: types and reflection, the
/// catalog, plans and schemas, and the tables' own rows.
/// </summary>
/// <remarks>
/// A test's measure of what a state holds, to hold the estimate its <c>Bytes</c> keeps against. An
/// object's size is what allocating one more of its type costs, so no layout is assumed.
/// </remarks>
internal static class HeapWeight
{
    private static readonly Dictionary<Type, long> Sizes = [];

    /// <summary>
    /// The bytes reachable from <paramref name="roots"/>, each object counted once, not walking on past an
    /// object <paramref name="beyond"/> names, which another weighing counts.
    /// </summary>
    public static long Of(IReadOnlyCollection<object> roots, Func<object, bool>? beyond = null)
    {
        var own = new HashSet<object>(roots, ReferenceEqualityComparer.Instance);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>(roots);
        long total = 0;
        while (pending.Count > 0)
        {
            var value = pending.Pop();
            if (!seen.Add(value) || Shared(value) || (!own.Contains(value) && beyond?.Invoke(value) == true))
            {
                continue;
            }

            total += SizeOf(value);
            Children(value, pending);
        }

        return total;
    }

    private static bool Shared(object value)
    {
        if (value is Type or MemberInfo or Module or Assembly or Delegate or ChalkEngine or Chalk.Sources.ISourceRuntime)
        {
            return true;
        }

        var space = value.GetType().Namespace ?? string.Empty;
        return space.StartsWith("Chalk.Catalog", StringComparison.Ordinal)
            || space.StartsWith("Chalk.Ir", StringComparison.Ordinal)
            || space.StartsWith("Chalk.Sources.Poco", StringComparison.Ordinal)
            || space.StartsWith("Apache.Arrow", StringComparison.Ordinal)
            || space.StartsWith("Google.Protobuf", StringComparison.Ordinal)
            || space.StartsWith("System.Reflection", StringComparison.Ordinal)
            || space.StartsWith("System.Threading", StringComparison.Ordinal);
    }

    private static long SizeOf(object value)
    {
        switch (value)
        {
            case string text:
                return Align(22 + (2L * text.Length));
            case Array array:
                var element = array.GetType().GetElementType()!;
                var each = element.IsValueType ? RuntimeHelpers.SizeOf(element.TypeHandle) : IntPtr.Size;
                return Align(24 + (array.LongLength * each));
            default:
                return SizeOf(value.GetType());
        }
    }

    /// <summary>What one more object of <paramref name="type"/> costs to allocate.</summary>
    private static long SizeOf(Type type)
    {
        lock (Sizes)
        {
            if (!Sizes.TryGetValue(type, out var size))
            {
                size = long.MaxValue;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    _ = RuntimeHelpers.GetUninitializedObject(type);
                    size = Math.Min(size, GC.GetAllocatedBytesForCurrentThread() - before);
                }

                Sizes[type] = size;
            }

            return size;
        }
    }

    private static void Children(object value, Stack<object> pending)
    {
        var type = value.GetType();
        if (value is string)
        {
            return;
        }

        if (value is Array array)
        {
            var element = type.GetElementType()!;
            if (!element.IsValueType)
            {
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        pending.Push(item);
                    }
                }
            }
            else if (HoldsReferences(element))
            {
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        Fields(item, item.GetType(), pending);
                    }
                }
            }

            return;
        }

        Fields(value, type, pending);
    }

    /// <summary>The objects <paramref name="value"/>'s fields refer to, those of a field that is itself a struct included.</summary>
    private static void Fields(object value, Type type, Stack<object> pending)
    {
        for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var fieldType = field.FieldType;
                if (fieldType.IsPointer || fieldType.IsByRefLike || fieldType.IsPrimitive || fieldType.IsEnum)
                {
                    continue;
                }

                if (field.GetValue(value) is not { } child)
                {
                    continue;
                }

                if (fieldType.IsValueType)
                {
                    // A struct read is a boxed copy, of the value's own type: a nullable one's value, unwrapped.
                    if (HoldsReferences(child.GetType()))
                    {
                        Fields(child, child.GetType(), pending);
                    }

                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private static readonly Dictionary<Type, bool> References = [];

    private static bool HoldsReferences(Type type)
    {
        lock (References)
        {
            if (!References.TryGetValue(type, out var holds))
            {
                holds = (bool)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!
                    .MakeGenericMethod(type).Invoke(null, null)!;
                References[type] = holds;
            }

            return holds;
        }
    }

    private static long Align(long bytes) => Math.Max(24, (bytes + 7) & ~7L);
}
