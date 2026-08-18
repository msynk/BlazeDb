namespace BlazeDb;

/// <summary>
/// An optional inclusive range bound. Implicitly convertible from a value, so call sites read
/// naturally: <c>table.Range(index, from: date1, to: Bound&lt;DateTime&gt;.Unbounded)</c>.
/// </summary>
public readonly struct Bound<T>
{
    private Bound(T value)
    {
        Value = value;
        HasValue = true;
    }

    public bool HasValue { get; }

    public T Value { get; }

    public static Bound<T> Unbounded => default;

    public static Bound<T> At(T value) => new(value);

    public static implicit operator Bound<T>(T value) => new(value);
}
