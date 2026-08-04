using System.Linq.Expressions;
using System.Reflection;

namespace Iyu.Core.Entities;

/// <summary>
/// Resolves the <see cref="PropertyInfo"/> behind a property-access lambda, so that
/// "which property" can be stated in a compiler-checked way at the call site.
/// </summary>
/// <remarks>
/// Shared by the OData and GraphQL model builders, which both need to exclude a
/// property from the surface they expose. A string-based API would make a typo
/// silently expose the very field the caller meant to hide — the failure mode of
/// an exclusion feature must not be "it quietly did nothing".
/// </remarks>
public static class ExposedProperty
{
    /// <summary>
    /// Extracts the property targeted by <paramref name="expression"/>.
    /// Accepts the <c>Convert</c> node the compiler inserts when a value-typed
    /// property is bound to an <c>object</c>-returning lambda.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The expression is not a direct property access on <typeparamref name="T"/>.
    /// </exception>
    public static PropertyInfo Resolve<T>(Expression<Func<T, object?>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var body = expression.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : expression.Body;

        if (body is not MemberExpression { Member: PropertyInfo property })
            throw new ArgumentException(
                $"Expected a property access such as x => x.Name, but got '{expression.Body}'.",
                nameof(expression));

        // A property declared on a base type is still a property of T; reject only
        // expressions that reach outside the entity (e.g. x => x.Owner.Name).
        if (body is MemberExpression { Expression: not ParameterExpression })
            throw new ArgumentException(
                $"Expected a property of {typeof(T).Name} itself, but got a nested access '{expression.Body}'.",
                nameof(expression));

        return property;
    }
}
