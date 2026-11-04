namespace OneDiff;

/// <summary>
/// Maps items to small integer ids so the diff algorithms can compare <c>int</c>s instead of calling a comparer for every pair.
/// Items the comparer calls equal share an id. Ids are dense (0, 1, 2, ...) and assigned in order of first appearance, so the same
/// input always produces the same ids.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
/// <remarks>Not thread safe: use one interner per comparison, and intern both sequences through the same instance.</remarks>
public sealed class TokenInterner<T> where T : notnull
{
    private readonly Dictionary<T, int> ids;
    private readonly List<T> representatives = [];

    /// <summary>Creates an interner using <paramref name="comparer"/> (the default comparer for <typeparamref name="T"/> when null).</summary>
    public TokenInterner(IEqualityComparer<T>? comparer = null)
    {
        ids = new Dictionary<T, int>(comparer);
    }

    /// <summary>Number of distinct items seen so far; every id is below this.</summary>
    public int Count => representatives.Count;

    /// <summary>The first item that received <paramref name="id"/>. Throws <see cref="ArgumentOutOfRangeException"/> for an unknown id.</summary>
    public T this[int id] => representatives[id];

    /// <summary>Returns the id of <paramref name="item"/>, assigning the next free one if it is new.</summary>
    public int Intern(T item)
    {
        if (!ids.TryGetValue(item, out int id))
        {
            id = representatives.Count;
            ids.Add(item, id);
            representatives.Add(item);
        }

        return id;
    }

    /// <summary>Interns every item into <paramref name="destination"/>, which must be exactly as long as <paramref name="items"/>.</summary>
    public void Intern(ReadOnlySpan<T> items, Span<int> destination)
    {
        if (destination.Length != items.Length)
        {
            throw new ArgumentException("the destination must be as long as the items", nameof(destination));
        }

        for (int i = 0; i < items.Length; i++)
        {
            destination[i] = Intern(items[i]);
        }
    }

    /// <summary>Interns every item and returns the ids in a new array.</summary>
    public int[] Intern(ReadOnlySpan<T> items)
    {
        int[] result = new int[items.Length];
        Intern(items, result);
        return result;
    }
}
