using Chalk.Execution.Operators;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// When a maintained node that reads a moving value last ran (design 68 §6, design 77 §5): the clock,
/// and how many times the view's rebindable parameters had moved. A node runs over what it holds again
/// when either differs.
/// </summary>
internal readonly record struct Moment(DateTimeOffset Now, long Bindings)
{
    /// <summary>The moment of the update <paramref name="context"/> runs.</summary>
    public static Moment Of(OperatorContext context) => new(context.Now, context.Bindings);
}
