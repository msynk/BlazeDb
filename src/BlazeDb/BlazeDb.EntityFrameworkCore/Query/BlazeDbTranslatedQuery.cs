using System.Linq.Expressions;

namespace BlazeDb.EntityFrameworkCore.Query;

/// <summary>A plan, plus the LINQ the plan could not absorb.</summary>
internal sealed record BlazeDbTranslatedQuery(BlazeDbQueryPlan Plan, Expression? Remainder);
