using System.Reflection;
using System.Text.Json;
using Iyu.Core.Identity;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Http;
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
        return (new ServiceClientService(store, store), store, owner);
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
        Assert.DoesNotContain("ecret", json, StringComparison.Ordinal);   // no field named *ecret* at all
    }

    /// <summary>
    /// The guarantee above is a property of the type, not of this one handler. A future caller
    /// that serialises a summary somewhere else inherits it; one that was handed the stored client
    /// would not, which is why the listing does not return that.
    /// </summary>
    [Fact]
    public void The_summary_type_declares_no_secret_bearing_member()
    {
        var members = typeof(ServiceClientSummary)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain(members, n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, n => n.Contains("Hash", StringComparison.OrdinalIgnoreCase));
        Assert.False(typeof(IServiceClient).IsAssignableFrom(typeof(ServiceClientSummary)));
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
