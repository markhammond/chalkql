using System.Collections;
using Chalk.Ir;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Chalk.Incremental;

/// <summary>
/// What a view needs to know about its statement's plan (D339): read once when the view is made,
/// and again when it prepares its statement anew.
/// </summary>
internal static class ViewPlan
{
    /// <summary>Whether the plan reads a source outside the engine's epochs, such as a database (D340).</summary>
    public static bool ReadsOutsideEpochs(Chalk.Client.ChalkEngine engine, Plan plan)
    {
        var reads = false;
        Walk(plan, message => reads |= message switch
        {
            TableRef table => engine.SourceById(table.SourceId) is not Chalk.Sources.IRefreshableSource,
            RemoteQuery remote => engine.SourceById(remote.SourceId) is not Chalk.Sources.IRefreshableSource,
            _ => false,
        });
        return reads;
    }

    /// <summary>Whether the plan reads the current time: <c>CURRENT_TIMESTAMP</c> and its kin.</summary>
    public static bool ReadsCurrentTime(Plan plan)
    {
        var reads = false;
        Walk(plan, message =>
        {
            if (message is ScalarCall
                {
                    Function: FunctionId.CurrentTimestamp or FunctionId.CurrentDate,
                })
            {
                reads = true;
            }
        });
        return reads;
    }

    /// <summary>
    /// Every message in <paramref name="root"/>, depth first: the plan, its shared plans, every rel,
    /// every expression, every measure, every pushed plan. Read by reflection, so a node kind the IR
    /// gains is walked without a change here.
    /// </summary>
    public static void Walk(IMessage root, Action<IMessage> visit)
    {
        var pending = new Stack<IMessage>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var message = pending.Pop();
            visit(message);
            foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
            {
                if (field.FieldType != FieldType.Message)
                {
                    continue;
                }

                switch (field.Accessor.GetValue(message))
                {
                    case IMessage child:
                        pending.Push(child);
                        break;
                    case IDictionary map:
                        foreach (var value in map.Values)
                        {
                            if (value is IMessage entry)
                            {
                                pending.Push(entry);
                            }
                        }

                        break;
                    case IList list:
                        foreach (var item in list)
                        {
                            if (item is IMessage element)
                            {
                                pending.Push(element);
                            }
                        }

                        break;
                }
            }
        }
    }
}
