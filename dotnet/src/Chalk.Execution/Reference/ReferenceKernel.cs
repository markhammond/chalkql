using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using FunctionDescriptor = Chalk.Catalog.FunctionDescriptor;

namespace Chalk.Execution.Reference;

/// <summary>
/// A Tier 2 kernel under the reference executor (D79,
/// <c>docs/design/17-user-defined-functions.md</c> §3): the same kernel, called on one-lane views.
/// </summary>
/// <remarks>
/// A whole-batch kernel and a row-at-a-time oracle meet here. Building a batch of one costs more per
/// row than the vectorised path, which is exactly as it should be: the oracle's job is to be
/// obviously right, and running the host's own code is what makes the differential test a test of
/// Chalk's plumbing rather than of the host's arithmetic.
/// </remarks>
internal sealed class ReferenceKernel
{
    private readonly IVectorFunction _kernel;
    private readonly ChalkType[] _parameterTypes;
    private readonly ChalkType _resultType;
    private readonly VectorScratch[] _arguments;
    private readonly VectorScratch _result;
    private readonly ColumnView[] _views;

    public ReferenceKernel(IVectorFunction kernel, FunctionDescriptor descriptor)
    {
        _kernel = kernel;
        _parameterTypes = [.. descriptor.Parameters.Select(p => p.Type)];
        _resultType = descriptor.ReturnType!.Value;
        _arguments = [.. _parameterTypes.Select(t => new VectorScratch(t))];
        _result = new VectorScratch(_resultType);
        _views = new ColumnView[_parameterTypes.Length];
    }

    /// <summary>One row, in and out, in the reference executor's own boxed vocabulary.</summary>
    public object? Invoke(object?[] arguments, DateTimeOffset now)
    {
        for (var i = 0; i < arguments.Length; i++)
        {
            _views[i] = OneLane(_arguments[i], _parameterTypes[i], arguments[i]);
        }

        var context = new FunctionContext { RowCount = 1, Now = now };
        _kernel.Invoke(_views.AsSpan(0, arguments.Length), _result.Writer, in context);
        return Read();
    }

    private object? Read()
    {
        var kind = ColumnKinds.Of(_resultType);
        if (ColumnKinds.IsVariableLength(kind))
        {
            var view = _result.FinishVarLen().View;
            return view.IsValid(0)
                ? System.Text.Encoding.UTF8.GetString(view.VarValue(0))
                : (object?)null;
        }

        _ = _result.RawValues(1);
        var validity = _result.MutableValidity(1);
        if (!validity.IsEmpty && !BitUtility.GetBit(validity, 0))
        {
            return null;
        }

        var lanes = _result.RawValues(1);
        return kind switch
        {
            ColumnKind.Boolean => lanes[0] != 0,
            ColumnKind.Int8 => (long)(sbyte)lanes[0],
            ColumnKind.Int16 => (long)System.Runtime.InteropServices.MemoryMarshal.Read<short>(lanes),
            ColumnKind.Int32 => (long)System.Runtime.InteropServices.MemoryMarshal.Read<int>(lanes),
            ColumnKind.Float => System.Runtime.InteropServices.MemoryMarshal.Read<float>(lanes),
            ColumnKind.Double => System.Runtime.InteropServices.MemoryMarshal.Read<double>(lanes),
            _ => System.Runtime.InteropServices.MemoryMarshal.Read<long>(lanes),
        };
    }

    private static ColumnView OneLane(VectorScratch scratch, ChalkType type, object? value)
    {
        var kind = ColumnKinds.Of(type);
        if (ColumnKinds.IsVariableLength(kind))
        {
            scratch.BeginVarLen(1, nullable: true);
            if (value is null)
            {
                scratch.AppendNull();
            }
            else
            {
                scratch.AppendValue(
                    value is byte[] bytes
                        ? bytes
                        : System.Text.Encoding.UTF8.GetBytes((string)value));
            }

            return scratch.FinishVarLen().View;
        }

        var width = ColumnKinds.Width(kind);
        var lanes = scratch.RawValues(1);
        lanes.Clear();
        var bits = scratch.BeginValidity(1);
        if (value is null)
        {
            return scratch.Finish(1, 1).View;
        }

        switch (value)
        {
            case bool b:
                lanes[0] = (byte)(b ? 1 : 0);
                break;
            case long l when kind == ColumnKind.Int8:
                lanes[0] = (byte)(sbyte)l;
                break;
            case long l when kind == ColumnKind.Int16:
                System.Runtime.InteropServices.MemoryMarshal.Write(lanes, (short)l);
                break;
            case long l when kind == ColumnKind.Int32:
                System.Runtime.InteropServices.MemoryMarshal.Write(lanes, (int)l);
                break;
            case long l:
                System.Runtime.InteropServices.MemoryMarshal.Write(lanes, in l);
                break;
            case double d:
                System.Runtime.InteropServices.MemoryMarshal.Write(lanes, in d);
                break;
            case float f:
                System.Runtime.InteropServices.MemoryMarshal.Write(lanes, in f);
                break;
            default:
                throw new UnsupportedFeatureException(
                    $"a Tier 2 argument of CLR type {value.GetType().Name}",
                    "docs/design/17-user-defined-functions.md §3 lists what a v1 kernel takes.");
        }

        BitUtility.SetBit(bits, 0);
        _ = width;
        return scratch.Finish(1, 0).View;
    }
}
