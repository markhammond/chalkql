using Chalk.Sources;

namespace Chalk.Execution;

/// <summary>The tables a plan, or a part of one, reads.</summary>
internal static class PlanTables
{
    /// <summary>Calls <paramref name="visit"/> with every table reference under <paramref name="root"/>.</summary>
    public static void Walk(Google.Protobuf.IMessage? root, Action<Ir.TableRef> visit)
    {
        if (root is null)
        {
            return;
        }

        var pending = new Stack<Google.Protobuf.IMessage>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var message = pending.Pop();
            if (message is Ir.TableRef table)
            {
                visit(table);
            }

            foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
            {
                if (field.FieldType != Google.Protobuf.Reflection.FieldType.Message)
                {
                    continue;
                }

                switch (field.Accessor.GetValue(message))
                {
                    case Google.Protobuf.IMessage child:
                        pending.Push(child);
                        break;
                    case System.Collections.IList list:
                        foreach (var item in list)
                        {
                            if (item is Google.Protobuf.IMessage element)
                            {
                                pending.Push(element);
                            }
                        }

                        break;
                }
            }
        }
    }

    /// <summary>The tables under <paramref name="root"/>, once each, by source and name.</summary>
    public static (string SourceId, string Table)[] Of(Google.Protobuf.IMessage? root)
    {
        var found = new List<(string SourceId, string Table)>();
        Walk(root, table =>
        {
            if (!found.Exists(f => f.SourceId == table.SourceId
                && string.Equals(f.Table, table.Table, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add((table.SourceId, table.Table));
            }
        });

        return [.. found];
    }
}
