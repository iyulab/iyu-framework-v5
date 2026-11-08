using System.Linq.Expressions;
using System.Reflection;

namespace Iyu.Ml.EntityFrameworkCore;

/// <summary>
/// Says which properties of an entity make up one dataset: the inputs, the answer, and which rows. The properties are named
/// with lambdas so a rename in the entity breaks the declaration at compile time instead of at the first export.
/// </summary>
/// <typeparam name="TEntity">The entity the dataset is read from.</typeparam>
public sealed class MlDatasetBuilder<TEntity> where TEntity : class
{
    private readonly List<ColumnBinding> _columns = [];
    private Expression<Func<TEntity, bool>>? _filter;

    /// <summary>Adds an input column.</summary>
    /// <param name="property">A property of the entity itself, e.g. <c>e =&gt; e.Temperature</c>.</param>
    /// <param name="column">The column's name in the exported file; defaults to the property's name.</param>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <exception cref="ArgumentException">The expression is not a plain property of the entity, or the property is already a column.</exception>
    public MlDatasetBuilder<TEntity> Feature<TValue>(Expression<Func<TEntity, TValue>> property, string? column = null) =>
        Add(property, column, MlColumnRole.Feature);

    /// <summary>Adds the column holding the true answer. A dataset has at most one.</summary>
    /// <param name="property">A property of the entity itself.</param>
    /// <param name="column">The column's name in the exported file; defaults to the property's name.</param>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <exception cref="ArgumentException">The expression is not a plain property of the entity, the property is already a column, or a label was already given.</exception>
    public MlDatasetBuilder<TEntity> Label<TValue>(Expression<Func<TEntity, TValue>> property, string? column = null)
    {
        if (_columns.Any(c => c.Role == MlColumnRole.Label))
            throw new ArgumentException("A dataset has at most one label column.", nameof(property));
        return Add(property, column, MlColumnRole.Label);
    }

    /// <summary>
    /// Restricts the rows. A filter reads the properties it names, so a property the host's read policies guard cannot
    /// appear in it any more than in a column. Called again, the predicates are combined with AND.
    /// </summary>
    /// <param name="predicate">Which rows belong to the dataset.</param>
    public MlDatasetBuilder<TEntity> Where(Expression<Func<TEntity, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _filter = _filter is null
            ? predicate
            : Expression.Lambda<Func<TEntity, bool>>(
                Expression.AndAlso(_filter.Body, new ParameterReplacer(predicate.Parameters[0], _filter.Parameters[0]).Visit(predicate.Body)),
                _filter.Parameters);
        return this;
    }

    internal EfDatasetPlan<TEntity> Build(string name)
    {
        var definition = new MlDatasetDefinition(name, _columns.Select(c => new MlColumn(c.Column, c.Role)));
        return new EfDatasetPlan<TEntity>(definition, _columns, _filter);
    }

    private MlDatasetBuilder<TEntity> Add<TValue>(Expression<Func<TEntity, TValue>> property, string? column, MlColumnRole role)
    {
        ArgumentNullException.ThrowIfNull(property);
        var info = PropertyOf(property);
        if (_columns.Any(c => c.Property.Name == info.Name))
            throw new ArgumentException($"The property '{info.Name}' is already a column of this dataset; the same value as both an input and the answer would leak the answer.", nameof(property));

        var name = string.IsNullOrWhiteSpace(column) ? info.Name : column;
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var body = Expression.Convert(new ParameterReplacer(property.Parameters[0], parameter).Visit(property.Body), typeof(object));
        _columns.Add(new ColumnBinding(name, role, info, Expression.Lambda<Func<TEntity, object?>>(body, parameter)));
        return this;
    }

    private static PropertyInfo PropertyOf<TValue>(Expression<Func<TEntity, TValue>> property)
    {
        var body = property.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;

        if (body is MemberExpression { Member: PropertyInfo info, Expression: ParameterExpression } && info.GetMethod is { IsPublic: true })
            return info;
        throw new ArgumentException("A column is a public property of the entity itself, such as e => e.Temperature; a nested property, a call or a calculation is not.", nameof(property));
    }

    internal sealed record ColumnBinding(string Column, MlColumnRole Role, PropertyInfo Property, Expression<Func<TEntity, object?>> Selector);

    internal sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
