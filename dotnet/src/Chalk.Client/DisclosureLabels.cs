using Apache.Arrow;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Client;

/// <summary>
/// The <c>chalk.disclosure</c> field metadata a typed consumer reads (D202, D218 amended): the key,
/// the words, and the rule for when a schema carries them. One place, because a prepared statement's
/// schema and a refusal's must say the same words the same way (D329).
/// </summary>
/// <remarks>
/// The words are upper-cased so they are stable across languages: the descriptor's own vocabulary
/// spells a disclosure <c>MASKED</c>, and so does the wire enum once its prefix is dropped, which is
/// how the planner writes a refusal's labels.
/// </remarks>
internal static class DisclosureLabels
{
    internal const string MetadataKey = "chalk.disclosure";

    internal const string Full = "FULL";
    internal const string Masked = "MASKED";
    internal const string Redacted = "REDACTED";
    internal const string PerRow = "PER_ROW";
    internal const string Aggregate = "AGGREGATE";
    internal const string Tested = "TESTED";

    /// <summary>
    /// The schema with <c>chalk.disclosure</c> on every field, when any field discloses anything but
    /// the value itself. The same instance back when none does, so a schema with nothing to say is not
    /// copied.
    /// </summary>
    internal static ArrowSchema Decorate(ArrowSchema schema, IReadOnlyList<string> words)
    {
        var interesting = false;
        for (var i = 0; i < words.Count; i++)
        {
            if (!string.Equals(words[i], Full, StringComparison.Ordinal))
            {
                interesting = true;
                break;
            }
        }

        if (!interesting)
        {
            return schema;
        }

        var fields = new List<Field>(schema.FieldsList.Count);
        for (var i = 0; i < schema.FieldsList.Count; i++)
        {
            var field = schema.FieldsList[i];
            var metadata = new Dictionary<string, string>(
                field.Metadata ?? new Dictionary<string, string>(0),
                StringComparer.Ordinal)
            {
                [MetadataKey] = i < words.Count ? words[i] : Full,
            };
            fields.Add(new Field(field.Name, field.DataType, field.IsNullable, metadata));
        }

        return new ArrowSchema(fields, schema.Metadata);
    }
}
