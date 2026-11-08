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
