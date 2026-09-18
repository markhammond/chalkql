namespace Chalk.Catalog;

/// <summary>
/// A catalog descriptor breaks a rule the planner would also reject (duplicate names, an index out
/// of range, a malformed type). Raised in-process before any RPC, so the host sees the problem at
/// registration rather than as a planner error. See <c>docs/design/03-planner.md</c> §3.1.
/// </summary>
public sealed class CatalogValidationException : ChalkException
{
    public CatalogValidationException(string path, string detail)
        : base($"Invalid catalog at {path}: {detail}")
    {
        Path = path;
    }

    /// <summary>Where, e.g. <c>schemas[0].tables[2].collations[0].keys[1]</c>.</summary>
    public string Path { get; }
}

/// <summary>
/// A declared collation or unique key does not hold over the data (D17). A lying collation is a
/// silent wrong-answer bug, so the check runs at <c>Build()</c> by default and fails with the first
/// offending row.
/// </summary>
public sealed class CatalogVerificationException : ChalkException
{
    public CatalogVerificationException(string table, string declaration, long rowIndex, string detail)
        : base(
            $"Table '{table}' does not satisfy its declared {declaration}: {detail} (first seen at row {rowIndex}). "
            + "Sort the collection to match, correct the declaration, or call Verify(false) if the host "
            + "guarantees the invariant another way.")
    {
        Table = table;
        Declaration = declaration;
        RowIndex = rowIndex;
    }

    public string Table { get; }

    /// <summary>The declaration that failed, e.g. <c>collation [ts ASC NULLS LAST, symbol ASC NULLS LAST]</c>.</summary>
    public string Declaration { get; }

    /// <summary>Index of the first row that breaks it.</summary>
    public long RowIndex { get; }
}
