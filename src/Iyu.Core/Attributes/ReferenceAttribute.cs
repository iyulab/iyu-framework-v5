namespace Iyu.Core.Attributes;

/// <summary>
/// Marker for a foreign-key property. Emitted by mdd-booster on M3L
/// <c>@reference(Target)</c> declarations and mirrored by a SQL
/// <c>FOREIGN KEY REFERENCES [Target]([Id])</c> constraint.
/// </summary>
/// <param name="target">The referenced entity name (PascalCase, as in M3L).</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ReferenceAttribute(string target) : Attribute
{
    public string Target { get; } = target;
}
