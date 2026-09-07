using Iyu.Core.Authorization;

namespace Iyu.Server.GraphQL;

/// <summary>
/// Reports the GraphQL surface from <see cref="IyuGraphQLSchemaBuilder"/> — one read entry per
/// registered query field.
/// </summary>
/// <remarks>
/// <b>Read entries only, because this builder generates no mutations yet.</b> Every pair records a
/// <c>mutationPrefix</c>, but that value is stored for future generation and nothing consumes it,
/// so the schema exposes queries alone. Emitting a write entry would put a row in the report for a
/// surface that does not exist — and since no policy can ever attach to it, that row would sit
/// permanently in <see cref="IAuthorizationSurfaceReport.Unprotected"/>. A list whose job is to be
/// empty, and which cannot be emptied, is one people stop reading.
/// <para>
/// When mutation generation lands, add the write entry here carrying the same policy — GraphQL
/// attaches one policy per field, covering both halves.
/// </para>
/// </remarks>
public sealed class GraphQLAuthorizationSurfaceProvider(IyuGraphQLSchemaBuilder builder)
    : IAuthorizationSurfaceProvider
{
    private readonly IyuGraphQLSchemaBuilder _builder =
        builder ?? throw new ArgumentNullException(nameof(builder));

    /// <inheritdoc />
    public string Surface => "GraphQL";

    /// <inheritdoc />
    public IReadOnlyList<AuthorizationSurfaceEntry> Describe()
    {
        var entries = new List<AuthorizationSurfaceEntry>();
        foreach (var queryName in _builder.QueryNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            entries.Add(new AuthorizationSurfaceEntry(
                Surface, queryName, AuthorizationSurfaceOperation.Read,
                _builder.GetAuthorizePolicy(queryName)));
        }

        return entries;
    }
}
