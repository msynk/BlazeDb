using System.Linq.Expressions;
using System.Reflection;
using BlazeDb.EntityFrameworkCore.Metadata;
using BlazeDb.Querying;

namespace BlazeDb.EntityFrameworkCore.Query;

/// <summary>
/// A translated query in the terms the engine understands: an optional index source, residual
/// predicates, an ordering, and paging. The translator builds one without knowing the row type
/// statically, so every member here is non-generic; <see cref="QueryPlan{TRow}"/> supplies the
/// typed implementation.
/// </summary>
internal abstract class QueryPlan
{
    /// <summary>The index the plan reads from, or null for a full scan.</summary>
    public string? IndexName { get; protected set; }

    /// <summary>True once an index source is chosen; a plan has at most one.</summary>
    public bool HasIndexSource { get; protected set; }

    /// <summary>True when the index source already yields rows in the order the query asked for.</summary>
    public bool IndexProvidesOrder { get; protected set; }

    public bool HasOrdering { get; protected set; }

    public int Skip { get; set; }

    public int Take { get; set; } = -1;

    /// <summary>True when the plan reads straight from the primary-key dictionary (one key or a list of keys).</summary>
    public bool UsesPrimaryKey { get; protected set; }

    /// <summary>Sources the one row with the given primary key (already of the table's key type).</summary>
    public abstract void UseKey(object key);

    /// <summary>Sources the rows whose primary key is one of <paramref name="keys"/> (distinct, key-typed).</summary>
    public abstract void UseKeys(IReadOnlyList<object> keys);

    public abstract void UseEquality(BlazeDbIndex index, object key);

    /// <summary>Sources the union of the index lookups for <paramref name="keys"/> (distinct, key-typed).</summary>
    public abstract void UseEqualityIn(BlazeDbIndex index, IReadOnlyList<object> keys);

    public abstract void UseRange(BlazeDbIndex index, bool hasFrom, object? from, bool hasTo, object? to, bool descending);

    /// <summary>Changes the direction an already-chosen range is walked in.</summary>
    public abstract void SetRangeDirection(bool descending);

    /// <summary>Adds a residual filter from an <c>Expression&lt;Func&lt;TRow, bool&gt;&gt;</c>.</summary>
    public abstract void AddPredicate(LambdaExpression predicate);

    /// <summary>Adds an <c>OrderBy</c>/<c>ThenBy</c> step, taken straight from the LINQ call.</summary>
    public abstract void AddOrdering(MethodCallExpression call);
}

internal sealed class QueryPlan<TRow> : QueryPlan
{
    private object? _primaryKey;
    private IReadOnlyList<object>? _primaryKeys;
    private BlazeDbIndex? _index;
    private object? _equalityKey;
    private IReadOnlyList<object>? _equalityKeys;
    private bool _hasFrom;
    private object? _from;
    private bool _hasTo;
    private object? _to;
    private bool _descending;
    private List<Func<TRow, bool>>? _predicates;
    private Func<IEnumerable<TRow>, IOrderedEnumerable<TRow>>? _order;

    public override void UseKey(object key)
    {
        _primaryKey = key;
        HasIndexSource = true;
        UsesPrimaryKey = true;
        IndexName = null;
    }

    public override void UseKeys(IReadOnlyList<object> keys)
    {
        _primaryKeys = keys;
        HasIndexSource = true;
        UsesPrimaryKey = true;
        IndexName = null;
    }

    public override void UseEquality(BlazeDbIndex index, object key)
    {
        _index = index;
        _equalityKey = key;
        HasIndexSource = true;
        IndexName = index.Name;
    }

    public override void UseEqualityIn(BlazeDbIndex index, IReadOnlyList<object> keys)
    {
        _index = index;
        _equalityKeys = keys;
        HasIndexSource = true;
        IndexName = index.Name;
    }

    public override void UseRange(BlazeDbIndex index, bool hasFrom, object? from, bool hasTo, object? to, bool descending)
    {
        _index = index;
        _hasFrom = hasFrom;
        _from = from;
        _hasTo = hasTo;
        _to = to;
        _descending = descending;
        HasIndexSource = true;
        IndexProvidesOrder = true;
        IndexName = index.Name;
    }

    public override void SetRangeDirection(bool descending)
    {
        if (!IndexProvidesOrder)
        {
            throw new InvalidOperationException("The plan has no range source to redirect.");
        }
        _descending = descending;
    }

    public override void AddPredicate(LambdaExpression predicate) =>
        (_predicates ??= []).Add((Func<TRow, bool>)predicate.Compile());

    public override void AddOrdering(MethodCallExpression call)
    {
        // Reuse the LINQ-to-Objects operator that matches the Queryable one, with the same generic
        // arguments, rather than reconstructing the comparison ourselves - that keeps null and
        // comparer semantics identical to any other LINQ provider.
        var selector = ((LambdaExpression)StripQuotes(call.Arguments[1])).Compile();
        var method = EnumerableOperator(call.Method.Name, call.Method.GetGenericArguments());
        var previous = _order;

        _order = previous is null
            ? rows => (IOrderedEnumerable<TRow>)method.Invoke(null, [rows, selector])!
            : rows => (IOrderedEnumerable<TRow>)method.Invoke(null, [previous(rows), selector])!;
        HasOrdering = true;
    }

    /// <summary>Composes this plan onto an engine query over the table it was translated against.</summary>
    public Query<TKey, TRow> Build<TKey>(Query<TKey, TRow> query)
        where TKey : notnull
    {
        if (_primaryKey is not null)
        {
            query = query.UseKey((TKey)_primaryKey);
        }
        else if (_primaryKeys is not null)
        {
            query = query.UseKeys(_primaryKeys.Cast<TKey>());
        }
        else if (_index is not null)
        {
            var definition = (IndexDefinition<TRow>)_index.Definition;
            query = _equalityKey is not null ? query.UseIndexBoxed(definition, _equalityKey)
                : _equalityKeys is not null ? query.UseIndexBoxed(definition, _equalityKeys)
                : query.UseIndexBoxed(definition, _hasFrom, _from, _hasTo, _to, _descending);
        }
        if (_predicates is not null)
        {
            foreach (var predicate in _predicates)
            {
                query.Where(predicate);
            }
        }
        if (_order is not null)
        {
            query.OrderBy(_order);
        }
        if (Skip > 0)
        {
            query.Skip(Skip);
        }
        if (Take >= 0)
        {
            query.Take(Take);
        }
        return query;
    }

    private static MethodInfo EnumerableOperator(string name, Type[] genericArguments) =>
        typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == name && m.GetParameters().Length == 2 && m.IsGenericMethodDefinition)
            .MakeGenericMethod(genericArguments);

    private static Expression StripQuotes(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            expression = quote.Operand;
        }
        return expression;
    }
}
