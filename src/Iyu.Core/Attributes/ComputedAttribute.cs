namespace Iyu.Core.Attributes;

/// <summary>
/// Marker for a read-model property computed from an expression on the same
/// row. Emitted by mdd-booster on M3L <c>@computed</c> declarations. The
/// expression is materialized either in the SQL <c>_ext</c> view or as a
/// <c>PERSISTED COMPUTED COLUMN</c> when it has no JOIN dependency.
/// </summary>
/// <param name="expression">
/// Original computed expression from M3L (e.g. <c>supply_total * 0.1</c>).
/// Retained for diagnostics.
/// </param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ComputedAttribute(string expression) : Attribute
{
    public string Expression { get; } = expression;
}
