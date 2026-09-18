using Chalk.Sources;

namespace Chalk.Execution.Vectors;

/// <summary>
/// What an expression evaluates to for one batch: a column view, or a constant broadcast over every
/// row (§6.4). Kernels accept the view/view, view/scalar and scalar/view combinations.
/// </summary>
/// <remarks>
/// Since step 20 the array half is a <see cref="ColumnView"/> rather than an Arrow array (D61): the
/// same physical layout with no object graph behind it. <see cref="IsTransient"/> marks a view that
/// belongs to an expression node's reusable scratch and is only valid until that node evaluates
/// again. Anything that puts a vector into a batch the engine hands on must copy a transient view;
/// see <c>ColumnCopier</c>.
/// </remarks>
internal readonly struct Vector
{
    private readonly ColumnView _view;
    private readonly ScalarValue? _scalar;

    private Vector(in ColumnView view, ScalarValue? scalar, int length, bool transient)
    {
        _view = view;
        _scalar = scalar;
        Length = length;
        IsTransient = transient;
        IsScalar = scalar is not null;
    }

    /// <summary>Rows in the batch this vector was evaluated for — for a scalar too.</summary>
    public int Length { get; }

    /// <summary>True when the view is a node's reusable scratch rather than memory the batch owns.</summary>
    public bool IsTransient { get; }

    public bool IsScalar { get; }

    /// <summary>The materialised values. Only valid when <see cref="IsScalar"/> is false.</summary>
    public ColumnView View => IsScalar
        ? throw new InvalidOperationException("This vector is a scalar; check IsScalar first.")
        : _view;

    /// <summary>The broadcast constant. Only valid when <see cref="IsScalar"/> is true.</summary>
    public ScalarValue Scalar => _scalar
        ?? throw new InvalidOperationException("This vector is an array; check IsScalar first.");

    /// <summary>A view owned elsewhere — a batch column, or memory the caller will hand on.</summary>
    public static Vector FromView(in ColumnView view) => new(view, null, view.Length, transient: false);

    /// <summary>A view owned elsewhere, read for a length the caller knows.</summary>
    public static Vector FromView(in ColumnView view, int length) => new(view, null, length, transient: false);

    /// <summary>A view borrowed from a node's scratch, valid until that node runs again.</summary>
    public static Vector Transient(in ColumnView view, int length) => new(view, null, length, transient: true);

    /// <summary>A constant broadcast over <paramref name="length"/> rows.</summary>
    public static Vector FromScalar(ScalarValue scalar, int length) =>
        new(default, scalar, length, transient: false);
}
