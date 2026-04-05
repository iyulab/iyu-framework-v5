namespace Iyu.Core.Attributes;

/// <summary>
/// Marker for a read-model property sourced from a JOINed foreign entity.
/// Emitted by mdd-booster on M3L <c>@lookup(target.field)</c> declarations.
/// The property is populated by the corresponding SQL <c>_full</c>/<c>_ext</c> view
/// — EF Core never writes to it.
/// </summary>
/// <param name="path">
/// Dotted path in the source M3L (e.g. <c>customer.name</c>). Retained for
/// tooling/diagnostics; the runtime itself uses the SQL view.
/// </param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class LookupAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}
