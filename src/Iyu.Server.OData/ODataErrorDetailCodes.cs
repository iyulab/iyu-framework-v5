namespace Iyu.Server.OData;

/// <summary>
/// The <c>error.details[].code</c> values <see cref="IyuODataController{TRead, TWrite}"/> answers with — a closed
/// set, so a caller can tell one entry's cause from another's without reading the message.
/// </summary>
/// <remarks>
/// Where a refusal has one cause, every entry carries that cause, and its code is the refusal's own
/// (<see cref="UnknownProperty"/>, <see cref="UnwritableProperty"/>, <see cref="SharedKeyRequired"/>). One refusal
/// carries entries of different causes: <see cref="ODataErrorCodes.InvalidBody"/>, whose entries are either
/// <see cref="Unconvertible"/> or <see cref="ValidationFailed"/>. Only the second carries a message the
/// application wrote — a client that localizes refusals replaces the first and keeps the second.
/// </remarks>
public static class ODataErrorDetailCodes
{
    /// <summary>The value could not be read as the property's declared type. The message is the framework's.</summary>
    public const string Unconvertible = "Unconvertible";

    /// <summary>The value was read but fails a rule the model declares. The message is the model's own.</summary>
    public const string ValidationFailed = "ValidationFailed";

    /// <summary>The property is not declared by the entity set's type — the entry's <c>target</c> is its name.</summary>
    public const string UnknownProperty = ODataErrorCodes.UnknownProperty;

    /// <summary>The property cannot be written by this request — the message says why.</summary>
    public const string UnwritableProperty = ODataErrorCodes.UnwritableProperty;

    /// <summary>The key is required because the set shares its key with another.</summary>
    public const string SharedKeyRequired = ODataErrorCodes.SharedKeyRequired;
}
