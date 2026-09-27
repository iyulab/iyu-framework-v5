namespace Iyu.Core.Attributes;

/// <summary>
/// Marks a property as a target of free-text search (OData <c>$search</c>). Emitted by mdd-booster on
/// M3L <c>@searchable</c> declarations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declaring any property narrows search to the declared ones.</b> A type with no
/// <c>[Searchable]</c> property is searched across all its readable string properties, as before this
/// attribute existed; a type with at least one is searched across its declared string properties
/// only. Notes, share tokens and identification numbers stop matching a search meant for names.
/// </para>
/// <para>
/// Search compares text, so a declaration on a non-string property is not searched. A type whose
/// declarations are all on non-string properties therefore matches no <c>$search</c> term — the
/// declaration says which fields are meant, and widening back to every string would ignore it.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SearchableAttribute : Attribute;
