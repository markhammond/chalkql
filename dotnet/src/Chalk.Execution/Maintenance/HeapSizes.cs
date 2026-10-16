namespace Chalk.Execution.Maintenance;

/// <summary>
/// What the objects a maintained state keeps weigh on the heap of a 64-bit runtime (F223): an
/// object's header, an array's header and length, a list's own object beside its array, a
/// dictionary's two arrays.
/// </summary>
/// <remarks>
/// A state's <c>Bytes</c> is an estimate made from these and from counts the state keeps as rows come
/// and go: how many keys, lists and values it holds, and how long its arrays are. Reading it costs
/// the same however much the state holds, where weighing each object would cost a walk of them all.
/// The sizes are those .NET gives on a 64-bit machine, measured; a test holds every state's estimate
/// close to what weighing its objects finds.
/// </remarks>
internal static class HeapSizes
{
    /// <summary>A reference to an object.</summary>
    public const int Reference = 8;

    /// <summary>A <see cref="List{T}"/> or a <see cref="Stack{T}"/> itself, apart from the array it keeps.</summary>
    public const int List = 32;

    /// <summary>A <see cref="Dictionary{TKey, TValue}"/> itself, apart from its arrays.</summary>
    public const int Dictionary = 80;

    /// <summary>A dictionary entry whose key and value are both references.</summary>
    public const int ReferenceEntry = 24;

    /// <summary>A <see cref="SortedSet{T}"/> itself, apart from its nodes.</summary>
    public const int SortedSet = 48;

    /// <summary>A <see cref="Chalk.Sources.ColumnView"/>, as an array of them holds each.</summary>
    public static readonly int ColumnView = System.Runtime.CompilerServices.Unsafe.SizeOf<Chalk.Sources.ColumnView>();

    /// <summary>
    /// An array of <paramref name="length"/> elements of <paramref name="each"/> bytes, its header
    /// included; nothing when empty, as an empty array is one the runtime shares.
    /// </summary>
    public static long Array(long length, int each) => length == 0 ? 0 : Align(24 + (length * each));

    /// <summary>A list or a stack of <paramref name="capacity"/> elements of <paramref name="each"/> bytes, and the array behind it.</summary>
    public static long ListOf(int capacity, int each) => List + Array(capacity, each);

    /// <summary>A dictionary with room for <paramref name="capacity"/> entries of <paramref name="entry"/> bytes, and its two arrays.</summary>
    public static long DictionaryOf(int capacity, int entry) =>
        Dictionary + Array(capacity, sizeof(int)) + Array(capacity, entry);

    /// <summary>A list and the array behind it, at its capacity.</summary>
    public static long Of<T>(List<T> list, int each) => ListOf(list.Capacity, each);

    /// <summary>A stack and its array: <c>EnsureCapacity(0)</c> gives the capacity, and changes nothing.</summary>
    public static long Of<T>(Stack<T> stack, int each) => ListOf(stack.EnsureCapacity(0), each);

    /// <summary>A dictionary and its arrays: <c>EnsureCapacity(0)</c> gives the capacity, and changes nothing.</summary>
    public static long Of<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int entry)
        where TKey : notnull => DictionaryOf(dictionary.EnsureCapacity(0), entry);

    /// <summary>A <see cref="Chalk.Sources.ColumnarBatch"/> of <paramref name="columns"/> columns, apart from the buffers its columns view.</summary>
    public static long Batch(int columns) => 56 + Array(columns, ColumnView);

    /// <summary>A node of a <see cref="SortedSet{T}"/> holding a value of <paramref name="each"/> bytes.</summary>
    public static int SortedSetNode(int each) => (int)Align(16 + (2 * Reference) + each + 1);

    private static long Align(long bytes) => (bytes + 7) & ~7L;
}
