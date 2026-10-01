using System.Reflection;
using System.Text.RegularExpressions;
using Chalk.TestKit;
using Xunit;

namespace Chalk.Architecture.Tests;

/// <summary>
/// What a host reads when ChalkQL refuses something (design 61): a message that stands on its own,
/// and a code the glossary explains, which the planner and the client spell alike.
/// </summary>
public sealed class MessageTests
{
    /// <summary>
    /// Something only this repository's maintainers can look up: a design document or section, a
    /// decision, finding or ADR number, or a milestone label.
    /// </summary>
    private static readonly Regex Citation = new(
        string.Join(
            "|",
            @"docs/design/",
            @"§\s?\d",
            @"\b[DF]\d{2,3}\b",
            @"\bADR[ -]?\d{3,4}\b",
            @"\b\d{2}-[a-z0-9-]+\.md\b",
            @"\bM[1-9]\b",
            @"\bv1\b(?![.\w])",
            @"\bmilestone\b"),
        RegexOptions.CultureInvariant);

    private static readonly Regex StringLiteral = new(@"""(?:[^""\\\n]|\\.)*""", RegexOptions.CultureInvariant);

    /// <summary>
    /// No string literal in the .NET sources or the planner's names what a host cannot see. A
    /// comment may: it is a note for whoever maintains the code. A gRPC package name such as
    /// <c>chalk.v1</c> is a name on the wire, not a milestone.
    /// </summary>
    [Fact]
    public void No_message_cites_a_design_document_a_decision_or_a_milestone()
    {
        var root = RepoLayout.Root.FullName;
        var offences = new List<string>();
        foreach (var (directory, pattern) in new[] { ("dotnet/src", "*.cs"), ("planner/src/main", "*.java") })
        {
            var files = Directory.EnumerateFiles(Path.Combine(root, directory), pattern, SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var separator = Path.DirectorySeparatorChar;
                if (file.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                    || file.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var code = lines[i].TrimStart();
                    if (code.StartsWith("//", StringComparison.Ordinal)
                        || code.StartsWith("/*", StringComparison.Ordinal)
                        || code.StartsWith('*'))
                    {
                        continue;
                    }

                    var cited = StringLiteral.Matches(lines[i])
                        .Select(literal => literal.Value)
                        .FirstOrDefault(literal =>
                            Citation.IsMatch(literal) && !literal.Contains(".v1", StringComparison.Ordinal));
                    if (cited is not null)
                    {
                        offences.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {cited}");
                    }
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "These messages name what a host cannot see; say the rule instead:\n" + string.Join("\n", offences));
    }

    /// <summary>The glossary explains every code once, and explains nothing that is not a code.</summary>
    [Fact]
    public void The_glossary_has_one_entry_for_each_code()
    {
        var glossary = File.ReadAllText(Path.Combine(RepoLayout.Root.FullName, "docs", "errors.md"));
        var entries = Regex.Matches(glossary, @"^### (\w+)\s*$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.Equal(entries.Count, entries.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            Constants(typeof(ChalkErrorCodes)).Select(field => field.Name).Order(StringComparer.Ordinal),
            entries.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A constant's value is its own name, so a code reads the same in a host's code, at the end of
    /// a message and on the wire.
    /// </summary>
    [Fact]
    public void Every_code_and_kind_is_spelt_as_its_constant_is_named()
    {
        foreach (var field in Constants(typeof(ChalkErrorCodes)).Concat(Constants(typeof(Chalk.Client.PlanErrorKinds))))
        {
            Assert.Equal(field.Name, (string?)field.GetRawConstantValue());
        }
    }

    /// <summary>
    /// The planner and the client name the same codes and the same kinds. A name travels as text, so
    /// nothing generates the one side from the other; this is what keeps them in step.
    /// </summary>
    [Fact]
    public void The_planner_and_the_client_name_the_same_codes_and_kinds()
    {
        Assert.Equal(
            Constants(typeof(ChalkErrorCodes)).Select(field => field.Name).Order(StringComparer.Ordinal),
            PlannerNames("chalk/planner/ErrorCode.java").Order(StringComparer.Ordinal));
        Assert.Equal(
            Constants(typeof(Chalk.Client.PlanErrorKinds)).Select(field => field.Name).Order(StringComparer.Ordinal),
            PlannerNames("chalk/planner/rpc/PlanErrorKind.java").Order(StringComparer.Ordinal));
    }

    private static IEnumerable<FieldInfo> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.IsLiteral);

    /// <summary>The names one of the planner's enums sends: the text in each <c>CONSTANT("Name")</c>.</summary>
    private static IEnumerable<string> PlannerNames(string path)
    {
        var source = File.ReadAllText(
            Path.Combine(RepoLayout.Root.FullName, "planner", "src", "main", "java", path));
        return Regex.Matches(source, @"^\s+[A-Z][A-Z0-9_]*\(""(\w+)""\)[,;]", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value);
    }
}
