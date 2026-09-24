namespace Iyu.Server.OData;

/// <summary>
/// The <c>error.code</c> values <see cref="IyuODataController{TRead, TWrite}"/> answers with — a closed
/// set, so a caller can branch on the code instead of the message text.
/// </summary>
public static class ODataErrorCodes
{
    /// <summary><c>400</c> — the body could not be read, or a value in it fails the model's rules.</summary>
    public const string InvalidBody = "InvalidBody";

    /// <summary>
    /// <c>400</c> — a query option could not be applied: an unknown property, a malformed literal, a
    /// limit exceeded, or an <c>$expand</c> this server cannot interpret.
    /// </summary>
    public const string InvalidQuery = "InvalidQuery";

    /// <summary><c>400</c> — every property a <c>PATCH</c> sent is one the write side does not accept.</summary>
    public const string UnwritableProperty = "UnwritableProperty";

    /// <summary><c>400</c> — a set that shares its key with another was posted to without a key.</summary>
    public const string SharedKeyRequired = "SharedKeyRequired";

    /// <summary><c>404</c> — the key names no row of the set (<c>GET</c>, <c>PATCH</c> and <c>DELETE</c> by key).</summary>
    public const string KeyNotFound = "KeyNotFound";

    /// <summary><c>405</c> — the set is registered read-only for this verb.</summary>
    public const string ReadOnlySet = "ReadOnlySet";

    /// <summary><c>409</c> — a shared-key row names a principal row that does not exist.</summary>
    public const string SharedKeyPrincipalMissing = "SharedKeyPrincipalMissing";

    /// <summary><c>409</c> — the principal already has its one shared-key row.</summary>
    public const string SharedKeyRowExists = "SharedKeyRowExists";
}
