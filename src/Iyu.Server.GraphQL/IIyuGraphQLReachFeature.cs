namespace Iyu.Server.GraphQL;

/// <summary>
/// The query fields a GraphQL request reads — the ones it addressed at the root, and every other served entity whose
/// data it reads by selecting into a navigation — read from <c>HttpContext.Features</c> by a host that records what a
/// request read: an access log, an audit trail, data lineage, usage metering. The GraphQL counterpart of
/// <c>IIyuODataReachFeature</c> (<c>Iyu.MainServer</c>).
/// </summary>
/// <remarks>
/// <para>
/// A host that serves both surfaces records the OData reach and this one the same way, instead of leaving GraphQL
/// reads out of its record — or parsing the GraphQL document itself and having to reproduce fragments, aliases and
/// connection wrappers.
/// </para>
/// <para>
/// <b>Reached</b> is selecting, not returning: a field named in the document counts whether or not a variable-driven
/// <c>@skip</c>/<c>@include</c> later drops it. The list may name an entity a request did not finally read, never leave
/// out one it did. Arguments are not followed — what a filter or order argument reaches is not part of this answer.
/// </para>
/// <para>
/// Names are the query names the entities are registered under (<see cref="IyuGraphQLSchemaBuilder.AddEntityPair{TRead,TWrite}"/>),
/// the same names <see cref="IyuGraphQLSchemaBuilder.QueryNames"/> and the authorization surface report use.
/// </para>
/// <para>
/// Set before the request runs, so a request the read policies refuse carries it too — the entities it tried to reach.
/// Absent when the request does not name its document (a persisted operation sent by id only), when it names an
/// operation the document does not contain, and when the host did not turn the computation on:
/// <see cref="IyuGraphQLSchemaBuilder.ComputeReach"/> is on by itself once any query field carries a policy.
/// </para>
/// </remarks>
public interface IIyuGraphQLReachFeature
{
    /// <summary>The query fields the operation addresses at its root, in ordinal order.</summary>
    IReadOnlyList<string> AddressedQueries { get; }

    /// <summary>
    /// The other served entities the operation selects into through a navigation, in ordinal order. Empty when it
    /// reads only what it addressed. Includes an addressed query only when a navigation leads back to it — being
    /// addressed is not counted twice.
    /// </summary>
    IReadOnlyList<string> ReachedQueries { get; }
}

internal sealed record IyuGraphQLReachFeature(
    IReadOnlyList<string> AddressedQueries, IReadOnlyList<string> ReachedQueries) : IIyuGraphQLReachFeature;
