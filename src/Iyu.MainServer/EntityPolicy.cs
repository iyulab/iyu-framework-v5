namespace Iyu.MainServer;

/// <summary>
/// The authorization policies an entity declares once (<see cref="IyuMainServerOptions.Authorize{TRead}"/>) and every
/// surface that serves it enforces.
/// </summary>
/// <param name="Read">Policy to read the entity — OData <c>GET</c>, the GraphQL query field, and every <c>$expand</c> that reaches it.</param>
/// <param name="Write">Policy to create or change it — OData <c>POST</c>/<c>PATCH</c>, and <c>DELETE</c> unless <paramref name="Delete"/> is set.</param>
/// <param name="Delete">Policy to delete it, when deleting is a different permission from editing.</param>
public sealed record EntityPolicy(string? Read, string? Write, string? Delete);
