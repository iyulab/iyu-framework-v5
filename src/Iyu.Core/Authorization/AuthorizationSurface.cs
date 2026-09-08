namespace Iyu.Core.Authorization;

/// <summary>Which half of an entity's surface an <see cref="AuthorizationSurfaceEntry"/> describes.</summary>
public enum AuthorizationSurfaceOperation
{
    /// <summary>Reads — OData GET, GraphQL queries.</summary>
    Read,

    /// <summary>Writes — OData POST/PATCH, GraphQL mutations.</summary>
    Write,

    /// <summary>
    /// Deletes — OData DELETE. Its own operation because a surface may require a different policy
    /// for "may remove this" than for "may change this"; a surface that does not separate the two
    /// reports the same policy on both rows rather than omitting one.
    /// </summary>
    Delete,
}

/// <summary>
/// One (surface, entity, operation) triple and the authorization policy attached to it —
/// <c>null</c> when nothing this framework knows about protects it.
/// </summary>
/// <param name="Surface">
/// The surface that exposes the entity, e.g. <c>"OData"</c> / <c>"GraphQL"</c>. A free string
/// rather than an enum on purpose: a consumer or a later package can add a surface without this
/// assembly having to learn its name first.
/// </param>
/// <param name="Entity">
/// The name the surface exposes the entity under — an OData entity set name, a GraphQL query
/// name. Deliberately the *surface's* name, not the CLR type: that is the name a reader has to
/// go look at when an entry comes back unprotected.
/// </param>
/// <param name="Operation">Read or write half.</param>
/// <param name="Policy">
/// The ASP.NET Core authorization policy required, or <c>null</c> when this framework attached
/// none. <b><c>null</c> does not prove the endpoint is open</b> — see
/// <see cref="IAuthorizationSurfaceReport"/>.
/// </param>
public sealed record AuthorizationSurfaceEntry(
    string Surface,
    string Entity,
    AuthorizationSurfaceOperation Operation,
    string? Policy);

/// <summary>
/// A surface's own account of what it exposes and what protects it. One implementation per
/// surface package; <see cref="IAuthorizationSurfaceReport"/> merges them.
/// </summary>
/// <remarks>
/// The report is built by *asking each surface* rather than by enumerating surfaces centrally so
/// that adding a third one does not require editing a central list — the case a consumer raised
/// as the reason a point-in-time audit does not hold: surfaces grow, and the check has to grow
/// with them without anyone remembering to widen it.
/// </remarks>
public interface IAuthorizationSurfaceProvider
{
    /// <summary>This surface's name, used as <see cref="AuthorizationSurfaceEntry.Surface"/>.</summary>
    string Surface { get; }

    /// <summary>Every (entity, operation) pair this surface exposes, with its attached policy.</summary>
    IReadOnlyList<AuthorizationSurfaceEntry> Describe();
}

/// <summary>
/// Every registered entity × surface × operation with the policy attached to it, so a consumer
/// can pin "nothing is unprotected" as a test instead of re-deriving the list by hand:
/// <code>
/// var report = services.GetRequiredService&lt;IAuthorizationSurfaceReport&gt;();
/// Assert.Empty(report.Unprotected);
/// </code>
/// A surface added later shows up here without the test changing.
/// </summary>
/// <remarks>
/// 🔴 <b>This reports what <i>this framework</i> attached, and nothing else.</b> A policy applied
/// some other way — an <c>[Authorize]</c> attribute on a hand-written controller, an MVC
/// convention, endpoint metadata, a reverse proxy — is invisible here, so a <c>null</c>
/// <see cref="AuthorizationSurfaceEntry.Policy"/> means <i>"not attached through this
/// framework"</i>, not <i>"reachable by anyone"</i>.
/// <para>
/// That distinction is the whole reason this type documents itself so loudly. An app that attaches
/// its policies through a controller convention will see every entry come back <c>null</c> and,
/// reading it as "everything is exposed", would either panic or — worse — decide the report is
/// noise and stop looking. To make this report meaningful, attach policies where the framework can
/// see them: <c>IyuEntityPairRegistry.RestrictPolicy</c> for OData,
/// <c>IyuGraphQLSchemaBuilder.Restrict</c> for GraphQL.
/// </para>
/// </remarks>
public interface IAuthorizationSurfaceReport
{
    /// <summary>Every entry from every registered provider.</summary>
    IReadOnlyList<AuthorizationSurfaceEntry> Entries { get; }

    /// <summary>
    /// The entries this framework attached no policy to — the subset a contract test asserts is
    /// empty. Read the <c>null</c> caveat on this interface before treating it as an exposure list.
    /// </summary>
    IReadOnlyList<AuthorizationSurfaceEntry> Unprotected { get; }
}
