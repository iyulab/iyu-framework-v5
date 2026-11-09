using System.Reflection;
using Iyu.Core.Identity;

namespace Iyu.Core.Entities;

/// <summary>
/// The properties of framework-owned contracts whose value is a secret derived from something a caller must never hold,
/// and which the OData and GraphQL models therefore leave out unless the host says otherwise.
/// </summary>
/// <remarks>
/// <para>
/// A machine credential's <see cref="IServiceClient.SecretHash"/> is stored so the server can check a presented secret; no
/// caller has a use for the hash, and handing it out lets whoever reads it test guesses offline. The framework owns the
/// type, so it is the framework that decides: a read type that implements <see cref="IServiceClient"/> (an entity
/// deriving from <see cref="IyuServiceClient"/>, or one that implements the interface itself) is served without its
/// <c>SecretHash</c>, on every surface, without the host having to know to call <c>Exclude</c>.
/// </para>
/// <para>
/// Only what the framework owns is in this list. A hash of a user's password lives on the application's own user type; the
/// framework cannot know its name, so it stays the host's to exclude. A host that really does want a withheld property
/// served puts it back with <c>Expose</c> on the model builder of each surface — a decision it writes down.
/// </para>
/// </remarks>
public static class HiddenByDefaultProperties
{
    /// <summary>The properties of <paramref name="readType"/> that the models withhold by default; empty for most types.</summary>
    public static IReadOnlyList<PropertyInfo> Of(Type readType)
    {
        ArgumentNullException.ThrowIfNull(readType);
        if (!typeof(IServiceClient).IsAssignableFrom(readType)) return [];

        // A type that implements the interface explicitly has no public property of that name; then there is no
        // property for a model to find and nothing to withhold.
        return readType.GetProperty(nameof(IServiceClient.SecretHash), BindingFlags.Public | BindingFlags.Instance) is { } hash
            ? [hash]
            : [];
    }
}
