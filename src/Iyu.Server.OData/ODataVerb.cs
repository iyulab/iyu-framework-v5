namespace Iyu.Server.OData;

/// <summary>
/// A write verb an entity pair's OData endpoint can be registered as read-only
/// for. <c>Get</c> is intentionally absent — a pair with no read access is not
/// registered at all.
/// </summary>
public enum ODataVerb
{
    Post,
    Patch,
    Delete,
}
