using System.Linq.Expressions;

namespace Iyu.Ml.EntityFrameworkCore;

/// <summary>
/// Names the properties a row filter reads, and refuses a filter that reads in a way this cannot see. A filter reads a property
/// when the entity is the direct target of a member access (<c>r =&gt; r.Active</c>). Passing the entity on — to a method such as
/// <c>EF.Property</c>, through a cast, as an argument — would read properties by a route that is invisible to the read guard,
/// and a row filter is a read: which rows are kept reveals the values it tests.
/// </summary>
internal sealed class FilterReads(ParameterExpression entity) : ExpressionVisitor
{
    public List<string> Names { get; } = [];

    // A filter that reaches another set of rows — a captured queryable, a nested query — reads that set's properties, which no
    // read guard was asked about. Anything queryable is refused outright, and so is any nested lambda (the way a subquery is
    // written), so a filter only ever tests properties of its own entity.
    public override Expression? Visit(Expression? node)
    {
        if (node is not null && typeof(IQueryable).IsAssignableFrom(node.Type))
            throw new ArgumentException("A filter tests properties of the entity itself and cannot read another set of rows.");
        return base.Visit(node);
    }

    protected override Expression VisitLambda<T>(Expression<T> node) =>
        throw new ArgumentException("A filter cannot contain a nested lambda (a subquery or a collection test): it reads properties of the entity directly.");

    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression == entity)
        {
            Names.Add(node.Member.Name);
            return node;
        }
        return base.VisitMember(node);
    }

    protected override Expression VisitParameter(ParameterExpression node)
    {
        if (node == entity)
            throw new ArgumentException(
                "A filter reads properties of the entity directly, such as r => r.Active. Passing the entity itself to a method, a cast or EF.Property hides which property is read, so the read guard could not hold it.");
        return node;
    }
}
