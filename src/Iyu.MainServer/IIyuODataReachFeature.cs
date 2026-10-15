namespace Iyu.MainServer;

/// <summary>
/// The entity sets an OData read request reaches — the set it addressed, and every set whose data it reads through a
/// navigation or a search into a reference — read from <c>HttpContext.Features</c> by a host that records what a
/// request read: an access log, an audit trail, data lineage, usage metering.
/// </summary>
/// <remarks>
/// <para>
/// It is the answer the framework's own read authorization works from, handed on instead of discarded. A host that
/// derives the same answer from the query string has to reproduce every rule that answer depends on — both spellings
/// of an option, parameter aliases, options nested inside an expand, the paths inside <c>$apply</c> — and where its
/// parser and this one disagree, the record and the authorization describe different requests.
/// </para>
/// <para>
/// <b>Reached</b> is reading, not returning: a navigation used only to filter, sort, group or compute reads the
/// target's values as surely as one that is expanded into the response, and so does a search that matches the text
/// of a searchable reference. Each of those names its target here — the same unit the read authorization applies.
/// A search nested in an expand of a single-valued navigation is counted although the query pipeline does not apply
/// one there today: the list may name a reference such a search did not follow, never leave out one it did.
/// </para>
/// <para>
/// Set before the read policies are evaluated, so a request refused with 401 or 403 carries it too — the sets it
/// tried to reach. Absent when the framework did not determine what the request reaches: a request it refused because
/// it could not account for the query (an expression it cannot interpret, an option given twice, a navigation to data
/// no set serves — 400, nothing was read), a request outside the OData read actions, and a host on which the check
/// does not run. The check runs on every read when any entity set has a read policy; on a host with none, set
/// <see cref="IyuMainServerOptions.ComputeODataReach"/>.
/// </para>
/// </remarks>
public interface IIyuODataReachFeature
{
    /// <summary>The entity set the request addressed.</summary>
    string EntitySet { get; }

    /// <summary>
    /// Every entity set the request reads through a navigation or a search into a reference, in ordinal order. Empty
    /// when it reads only the set it addressed. Includes <see cref="EntitySet"/> only when a navigation leads back to
    /// it — being addressed is not counted twice.
    /// </summary>
    IReadOnlyList<string> ReachedEntitySets { get; }
}

internal sealed record IyuODataReachFeature(string EntitySet, IReadOnlyList<string> ReachedEntitySets) : IIyuODataReachFeature;
