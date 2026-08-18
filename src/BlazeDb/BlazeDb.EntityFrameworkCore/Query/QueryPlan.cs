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

    public abstract void UseEquality(BlazeDbIndex index, object key);

    public abstract void UseRange(BlazeDbIndex index, bool hasFrom, object? from, bool hasTo, object? to, bool descending);

    /// <summary>Adds a residual filter from an <c>Expression&lt;Func&lt;TRow, bool&gt;&gt;</c>.</summary>
    public abstract void AddPredicate(LambdaExpression predicate);

    /// <summary>Adds an <c>OrderBy</c>/<c>ThenBy</c> step, taken straight from the LINQ call.</summary>
    public abstract void AddOrdering(MethodCallExpression call);
}

internal sealed class QueryPlan<TRow> : QueryPlan
{
    private BlazeDbIndex? _index;
    private object? _equalityKey;
    private bool _hasFrom;
    private object? _from;
    private bool _hasTo;
    private object? _to;
    private bool _descending;
    private List<Func<TRow, bool>>? _predicates;
    private Func<IEnumerable<TRow>, IOrderedEnumerable<TRow>>? _order;

    public override void UseEquality(BlazeDbIndex index, object key)
    {
        _index = index;
        _equalityKey = key;
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

    public override void AddPredicate(LambdaExpression predicate) =>
        (_predicates ??= []).Add((Func<TRow, bool>)predicate.Compile());

    public override void AddOrdering(MethodCallExpression call)
    {
        // Reuse the LINQ-to-Objects operator that matches the Queryable one, with the same generic
        // arguments, rather than reconstructing the comparison ourselves — that keeps null and
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
        if (_index is not null)
        {
            var definition = (IndexDefinition<TRow>)_index.Definition;
            query = _equalityKey is not null
                ? query.UseIndexBoxed(definition, _equalityKey)
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
