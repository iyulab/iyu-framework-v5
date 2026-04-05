namespace Iyu.Core.Attributes;

/// <summary>
/// Marker for a read-model property computed from aggregated child rows.
/// Emitted by mdd-booster on M3L <c>@rollup</c> declarations. The aggregate is
/// materialized in the corresponding SQL <c>_ext</c> view (or an indexed view
/// when the field is also <c>@indexed</c>).
/// </summary>
/// <param name="expression">
/// The original rollup expression from M3L (e.g. <c>sum(items.amount)</c>).
/// Retained for diagnostics and schema introspection.
/// </param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class RollupAttribute(string expression) : Attribute
{
    public string Expression { get; } = expression;

    /// <summary>
    /// True if mdd-booster chose an indexed view strategy for this rollup
    /// (derived from <c>@indexed</c> in M3L). Informational only; enforcement
    /// happens in SQL.
    /// </summary>
    public bool Indexed { get; init; }
}
