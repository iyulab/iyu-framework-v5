using System.Reflection;
using System.Text.Json;
using Iyu.Core.Identity;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// The listing surface, judged against what it exists to make possible: recovering the handle
/// that <c>rotate</c> and <c>revoke</c> need after the issuing response is gone.
/// </summary>
public class ServiceClientListingTests
{
    private static (ServiceClientService svc, FakeIdentityStore store, Guid owner) Make()
    {
        var store = new FakeIdentityStore();
        var owner = store.AddUser("owner", "소유자", perms: ["orders.read", "orders.write"]);
        return (new ServiceClientService(store, store, TimeProvider.System), store, owner);
    }

    private static IReadOnlyList<ServiceClientSummary> Listed(IResult result)
        => Assert.IsType<Ok<IReadOnlyList<ServiceClientSummary>>>(result).Value!;

    /// <summary>
    /// The acceptance criterion this endpoint was asked for: an owner who kept nothing from the
    /// issuing response can still reach rotate and revoke.
    /// </summary>
    [Fact]
    public async Task An_owner_who_lost_the_issuing_response_can_still_revoke()
    {
        var (svc, _, owner) = Make();
        await svc.CreateAsync(owner, "connector", ["orders.read"], null, default);
        // The issuing response — id and secret both — is deliberately not captured here.

        var listing = Listed(await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default));
        var recovered = Assert.Single(listing);

