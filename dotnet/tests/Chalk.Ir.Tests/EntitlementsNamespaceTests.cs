using static Chalk.TestKit.IrBuilder;

namespace Chalk.Ir.Tests;

/// <summary>
/// The invariant asks about a table by <b>(schema, table)</b> and never by the bare name (D270,
/// <c>docs/design/45-typed-tenancy-surface.md</c> §1 as amended 2026-09-16).
/// </summary>
/// <remarks>
/// <para>
/// One policy may now speak about two sources that both hold a table called <c>orders</c>, each with
/// its own descriptor and its own column rules. Everything the client re-establishes from its own
/// catalog has to key on the pair: an entitled column count, a reported descriptor hash, a verdict.
/// A check that keyed on the name alone would answer the first source's question about the second
/// source's read, and a mismatched answer here is a refusal the host would have to explain away —
/// or, worse, a match that should not have been one.
/// </para>
/// <para>
/// The two reads below are deliberately the same name with different shapes, so a bare-name lookup
/// cannot be right for both: the ledger's <c>orders</c> carries three entitled columns and the
/// archive's four.
/// </para>
/// </remarks>
public sealed class EntitlementsNamespaceTests
{
    private static readonly RowType Ledger =
        Row(F("id", I64()), F("org_id", I64()), F("amount", I64()));

    private static readonly RowType Archive =
        Row(F("id", I64()), F("region_id", I64()), F("amount", I64()), F("note", Str()));

    private const string LedgerHash = "11111111222222221111111122222222";
    private const string ArchiveHash = "33333333444444443333333344444444";

    private static Rel Read(string schema, RowType row, int columns, string hash)
    {
        var read = new Rel
        {
            RowType = row,
            PolicyInjected = true,
            Read = new Read
            {
                Table = new TableRef { SourceId = schema + "-mem", Schema = schema, Table = "orders" },
                DescriptorHash = hash,
            },
        };

        for (var i = 0; i < columns; i++)
        {
            read.Read.Projection.Add((uint)i);
            read.Read.Disclosures.Add(
                new ColumnDisclosure { Column = (uint)i, Outcome = DisclosureOutcome.Full });
        }

        return read;
    }

    /// <summary>The two reads in one plan, joined the way a statement naming both would join them.</summary>
    private static Plan TwoSources()
    {
        var join = new Rel
        {
            RowType = Row(
                F("id", I64()), F("org_id", I64()), F("amount", I64()),
                F("id0", I64()), F("region_id", I64()), F("amount0", I64()), F("note", Str())),
            Join = new Join
            {
                Left = Read("ledger", Ledger, 3, LedgerHash),
                Right = Read("archive", Archive, 4, ArchiveHash),
                Type = JoinType.Inner,
            },
        };

        return new Plan
        {
            IrVersion = IrVersion.Current,
            ContextId = "namespace",
            CatalogEpoch = 1,
            Root = join,
            OutputType = join.RowType,
        };
    }

    /// <summary>
    /// The catalog answers per (schema, table): three entitled columns in the ledger, four in the
    /// archive, and a different descriptor hash for each.
    /// </summary>
    private static PlanValidationOptions Qualified() => new()
    {
        VerifyDigest = false,
        EntitledTables = table => table.Schema switch
        {
            "ledger" => 3,
            "archive" => 4,
            _ => null,
        },
        ReportedDescriptorHashes = table => table.Schema switch
        {
            "ledger" => LedgerHash,
            "archive" => ArchiveHash,
            _ => null,
        },
    };

    [Fact]
    public void The_invariant_asks_about_each_read_by_its_own_schema()
    {
        // Both reads are well formed against their own table, so a validator that asked the right
        // question of each accepts the plan. This is the whole claim; the two below say what would
        // have happened had it asked the wrong one.
        PlanValidator.Validate(TwoSources(), Qualified());
    }

    /// <summary>
    /// The shape of the mistake, made deliberately: a catalog that answers by the bare name gives
    /// the ledger's column count for the archive's read, and the invariant refuses the plan.
    /// </summary>
    [Fact]
    public void A_catalog_that_answered_by_the_bare_name_would_be_refused()
    {
        var error = Assert.Throws<InvalidPlanException>(
            () => PlanValidator.Validate(
                TwoSources(),
                new PlanValidationOptions
                {
                    VerifyDigest = false,
                    EntitledTables = table =>
                        string.Equals(table.Table, "orders", StringComparison.Ordinal) ? 3 : null,
                }));

        Assert.Contains("I-IR-E", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the same for the descriptor hash the report names (D231): answering by the bare name
    /// hands the ledger's hash to the archive's read, which is a plan compiled under a policy nobody
    /// wrote.
    /// </summary>
    [Fact]
    public void A_descriptor_hash_answered_by_the_bare_name_would_be_refused()
    {
        var error = Assert.Throws<InvalidPlanException>(
            () => PlanValidator.Validate(
                TwoSources(),
                new PlanValidationOptions
                {
                    VerifyDigest = false,
                    EntitledTables = table => table.Schema switch
                    {
                        "ledger" => 3,
                        "archive" => 4,
                        _ => null,
                    },
                    ReportedDescriptorHashes = _ => LedgerHash,
                }));

        Assert.Contains("I-IR-E", error.Message, StringComparison.Ordinal);
    }
}
