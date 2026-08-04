namespace Iyu.MainServer.Identity;

/// <summary>
/// What a service client looks like to its owner: everything needed to recognise, audit and
/// retire a credential, and nothing that could be used to authenticate as one.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a separate type on purpose, not a projection of <c>IServiceClient</c>.</b> That
/// interface carries <c>SecretHash</c>, so returning it from a listing endpoint would put a
/// credential hash on the wire the first time anyone serialised the result. Keeping the wire
/// shape in its own record makes "no secret material leaves here" a property of the type rather
/// than a rule each caller has to remember.
/// </para>
/// <para>
/// <b><see cref="CreatedAt"/> is not nullable, and the store supplies it.</b> The interface a
/// client entity implements does not declare a creation timestamp — where it comes from is the
/// store's business, and every store has an answer. Making the field nullable to accommodate a
/// store that has not looked would push a null into every consumer that always has a value, for
/// the sake of one that simply has not been asked.
/// </para>
/// </remarks>
/// <param name="Id">The handle <c>rotate</c> and <c>revoke</c> require.</param>
/// <param name="ClientId">The public identifier, for matching a listing entry against a key in use.</param>
/// <param name="DisplayName">Whatever the issuer called it.</param>
/// <param name="Permissions">The effective grant, so least-privilege can be checked by eye.</param>
/// <param name="CreatedAt">When the credential was issued.</param>
/// <param name="ExpiresAt">When it stops working, or <c>null</c> if it does not expire on its own.</param>
/// <param name="LastUsedAt">When it last obtained a token, or <c>null</c> if never — this is how a dead key is spotted.</param>
/// <param name="IsActive">False once revoked. Revoked clients are listed, not hidden — see the store contract.</param>
public sealed record ServiceClientSummary(
    Guid Id,
    string ClientId,
    string DisplayName,
    IReadOnlyList<string> Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    bool IsActive);