        var revoked = await IdentityEndpointHandlers.RevokeServiceClientAsync(recovered.Id, owner, svc, default);
        Assert.Equal(204, Assert.IsAssignableFrom<IStatusCodeHttpResult>(revoked).StatusCode);
    }

    [Fact]
    public async Task The_same_route_recovers_the_handle_that_rotate_needs()
    {
        var (svc, _, owner) = Make();
        await svc.CreateAsync(owner, "connector", ["orders.read"], null, default);

        var recovered = Assert.Single(Listed(
            await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default)));

        var rotated = await IdentityEndpointHandlers.RotateServiceClientAsync(recovered.Id, owner, svc, default);
        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(rotated).StatusCode);
    }

    /// <summary>
    /// A revoked client stays listed. Dropping it would answer "is that credential still out
    /// there?" the same way as "it never existed" — and the owner asking has usually just found
    /// out that something leaked.
    /// </summary>
    [Fact]
    public async Task Revoked_clients_remain_listed_and_are_distinguishable()
    {
        var (svc, _, owner) = Make();
        var live = await svc.CreateAsync(owner, "live", ["orders.read"], null, default);
        var dead = await svc.CreateAsync(owner, "dead", ["orders.read"], null, default);
        Assert.True(await svc.RevokeAsync(dead.Id, owner, default));

        var listing = Listed(await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default));

        Assert.Equal(2, listing.Count);
        Assert.True(listing.Single(c => c.Id == live.Id).IsActive);
        Assert.False(listing.Single(c => c.Id == dead.Id).IsActive);
    }

    /// <summary>Another owner's clients are invisible, matching revoke/rotate's 404-not-403 convention.</summary>
    [Fact]
    public async Task Only_the_callers_own_clients_are_listed()
    {
        var (svc, store, owner) = Make();
        var stranger = store.AddUser("x", "남", perms: ["orders.read"]);
        var mine = await svc.CreateAsync(owner, "mine", ["orders.read"], null, default);
        await svc.CreateAsync(stranger, "theirs", ["orders.read"], null, default);

        var listing = Listed(await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default));

        Assert.Equal(mine.Id, Assert.Single(listing).Id);
    }

    [Fact]
    public async Task An_owner_with_no_clients_gets_an_empty_listing()
    {
        var (svc, _, owner) = Make();
        Assert.Empty(Listed(await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default)));
    }

    /// <summary>
    /// No secret material on the wire — asserted against the serialised payload, because that is
    /// the artifact that actually reaches a caller.
    /// </summary>
    [Fact]
    public async Task The_serialised_listing_carries_no_secret_material()
    {
        var (svc, store, owner) = Make();
        var created = await svc.CreateAsync(owner, "connector", ["orders.read"], null, default);
        var stored = await store.FindServiceClientByClientIdAsync(created.ClientId!, default);
        var hash = stored!.SecretHash;

        var listing = Listed(await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default));
        var json = JsonSerializer.Serialize(listing);

        Assert.False(string.IsNullOrEmpty(hash));
        Assert.DoesNotContain(hash, json, StringComparison.Ordinal);
        Assert.DoesNotContain(created.PlaintextSecret!, json, StringComparison.Ordinal);

        // And no *textual* field is named like a credential. Scoping this to string-valued members
        // is what lets the payload carry a rotation timestamp: a number cannot hold a secret, so a
        // date named after one is not the leak this is watching for. Matching the name alone would
        // have forced the field to be called something less exact to get past its own guard.
        foreach (var property in JsonDocument.Parse(json).RootElement.EnumerateArray()
                     .SelectMany(element => element.EnumerateObject())
                     .Where(property => property.Value.ValueKind is JsonValueKind.String))
        {
            Assert.DoesNotContain("secret", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hash", property.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The guarantee above is a property of the type, not of this one handler. A future caller
    /// that serialises a summary somewhere else inherits it; one that was handed the stored client
    /// would not, which is why the listing does not return that.
    /// </summary>
    [Fact]
    public void The_summary_type_declares_no_secret_bearing_member()
    {
        // Only members that could hold credential material are judged by name. A member whose type
        // cannot carry text carries no secret whatever it is called, and excluding it by type keeps
        // the assertion about leakage rather than about vocabulary.
        var carriers = typeof(ServiceClientSummary)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => CanCarryText(p.PropertyType))
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain(carriers, n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(carriers, n => n.Contains("Hash", StringComparison.OrdinalIgnoreCase));
        Assert.False(typeof(IServiceClient).IsAssignableFrom(typeof(ServiceClientSummary)));

        // The exclusion is load-bearing only if something is actually excluded by it, and only if
        // what remains is still the set the assertions above are meant to police.
        Assert.Contains(nameof(ServiceClientSummary.SecretRotatedAt),
            typeof(ServiceClientSummary).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain(nameof(ServiceClientSummary.SecretRotatedAt), carriers);
        Assert.Contains(nameof(ServiceClientSummary.ClientId), carriers);
    }

    /// <summary>
    /// Whether a member could hold a credential at all: text, bytes, or a sequence of either.
    /// Anything else — a timestamp, a flag, an id — cannot, whatever it is named.
    /// </summary>
    private static bool CanCarryText(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[])) return true;
        if (type == typeof(Guid) || type == typeof(Guid?)) return false;
        if (!type.IsGenericType) return false;
        return type.GetGenericArguments().Any(CanCarryText);
    }

    /// <summary>
    /// A freshly issued credential has never been rotated, so the field is null rather than
    /// echoing the creation time. "Rotated at, same as created at" would read as a rotation.
    /// </summary>
    [Fact]
    public async Task A_credential_that_was_never_rotated_reports_no_rotation()
    {
        var (svc, _, owner) = Make();
        await svc.CreateAsync(owner, "connector", ["orders.read"], null, default);

        var listing = Listed(await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default));

        Assert.Null(Assert.Single(listing).SecretRotatedAt);
    }

    /// <summary>
    /// Rotating records when it happened, on the framework's clock. Without this an owner cannot
    /// tell a credential whose holder is presenting the previous secret from one that simply died:
    /// both stop working, and only one of them has a recent <c>LastUsedAt</c>.
    /// </summary>
    [Fact]
    public async Task Rotating_records_when_the_secret_changed()
    {
        var store = new FakeIdentityStore();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        var svc = new ServiceClientService(store, store, clock);
        var owner = store.AddUser("owner", "owner", perms: ["orders.read"]);
        var created = await svc.CreateAsync(owner, "connector", ["orders.read"], null, default);

        var rotated = await svc.RotateAsync(created.Id, owner, default);

        Assert.True(rotated.Ok);
        var listing = Listed(await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default));
        Assert.Equal(clock.GetUtcNow(), Assert.Single(listing).SecretRotatedAt);
    }

    /// <summary>
    /// Changing only the permission grant is not a rotation — the holder's secret still works, so
    /// reporting one would send an owner looking for a redelivery that never needed to happen.
    /// This is what a single "last modified" stamp could not have expressed.
    /// </summary>
    [Fact]
    public async Task Changing_permissions_is_not_reported_as_a_rotation()
    {
        var (svc, store, owner) = Make();
        var created = await svc.CreateAsync(owner, "connector", ["orders.read"], null, default);

        var updated = await svc.UpdatePermissionsAsync(created.Id, owner, [], default);

        Assert.True(updated.Ok);
        var listing = Listed(await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default));
        Assert.Null(Assert.Single(listing).SecretRotatedAt);
    }

    /// <summary>
    /// <c>LastUsedAt</c> is how a dead key is told from a live one, and it is already maintained —
    /// token issuance touches the client. If the listing did not carry it forward, an owner
    /// deciding what to retire would be reading a column that always says "never".
    /// </summary>
    [Fact]
    public async Task Last_used_is_carried_through_from_token_issuance()
    {
        var (svc, store, owner) = Make();
        var created = await svc.CreateAsync(owner, "connector", ["orders.read"], null, default);

        var before = Assert.Single(Listed(
            await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default)));
        Assert.Null(before.LastUsedAt);

        var used = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        await store.TouchServiceClientAsync(created.Id, used, default);

        var after = Assert.Single(Listed(
            await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default)));
        Assert.Equal(used, after.LastUsedAt);
    }

    /// <summary>
    /// The permission grant is shown so least-privilege can be checked by eye — the reason a
    /// listing is worth having beyond bare ids.
    /// </summary>
    [Fact]
    public async Task The_effective_permission_grant_is_shown()
    {
        var (svc, _, owner) = Make();
        await svc.CreateAsync(owner, "connector", ["orders.read"], null, default);

        var listed = Assert.Single(Listed(
            await IdentityEndpointHandlers.ListServiceClientsAsync(owner, svc, default)));

        Assert.Equal(["orders.read"], listed.Permissions);
        Assert.Equal("connector", listed.DisplayName);
        Assert.NotEqual(default, listed.CreatedAt);
    }
}
