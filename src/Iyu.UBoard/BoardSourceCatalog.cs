using System.Security.Claims;

namespace Iyu.UBoard;

/// <summary>
/// What a caller may build a board from: the entities a host serves, their properties and the links between them, as that caller
/// may read them. A host-neutral port — a host that serves its data some other way implements it, and <c>Iyu.UBoard.MainServer</c>
/// implements it over the OData model of <c>Iyu.MainServer</c> and its read policies.
/// </summary>
public interface IBoardSourceCatalog
{
    /// <summary>Describes the sources for <paramref name="caller"/>.</summary>
    /// <param name="caller">Who is asking: what they may read decides what is listed.</param>
    /// <param name="includeUnreadable">
    /// False: an entity or a property the caller may not read is not listed. True: it is listed and marked not readable — nothing
    /// more is said about it, not even which policy guards it.
    /// </param>
    /// <param name="cancellationToken">Cancels the work.</param>
    Task<BoardSourceCatalog> DescribeAsync(ClaimsPrincipal caller, bool includeUnreadable, CancellationToken cancellationToken = default);
}

/// <summary>The sources a caller may build a board from.</summary>
/// <param name="Entities">The entities.</param>
public sealed record BoardSourceCatalog(IReadOnlyList<SourceEntity> Entities);

/// <summary>An entity a board can read, by the name it is served under (an OData entity set).</summary>
/// <param name="Name">The name to query it by.</param>
/// <param name="Readable">Whether the caller may read it. False only when the catalog was asked to list what is not readable.</param>
/// <param name="Properties">Its properties.</param>
/// <param name="Links">What it links to.</param>
public sealed record SourceEntity(string Name, bool Readable, IReadOnlyList<SourceProperty> Properties, IReadOnlyList<SourceLink> Links);

/// <summary>A property of an entity.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Type">Its type as the host's model names it, for example <c>Edm.Decimal</c> or the name of an enum type.</param>
/// <param name="Nullable">Whether a row may have no value for it.</param>
/// <param name="Readable">Whether the caller may read it. False only when the catalog was asked to list what is not readable.</param>
/// <param name="Members">The values of an enum type, in the spelling a query uses; null for any other type.</param>
public sealed record SourceProperty(string Name, string Type, bool Nullable, bool Readable, IReadOnlyList<string>? Members = null);

/// <summary>A link from one entity to another.</summary>
/// <param name="Name">The link's name, as a query follows it.</param>
/// <param name="Target">The name of the entity it leads to.</param>
/// <param name="Collection">Whether it leads to many.</param>
public sealed record SourceLink(string Name, string Target, bool Collection);
