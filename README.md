# iyu-framework-v5

Runtime library for the Iyu stack. Consumed by apps generated from M3L models
via [mdd-booster](https://github.com/iyulab/mdd-booster). Provides a single
`AddIyuMainServer` entry point that wires EF Core, OData, and GraphQL on top of
generator-produced entities, plus optional modules for identity, attachments,
chat, scheduled reports, office-document template rendering, and document-to-PDF
conversion.

Targets .NET 10. All twelve projects share one version and ship as separate
NuGet packages.

## Role in the stack

A consumer app builds directly on this framework's interfaces, abstractions, and base
classes — `IyuEntity`, `IyuDbContext`, the OData/GraphQL builders, the identity/authorization
primitives — while mdd-booster generates the model-shaped code that sits on top of them from
an M3L model. The split is deliberate: this framework owns the parts that stay the same no
matter what the model looks like (extension points, cross-cutting mechanisms, wiring), so a
consumer app doesn't reimplement them per project; mdd-booster owns the parts that depend on
the model itself. Neither side assumes anything about which app is consuming it.

## Layers

| Project | Role |
|---|---|
| `Iyu.Core` | `IyuEntity` base class, marker attributes (`[Lookup]`, `[Rollup]`, `[Computed]`, `[Reference]`), value objects (`PhoneNumber`, `EmailAddress`, `WebUrl`), identity contracts, and the attachment contracts (`IAttachmentStorage`, `FileAccessToken`, `FileAccessTokenService`) |
| `Iyu.Data` | `IyuDbContext` base + `IyuTimestampInterceptor` (automatic `CreatedAt`/`UpdatedAt`) + `IyuDateTimeOffsetNormalizationInterceptor` (normalizes every saved `DateTimeOffset` to UTC) + EF Core `ValueConverter`s for the value objects |
| `Iyu.Server.OData` | `IyuEdmModelBuilder.AddEntityPair<TRead,TWrite>(setName)` + generic `IyuODataController<TRead,TWrite>` (CRUD), `$search` binder (searches `[Searchable]` properties when a type declares any, otherwise every string property; a `[Searchable]` reference navigation searches the referenced type's text, one step), `$filter` binder (`in` accepts `[EnumMember]` wire values like `eq`) |
| `Iyu.Server.GraphQL` | `IyuGraphQLSchemaBuilder.AddEntityPair<TRead,TWrite>(queryName, mutationPrefix)` (HotChocolate-based) |
| `Iyu.MainServer` | Composite — `AddIyuMainServer` / `UseIyuMainServer`; also `AddIyuIdentity` / `MapIyuIdentity` (cookie + JWT bearer sessions; service clients, local credentials and refresh tokens each turned on by their own call). Serves OData; carries no GraphQL dependency |
| `Iyu.MainServer.GraphQL` | The GraphQL surface for `AddIyuMainServer` — `options.GraphQL`, `/graphql`. Reference it only if the host serves GraphQL (below) |
| `Iyu.MainServer.OpenIdConnect` | `AddIyuBackChannelLogout` / `MapIyuBackChannelLogout` — receives an OpenID Connect provider's logout notices and ends the sessions opened from the session they name. Reference it only if the host signs people in through an OpenID Connect provider and wants that (see «Identity») |
| `Iyu.FileServer` | `AddIyuFileGateway` / `MapIyuFileGateway` — token-gated byte gateway with Azure Blob and local filesystem backends |
| `Iyu.Server.Chat` | `AddIyuChat` / `UseIyuChat` — bare-chat adapter |
| `Iyu.VaultAi` | `AddVaultAiReports` / `UseVaultAiReports` — scheduled report generation |
| `Iyu.Report` | `AddIyuReport` — office-document template rendering via [DocuChef](https://github.com/iyulab/DocuChef); unrelated to `Iyu.VaultAi`'s scheduled reports, see below |
| `Iyu.DocConvert` | `AddIyuDocConvert` — `IDocumentConverter.ConvertToPdfAsync`, backed by a self-hosted [Gotenberg](https://gotenberg.dev) instance |

## Namespaces a consumer needs

Entity classes, the context, and the controllers all sit in different packages, so
a consuming project ends up repeating the same `using` lines in most of its files.
Declaring them once — in `GlobalUsings.cs`, or any file, with `global using` — is
usually less friction:

```csharp
global using Iyu.Core.Attributes;   // Lookup, Rollup, Computed, Reference, Binding, Searchable, DecimalDigits
global using Iyu.Core.Entities;     // IyuEntity
global using Iyu.Data;              // IyuDbContext
global using Iyu.MainServer;        // IyuMainServerOptions
global using Iyu.Server.OData;      // IyuODataController<,>
```

Take the lines for the packages you actually reference; a project with no OData
surface has no reason for the last one. Anything missing shows up as a normal
`CS0246` naming the type, so the fix is mechanical.

## Minimum consumer

```csharp
using Iyu.MainServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIyuMainServer<AppDbContext>(
    configureDb: db => db.UseSqlServer(builder.Configuration.GetConnectionString("Default")),
    configure: options =>
    {
        options.ODataModel.AddEntityPair<OrderExt, Order>("Orders");
        options.GraphQL   .AddEntityPair<OrderExt, Order>("orders", "order");
        // ...additional pairs, or a generated RegisterEntities(options) call
    });

var app = builder.Build();
app.UseIyuMainServer();
app.Run();
```

**GraphQL is its own package.** `options.GraphQL` comes from `Iyu.MainServer.GraphQL` — reference it and the line
above compiles as written (the property is an extension in the `Iyu.MainServer` namespace); a host that serves only
OData references `Iyu.MainServer` alone and carries none of the GraphQL stack. Serving GraphQL brings HotChocolate,
whose `HotChocolate.AspNetCore` depends on `ChilliCream.Nitro.App` — distributed under the ChilliCream License 1.0,
not an OSI license, with a notice obligation for whoever ships it. A host that never touches `options.GraphQL`
maps no `/graphql`.

Resulting endpoints:
- `GET /$data/$metadata` — OData EDM document
- `GET /$data/Orders?$filter=Status eq 'confirmed'` — OData query
- `POST /graphql` with `{ orders(first: 50) { nodes { ... } pageInfo { hasNextPage endCursor } } }` — GraphQL query (a cursor connection, at most `options.GraphQL.MaxPageSize` rows per request)

### Placing your own middleware

`UseIyuMainServer()` is three calls and nothing else — `UseIyuErrorResponses()`, `UseRouting()`,
`MapIyuMainServer()`.

**Middleware added after it already runs between routing and the endpoints.** The endpoints execute at the end of
the pipeline, so this is the place for middleware that needs the endpoint routing chose — authorization, or
anything of your own that reads it:

```csharp
app.UseAuthentication();   // before or after UseIyuMainServer — it does not read the endpoint
app.UseIyuMainServer();
app.UseAuthorization();    // routing has run; the endpoint has not
app.UseMiddleware<AuditMiddleware>();
```

**When that is not the place you need, make the three calls yourself:**

```csharp
app.UseIyuErrorResponses();   // the exception handler, and error bodies for the OData route's 401/403/404/405
app.UseMiddleware<RequestTimingMiddleware>();   // e.g. something that must run before routing
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapIyuMainServer();       // the OData controllers and every optional surface's endpoints
```

Compose from these two pieces rather than from what they contain: what `UseIyuMainServer()` gains in a later
release it gains in one of them. A pipeline that leaves `UseIyuErrorResponses()` out starts and serves — the
failures under the OData route simply answer with no body — so the host says it once at startup: a warning
under the log category `Iyu.MainServer.Pipeline`. It is a warning, not a refusal to start, because a host may
write those responses itself.

## Identity

`AddIyuIdentity` wires authentication/authorization *infrastructure*: dual-scheme authentication
(cookie for browsers/API clients, JWT bearer for access tokens), per-permission authorization
policies built from a `permissionCatalog`, and the services that open a person's session. It needs
no store. Each further surface is turned on by its own call, and **only a surface that is on asks
for its store**:

| Call | Surface | The app registers |
|---|---|---|
| `AddIyuServiceClients()` | Service clients — the OAuth2 `client_credentials` grant, the owner's management endpoints, and the check of issued tokens against the store | `IServiceClientStore` |
| `AddIyuUserCredentials()` | The local password check — `UserCredentialService` | `IUserCredentialStore` |
| `AddIyuRefreshTokens()` | People's refresh tokens — `UserTokenService`, the `refresh_token` grant | `IRefreshTokenStore` |
| `AddIyuExternalSignIn()` | Sign-in through external providers | `IExternalLoginResolver` |
| `AddIyuSessionEnds()` | Ending a session for good — signing out, signing in again, revoking a refresh token, or the provider's own session behind it ending — so a copy of its cookie or an access token issued in it is refused | `ISessionEndStore` |

A host that registers a surface's store without turning the surface on is refused at startup, with
the call to add in the message. `MapIyuIdentity` maps `POST /api/auth/token` — which answers the
grants that are on, and `unsupported_grant_type` for the rest — and, with service clients on,
`/api/service-clients/*`, the owner's own management endpoints. Those answer only a request that carries a
session cookie: a bearer token — a person's or a service client's — never manages service clients, even when
the host narrows the group with a permission policy that accepts bearer tokens
(`app.MapGroup("").RequireAuthorization("identity.admin").MapIyuIdentity()` narrows who; it does not widen how).

**An administrator's view of every owner's service clients** is mapped separately, behind a policy the host
names — the framework knows no administrator:

```csharp
app.MapIyuServiceClientAdministration("identity.admin");   // a code from your permission catalog, or any policy
// GET    /api/admin/service-clients[?owner={id}][&issuer={id}]   every owner's clients (no secrets), revoked ones marked
// DELETE /api/admin/service-clients/{id}            revoke one, whoever owns it
// DELETE /api/admin/service-clients/owner/{owner}   revoke every active client of one owner → { "revoked": n }
```

It is the step after a password reset: a reset and a signed-out session do not end the credentials an account
issued, and the owner is the one person who cannot be asked to find them. These endpoints also require a
session cookie, and the policy is met by the person whose session it is: an administrator's bearer token
presented beside someone else's session cookie is answered `403`, as it is alone. The store answers them (`ListServiceClientsAsync`, `DeactivateAsync(id)`,
`DeactivateByOwnerAsync`), and each summary names its owner and who issued it (`ServiceClientSummary.OwnerUserId`,
`IssuedBy`).

**An administrator can also act on a service account's behalf** — issue, rotate and re-scope the credentials held
under an account that is not a person, without holding that account's permissions. The host says which accounts
those are; until it does, an administrator issues nothing:

```csharp
builder.Services.AddScoped<IServiceAccounts, MyServiceAccounts>();   // IsServiceAccountAsync(accountId, ct)
// POST  /api/admin/service-clients                  { ownerUserId, displayName, permissions, expiresAt } → id, clientId, secret
// POST  /api/admin/service-clients/{id}/rotate      a new secret
// PATCH /api/admin/service-clients/{id}/permissions { permissions }
```

The ceiling is the service account's permissions as they are now — the same live ceiling an owner's own
credential has — not the administrator's: an accounts administrator need not hold the business permissions a
scheduler's credential carries (separation of duties). A person's account is refused (`403
owner_not_service_account`): acting in a person's name would hand the administrator that person's permissions.
What it separates is who may mint a credential, not who may use one: an issue or a rotation answers the secret
to the administrator who asked, once, and its holder obtains tokens with the service account's permissions. So each
act is logged with the administrator — refusals too — and the store records the issuer (`InsertAsync(…, issuedBy,
…)`): `?issuer=` finds what a compromised administrator account issued, since its credentials are owned by service
accounts, not by it. A store that existed before `IssuedBy` fills it for its rows with the owner — an owner's own
credential is the only kind there was.

**There is no built-in human sign-in endpoint, local or federated.** That
is deliberate: how a person is authenticated — the credential check, the route, what the response
says — is the consuming app's, the same way the generic OData controller leaves business validation
to the consumer.

**A wider permission can cover a narrower one.** Each catalog code is a policy of the same name, met by
that code — and by any code declared to imply it:

```csharp
builder.Services.AddIyuIdentity(tokenOptions,
    permissionCatalog: ["orders.read", "orders.write"],
    permissionImplications: [new("orders.write", "orders.read")]);   // orders.write also meets orders.read
```

Implication is transitive, and it applies wherever the framework decides from codes: the policies, and
the rule that a service client never exceeds its owner (an owner holding `orders.write` may give a client
`orders.read`). What is issued is not expanded — a session or token carries the codes it was given.
`PermissionHierarchy.Expand(codes)` gives application code the full set, e.g. to decide what a screen shows.
A code named outside the catalog, or two codes implying each other, is refused at registration.

**What the framework does own is the step after: opening the session.** Once the app knows which
account it is, it names that account's subject and the framework assembles the session —
`UserSessionService.SignInAsync(http, subject)` for the session cookie,
`UserAccessTokenService.IssueAsync(subject)` for an access token, `UserTokenService.IssueAsync(subject)`
for an access token with a refresh token. All of them ask one port, which every host registers:

```csharp
builder.Services.AddScoped<IUserClaimsSource, AppUserClaimsSource>();

public sealed class AppUserClaimsSource(AppDbContext db) : IUserClaimsSource
{
    // What this person may do here, or null to refuse them (no account, deactivated).
    public async Task<IReadOnlyCollection<Claim>?> GetClaimsAsync(string subject, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([Guid.Parse(subject)], ct);
        if (user is null || !user.IsActive) return null;
        return [.. (await PermissionCodesAsync(user, ct)).Select(code => IyuIdentityClaims.Permission(code))];
    }
}
```

- **The permission claims are not assembled by hand.** A sign-in endpoint that builds its own
  `ClaimsPrincipal` has to remember one claim per granted permission, under the claim type the policies
  read; leave them out and service clients work while every person gets 403. The source is asked
  instead, for the cookie and the token alike, so the two cannot describe the same person differently.
- **The identifier is the framework's to write** — from the subject, as `ClaimTypes.NameIdentifier` in
  the cookie and `sub` in the token (which the bearer handler presents as `ClaimTypes.NameIdentifier`
  too). `IyuIdentityClaims.FindSubject(user)` reads it on either. A source need not answer it; one that
  answers an identifier naming someone else is an error, not a sign-in.
- **A host with no `IUserClaimsSource` is refused at startup.** An application that signs no person in
  registers one that answers `null`.
- **A service client's owner is asked too** (with `AddIyuServiceClients`) — by the owner's id as a GUID string,
  when a client is issued or re-scoped, when it obtains a token, and while its tokens are checked. A client never
  carries more than its owner holds now; an owner the source refuses (deactivated) leaves their clients nothing.
- **The source is asked again while a cookie session lives** — every
  `IdentityTokenOptions.SessionRevalidationInterval` (default five minutes; `TimeSpan.Zero` asks on every
  request). A person the source now refuses is signed out; a narrowed permission set replaces the one in the
  cookie, which is re-issued. That is what a refresh does for an access token, and the interval is the longest
  a deactivation or a withdrawn permission takes to reach the cookie. A request inside the interval costs a
  timestamp comparison. The cookie is written again only when there is something to write — claims that
  changed, or, with an interval, the time of the answer (once per interval). With `TimeSpan.Zero` a session the
  source answers the same for is not re-issued at all.
- **A session belongs to the state of the account it was opened in — answer a security stamp to end the
  others.** Have the source include `IyuIdentityClaims.SecurityStamp(value)` in its answer, with a value that
  changes whenever every open session of the account must end: the password was changed or reset, a second
  factor was added, the person signed out everywhere. A session cookie keeps the stamp it was opened under and
  ends at its next revalidation when the source answers another; a refresh token keeps it too
  (`RefreshTokenRecord.SecurityStamp` — persist it), and presenting one issued under an older stamp revokes its
  family. The comparison is made before the session's claims are replaced by the new answer — a check of your
  own placed after the framework's revalidation sees the account as it is now and always passes, so this is
  where "is this still that session" is decided. A source that never answers a stamp changes nothing. The
  comparison runs both ways: once a source starts answering one, a session or refresh token opened without it
  ends the next time the source is asked (one sign-in at adoption) — and one opened under a stamp ends the same
  way if the source stops answering it, so an account that has a stamp keeps having one. The stamp travels in
  the cookie and in access tokens, which are readable: use a random value stored with the account or a keyed
  digest, never a credential hash itself.
- **An access token is not looked at again while it lives — unless you ask for it.** By default a person's
  access token is as good as the day it was signed until it expires: a changed password or a deactivated
  account reaches it at the next refresh, which is why its lifetime is kept short
  (`UserAccessTokenLifetime`). `IdentityTokenOptions.ValidateUserAccessTokens = true` checks it on every
  request instead: the source is asked about the token's subject, and the token is refused (`401`) when the
  source declines the subject or answers a stamp other than the one the token carries — the two things that
  end a session cookie. It costs one question to the source per request that presents such a token, and it
  decides only whether the token still belongs to the account: the permissions a token carries stay the ones
  it was issued with until it is refreshed. Service-client tokens have their own check
  (`ValidateServiceClientTokens`, on by default). Separately, where session ends are on, a token issued with a
  refresh token is refused once its sign-in has ended — a store read, not a question to the source (see the
  refresh-token section).
- **A session cookie written by hand is not exempt.** One an endpoint signed in itself, without
  `UserSessionService`, is re-assembled from the source the first time it is presented, by the identifier it
  carries (`ClaimTypes.NameIdentifier` or `sub`): claims the endpoint added and the source does not answer are
  gone, and a cookie carrying no identifier is signed out. The one exception is a host that set
  `AllowExternalIdentityAsSession` (below) — there such a cookie is the provider's identity and is left as it is.
- **An access token cannot be written by hand at all.** A bearer token is not re-assembled while it lives, so
  there is nothing to correct it later — every person's token is signed over what the source answered.
- **One signing algorithm, declared.** Tokens are signed with HMAC-SHA256 under `SigningKey`, and the bearer
  scheme accepts that algorithm and no other: a token signed with another HMAC under the same key is refused,
  as is an unsigned one (`alg: none`).

**A cookie lasts as long as the key ring that sealed it.** Cookie sessions are protected by ASP.NET Core
Data Protection, and where its keys live is the application's choice — the framework does not pick a store.
With none configured, the keys stay in the process's own profile directory; inside a container that directory
goes with the container, and every redeploy signs every web user out. So a host that would run that way —
outside Development, no key repository, `DOTNET_RUNNING_IN_CONTAINER` set — **is refused at startup**. Persist
the ring and fix the application name:

```csharp
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AppDbContext>()      // Microsoft.AspNetCore.DataProtection.EntityFrameworkCore
    .SetApplicationName("my-app");              // or PersistKeysToFileSystem(...) on a mounted volume
```

A deployment that accepts sessions ending with the container sets
`IdentityTokenOptions.AllowContainerLocalKeyRing`. Outside a container the default store (user profile or
registry) outlives the process, so nothing is refused there.

Two recipes for a sign-in endpoint — same shape, only the way the account is established differs:

**Local username/password:**

```csharp
builder.Services.AddIyuUserCredentials();
builder.Services.AddScoped<IUserCredentialStore, AppUserCredentialStore>();   // FindUserByUsernameAsync over your users

app.MapPost("/api/auth/login", async (LoginRequest req, UserCredentialService credentials, UserSessionService sessions,
    HttpContext http, CancellationToken ct) =>
{
    var check = await credentials.VerifyAsync(req.Username, req.Password, ct);
    if (!check.Ok) return Results.Unauthorized();

    return await sessions.SignInAsync(http, check.User.Id.ToString(), ct: ct) ? Results.Ok() : Results.Unauthorized();
}).AllowAnonymous();
```

`UserCredentialService` (registered by `AddIyuUserCredentials`) finds the account, verifies the password against
`IUser.PasswordHash`, and refuses an account that is not active. Every check costs one hash verification —
an unknown name and an account without a password included — so response time does not tell which names are
accounts, and the password is verified before the account's state is read. The hasher is ASP.NET Core
Identity's `PasswordHasher<T>`: hash a password you store with the same `IPasswordHasher<IUser>`. **Stored
hashes in another format need a hasher that reads them** — register your own `IPasswordHasher<IUser>`; without
it those accounts cannot sign in (a hash it cannot parse is `CredentialFailure.UnreadableHash`, logged as an
error). What follows a check stays yours: `check.Failure` says why it failed (count `WrongPassword` toward a
lockout, but answer the person the same way for all), and a non-null `check.UpgradedPasswordHash` — the hash
was made with older settings — is the value to store in its place.

Sign-out: `await sessions.SignOutAsync(http)` — it also ends the person's session at an external provider
that opened this one (below); for a session opened as above it is the cookie scheme's own sign-out.

**A sign-out removes the browser's cookie — it ends the session only with `AddIyuSessionEnds()`.** A session
cookie is held by the browser: without session ends, a copy of it kept elsewhere is accepted until the cookie
expires, and with sliding expiration that is as long as it keeps being used. Turn them on and the end is
recorded and asked about on every request that presents a session:

```csharp
builder.Services.AddStackExchangeRedisCache(o => { /* ... */ });                     // a cache every instance shares
builder.Services.AddSingleton<ISessionEndStore, DistributedCacheSessionEndStore>();
builder.Services.AddIyuSessionEnds();
```

With them on, `SignOutAsync` ends that session — the person's other sessions are untouched (the security stamp is
what ends all of them) — and `SignInAsync` on a request that already carries a session ends that one before
opening the new one. The store is read on every request that presents a session cookie (twice for one opened
through a provider, below): keep it cheap, and shared — over `AddDistributedMemoryCache()` the record lives in
one process, which is right for a single instance and ends the session on one instance only behind a load
balancer. A cookie this service did not open names no session and cannot be ended this way.

**External OIDC / AD federation** (a customer-hosted IdP — common in on-premises deployments
where each site brings its own directory):

```csharp
builder.Services.AddIyuExternalSignIn();                          // the scheme that holds the provider's answer
builder.Services.AddAuthentication()
    .AddOpenIdConnect("oidc", opts => { /* Authority, ClientId, ClientSecret from config */ });
builder.Services.AddScoped<IExternalLoginResolver, AppExternalLoginResolver>();

app.MapIyuExternalSignIn();   // GET /api/auth/external/{provider}?returnUrl=…  and  GET /api/auth/external/complete

public sealed class AppExternalLoginResolver(AppDbContext db) : IExternalLoginResolver
{
    // Which local account this external sign-in is — or null to refuse it.
    public async Task<string?> ResolveSubjectAsync(ExternalLogin login, CancellationToken ct)
    {
        var account = await db.ExternalLogins
            .Where(l => l.Provider == login.Provider && l.ProviderKey == login.ProviderKey)
            .Select(l => l.UserId).SingleOrDefaultAsync(ct);
        return account == Guid.Empty ? null : account.ToString();   // or provision one here
    }
}
```

A link (or a button) to `/api/auth/external/oidc?returnUrl=/reports` sends the browser to the provider.
The handler brings it back to `/api/auth/external/complete`, which asks the resolver which local account
this is and opens that account's session with `UserSessionService` — the claims the application's
`IUserClaimsSource` answers, nothing the provider sent. Then it redirects to `returnUrl` (only a path on
this application; anything else is 400) or to `/`. The provider's answer is discarded whatever the
outcome: 401 if there was none, 403 if the resolver or the claims source refuses.

**The provider's identity is not the session.** An external sign-in handler stores what the provider
told it in its `SignInScheme`, and when none is named that is the default sign-in scheme.
`AddIyuExternalSignIn` makes that its own cookie (`IyuExternalSignInDefaults.AuthenticationScheme`, five
minutes), which nothing the application serves accepts. Without it, the default would be the session
cookie `AddIyuIdentity` registered, and the provider's claims would be the session as they stand: anyone
the provider authenticates is signed in whether or not a local account exists, and a claim the provider
sends under the permission claim type (`perm`) satisfies the permission policies. **A host whose handler
would sign into the session cookie is refused at startup**; an application that really means the
provider's identity to be its session (no local accounts, authorization that does not rest on those
claims) sets `IdentityTokenOptions.AllowExternalIdentityAsSession`. With `AddIyuExternalSignIn`, a
`HttpContext.SignInAsync(principal)` that names no scheme also lands in the external cookie — a person's
session is opened by `UserSessionService`, which names its scheme.

The resolver is where each deployment's policy lives: whether a first-seen external identity is
provisioned or must already be linked, and which of its claims fill which local fields. `ProviderKey` is
the person's identifier at that provider (its `sub`) and is unique only together with `Provider` — key
links on the pair. A host that calls `AddIyuExternalSignIn` without registering a resolver is refused at
startup.

**Signing out** of a session the provider opened ends it at the provider too. `MapIyuExternalSignIn`
also maps `POST /api/auth/sign-out?returnUrl=…`, which calls `UserSessionService.SignOutAsync`: the
session cookie ends, and when the provider's handler can sign out (OpenID Connect does — RP-initiated
logout) the browser goes to the provider's end-session endpoint with the identity token the provider
issued (`id_token_hint`), then back to `returnUrl`. That token is kept in the session when the handler
keeps its tokens — set `SaveTokens = true` on it; without it the provider is asked to sign out without
knowing which session. A session with no provider behind it simply ends, and the browser goes to
`returnUrl` (or `/`). Use a form `POST`, not a fetch: the response is a redirect for the browser to follow.
With external sign-in on, a `SignOutAsync()` that names no scheme still ends the session cookie.

**When the provider's session ends somewhere else** — the person signed out of another application under
the same provider, or at the provider itself — nothing above notices: the account has not changed, so no
revalidation would end the session here. A session opened through `MapIyuExternalSignIn` remembers the
provider's session behind it (the identity token's `sid`, when the provider sends one — kept beside the
session's claims, never among them), and session ends (`AddIyuSessionEnds()`, above) make that usable:
`SessionEndService.EndProviderSessionAsync(provider, sessionId)` records that the provider's session is
over, and every session opened from it ends the next time its cookie is presented — on that very request,
whatever `SessionRevalidationInterval` is. A session whose provider sent no `sid` cannot be ended this way.

**What calls it for an OpenID Connect provider is the provider's own logout notice** — OpenID Connect
Back-Channel Logout 1.0, received by the `Iyu.MainServer.OpenIdConnect` package:

```csharp
builder.Services.AddSingleton<ISessionEndStore, DistributedCacheSessionEndStore>();
builder.Services.AddIyuBackChannelLogout();          // turns AddIyuSessionEnds on as well
// ...
app.MapIyuBackChannelLogout();                       // POST /api/auth/external/{provider}/backchannel-logout
```

Register the endpoint's absolute address with the provider as the client's `backchannel_logout_uri`
(`https://app.example/api/auth/external/oidc/backchannel-logout` for a handler added as `"oidc"`), together
with `backchannel_logout_session_required: true`. When the person's session at the provider ends — in
another application, or at the provider — the provider posts a signed logout token there, and every
session this application opened from that session ends on its next request.

- **The token is checked against the handler itself**: the issuer and signing keys of the provider that
  `AddOpenIdConnect("oidc", …)` points at, and its `ClientId` as the audience. Nothing about the provider
  is configured a second time. An identity token is not a logout token and is refused, as is a token that
  carries a `nonce`, lacks the logout event, or is unsigned. It must be signed with an algorithm the
  handler's `TokenValidationParameters.ValidAlgorithms` lists or, when that is unset, one the provider
  advertises in `id_token_signing_alg_values_supported`; a provider that advertises none leaves it to its keys.
  A token signed with a key the provider's document does not list has the document read again once, as the
  handler does for an identity token (`RefreshOnIssuerKeyNotFound`) — a provider that rolled its keys is
  answered at once, not after the document's next scheduled refresh.
- **The token must name the session** (`sid`). One that names only a person (`sub`) is refused with a 400
  rather than half honoured — sessions are not kept by the provider's subject here. That is what
  `backchannel_logout_session_required` asks the provider for.
- **Expiry.** The specification requires `exp` on a logout token; providers in use send none. A token that
  carries one is held to it. One that does not is accepted while it is young — five minutes from `iat` by
  default (`MaxAgeWithoutExpiration`), plus the handler's clock skew — or refused outright with
  `AddIyuBackChannelLogout(o => o.RequireExpiration = true)`.
- **Answers.** 200 with no body when the token is accepted, whether or not anyone here held a session from
  it; 400 with `{"error":"invalid_request","error_description":…}` when it is refused; 404 when
  `{provider}` is not an OpenID Connect handler. The endpoint is anonymous — the signature is what
  authenticates the provider — so it must be reachable from the provider, not only from browsers.
- **What it does not cover.** A notice the provider failed to deliver is not retried by anything here, and
  revalidation cannot make up for it: the claims source knows the account, not the provider's session.
  An access token issued on its own (`UserAccessTokenService`) is not tied to the provider's session and is
  not ended by a notice. A refresh-token sign-in is, when the application opened it from that session —
  `UserTokenService.IssueAsync(subject, new SessionKey("oidc", sid), ct)` with the `sid` of the identity token
  it verified: the first refresh after the notice is refused and ends the sign-in, its access tokens included
  (until then, an access token already issued lives out its lifetime), and a session already ended opens no
  sign-in. The notice is kept for the longer of the session cookie's lifetime and `RefreshTokenLifetime`, so a
  refresh token left unused that long still meets it. The store must keep
  `RefreshTokenRecord.ProviderSession`.

**Native/mobile clients (bearer token instead of a cookie):** a client that cannot hold a cookie
session verifies the user by whichever means the app already uses (the local check above, or an
external IdP), then asks `UserAccessTokenService` for an access token — the bearer counterpart of
`UserSessionService.SignInAsync`:

```csharp
app.MapPost("/api/auth/mobile-login", async (LoginRequest req, UserCredentialService credentials, UserAccessTokenService tokens, CancellationToken ct) =>
{
    var check = await credentials.VerifyAsync(req.Username, req.Password, ct);
    if (!check.Ok) return Results.Unauthorized();

    var result = await tokens.IssueAsync(check.User.Id.ToString(), TimeSpan.FromDays(30), ct);
    return result.Ok
        ? Results.Ok(new { access_token = result.AccessToken, expires_in = result.ExpiresInSeconds })
        : Results.Unauthorized();
}).AllowAnonymous();
```

The token is assembled the way a session cookie is: the claims `IUserClaimsSource` answers for the
subject (the permission claims included), and `sub` written from the subject. A subject the source
refuses gets no token (`Ok == false`, `invalid_grant`). Nothing here authenticates — the app has done
that — and `AddIyuIdentity` registers the service; it needs nothing beyond the claims source.
The lifetime argument is optional: unset, it is `IdentityTokenOptions.UserAccessTokenLifetime`, or
`Lifetime` when that is unset too. A longer one serves a client without a refresh flow (a mobile app
queuing work offline), but **a token issued here cannot be taken back on its own** — it names no
sign-in, so neither signing out nor revoking ends it, and a person deactivated after it was issued keeps
it until it expires unless every token is checked against the account
(`IdentityTokenOptions.ValidateUserAccessTokens`). Prefer the refresh flow below for a public client
that stays signed in: with session ends on, revoking its refresh token ends its access tokens too.

There is no public way to sign a person's token over claims the caller put together: such a token would
carry whatever permissions the caller wrote, for as long as it lives.

**Refresh tokens (short access, rotating refresh — RFC 9700 §4.14, RFC 8252):** turn them on, register
the store, and sign the person in through `UserTokenService` instead:

```csharp
builder.Services.AddIyuRefreshTokens();                                        // without it, no store is needed
builder.Services.AddScoped<IRefreshTokenStore, EfRefreshTokenStore>();        // your table of RefreshTokenRecord

app.MapPost("/api/auth/mobile-login", async (LoginRequest req, UserCredentialService credentials, UserTokenService users, CancellationToken ct) =>
{
    var check = await credentials.VerifyAsync(req.Username, req.Password, ct);
    if (!check.Ok) return Results.Unauthorized();

    var pair = await users.IssueAsync(check.User.Id.ToString(), ct);
    return pair.Ok
        ? Results.Ok(new { access_token = pair.AccessToken, expires_in = pair.ExpiresInSeconds, refresh_token = pair.RefreshToken })
        : Results.Unauthorized();
}).AllowAnonymous();
```

The client then exchanges its refresh token at the existing endpoint —
`POST /api/auth/token` with `grant_type=refresh_token&refresh_token=…` (form or JSON) — and gets the
next access token and the refresh token that replaces the one it sent. What the flow guarantees:

- **Each refresh token works once.** Presenting one a second time revokes every token descended from
  that sign-in — the server cannot tell the client from whoever copied the token. A client must keep
  the newest refresh token it received; by default, one that retries a refresh whose response it lost
  is signed out.
- **A lost response can be retried, if you allow it.** `IdentityTokenOptions.RefreshTokenReuseInterval`
  (default `TimeSpan.Zero` — strict) lets a used refresh token be presented again for that long after
  it was exchanged: the retry gets a new pair and the pair the lost response carried is retired, so the
  sign-in still has one live refresh token. Once that lost pair has been used, or after the interval,
  a second presentation revokes the sign-in as before. The price is that for those seconds a copy of
  the old token works too — set it to the time a retry takes (a few seconds), for clients on networks
  that drop responses.
- **Every refresh asks `IUserClaimsSource` again.** A deactivated person (the source answers
  `null`) cannot refresh, and a narrowed permission set reaches the next access token — within one
  access-token lifetime. Set `IdentityTokenOptions.UserAccessTokenLifetime` (e.g. 15 minutes) to keep a
  person's access token short without shortening service-client tokens; unset, it is `Lifetime`.
- **Sign-out**: `UserTokenService.RevokeAsync(refreshToken)` ends one sign-in,
  `RevokeAllAsync(subject)` every sign-in of the person (password change, deactivation). An access token
  names the sign-in it was issued in (`sid`): with session ends on (`AddIyuSessionEnds()`), the access tokens
  of a sign-in that `RevokeAsync` ended are refused from then on — and so are those of a sign-in ended by a
  reused refresh token, a changed security stamp or a declined account. `RevokeAllAsync` ends the refresh
  tokens only; the access tokens of every sign-in at once end with the security stamp
  (`ValidateUserAccessTokens`). Otherwise access tokens already issued run out on their own.
- **Lifetime**: `IdentityTokenOptions.RefreshTokenLifetime` (30 days by default), renewed at every
  refresh — a client used at least that often stays signed in.
- **The store** keeps only the token's hash (`UserTokenService.HashRefreshToken`) and must make
  `TryMarkUsedAsync` one conditional write (`… WHERE Id = @id AND UsedAt IS NULL`), so two requests
  racing with one token cannot both win. It also stores `ParentId` — the token each one was issued in
  exchange for — and answers `FindLatestChildAsync(parentId)` (the newest token with that parent; index
  the column), which the reuse interval uses to find the pair a lost response carried.

Without the two ports, `grant_type=refresh_token` answers `unsupported_grant_type` and nothing else changes.
With only one of them, a refresh request fails with an exception naming the missing one.

### Second factors, tenants and devices — where each one goes

Identity has no built-in sign-in, so the questions that hang off signing a person in are answered
by the seams above, not by more types in this package:

- **A second factor** belongs in your sign-in endpoint, between the credential check and
  `SignInAsync` / `UserTokenService.IssueAsync`: nothing is signed in until your code says so, so a
  TOTP check, a mailed code or a hardware key is one more condition there. The token and cookie
  surfaces do not change.
- **Tenant scoping** is a column your models carry and two rules: a global query filter on your
  `IyuDbContext` (`HasQueryFilter(e => e.TenantId == currentTenant)`) — every read surface, OData and
  GraphQL alike, queries through it — and a write rule that stamps or checks the column on the generic
  write path (see «Rules on the generic write path»). Which claim names the tenant is your token's
  content (`IUserClaimsSource`).
- **A device** is already a sign-in: each refresh token belongs to one, `RevokeAsync` ends it, and
  `RevokeAllAsync` ends every one. A list of trusted devices, push endpoints and the like is a model of
  your own, keyed by the person.

### Diagnosing a service client that stopped working

`ServiceClientSummary` — what `ListServiceClientsAsync` returns to an owner — carries two
timestamps that answer different questions, and the pair is what makes a failing credential
diagnosable without reading the database:

| | Means |
|---|---|
| `LastUsedAt` | When it last obtained a token, or `null` if it never has |
| `SecretRotatedAt` | When the secret was last replaced, or `null` if it is still the one issued |

Read together they separate causes that look identical from the outside — the endpoint answers
every rejection with one `invalid_client`, deliberately:

- `SecretRotatedAt` **newer than** `LastUsedAt` — the holder is presenting the previous secret.
  The credential is healthy; the rotation was never delivered.
- `LastUsedAt` is `null` on an active client — the secret never worked at all, so what was
  handed over was wrong from the start.
- Neither, and `IsActive` is false — it was revoked.

`SecretRotatedAt` names one event rather than being a general "last modified": a permission
change is not a rotation, and folding the two into one column puts the owner back to guessing
which happened.

**Implementing the store**: `IServiceClientStore.UpdateSecretAsync` receives the rotation
timestamp — persist it, and return it on the summary. The value is passed in rather than taken
from the store's own clock so that a rotation and a last-use can be compared without having been
stamped by two different machines.

**Server-side diagnosis**: rejected `client_credentials` requests are logged with the cause —
no such client, revoked, expired, or secret mismatch — at `Warning`, against the category
`Iyu.MainServer.Identity.ServiceClientTokenService`. The *response* stays one undifferentiated
`invalid_client` with equalized timing, which is what stops the endpoint confirming which client
ids exist; the log is how the operator, who already has the database, is not left with the same
information as the caller. The secret is never logged in any form.

## Read/Write pair model

Each logical entity has two CLR types:

- **Write type** (e.g. `Order`) — mapped to the base SQL table. Contains only
  stored fields. Used for POST/PATCH/DELETE inside the controller.
- **Read type** (e.g. `OrderExt`) — mapped to a SQL view. Contains stored
  fields **plus** lookups/rollups/computed fields. Exposed as the OData entity
  set and GraphQL query field.

The controller copies overlapping properties from the read body to a fresh
write entity using reflection; extras are dropped. `CreatedAt`/`UpdatedAt`/`Id`
are explicitly excluded because they are owned by the interceptor or the
caller's explicit assignment. On a `PATCH` the drop is not unconditional — an
update that could store *none* of what it was sent is refused rather than
reported as done; see [What a `PATCH` can store](#what-a-patch-can-store).

### Write failure responses

`AddIyuMainServer` wires a global exception handler (`AddExceptionHandler` + `AddProblemDetails`,
completed by `app.UseExceptionHandler()` in `UseIyuErrorResponses()`, the first thing `UseIyuMainServer`
calls) so no unhandled write-path exception
reaches a client raw. A `SaveChangesAsync` failure that surfaces as `DbUpdateException` — a
unique-index violation, a foreign-key violation, an optimistic-concurrency conflict — is answered
with a `409 Conflict`; every other unhandled exception still falls through to a generic, structured
`500`. Neither response includes the underlying exception's message or type: this framework stays
provider-agnostic (no Npgsql/SqlClient package reference), so it does not attempt to distinguish
*which* constraint failed — only that the write conflicted with the current state of the data. No
opt-in is needed; a consumer that never hits either path pays nothing for it.

**Under the OData route these are OData errors**, like every refusal the controller makes there —
one format per route, so a client that reads `error.code` reads these too:

```json
{"error":{"code":"Conflict","message":"…","innererror":{"traceId":"00-…"}}}
```

`error.code` is `Conflict` (409) or `InternalServerError` (500) — both in `ODataErrorCodes` — and
`innererror.traceId` names the request in the server's logs. The rule is the route, not the cause:
any `ProblemDetails` written for a request under the OData route comes out in this shape, including
one your own exception handler writes; give it a `code` extension and that is the `error.code`.
A failure that would otherwise have no body on the route — a set policy's `401`/`403`, a path that
names no set (`404`, distinct from `KeyNotFound`), a verb nothing there answers (`405`) — carries the
same object, with the status's name as its code. Elsewhere — your `/api` endpoints — the default
`application/problem+json` stays, and body-less responses stay body-less. An
`IProblemDetailsWriter` of your own registered before `AddIyuMainServer` is asked first, so an
application with one error format for every surface keeps it.

⚠ **This handling runs inside `UseIyuMainServer`, so it displaces an exception-catching middleware
placed before it.** `UseIyuMainServer` calls `app.UseExceptionHandler()` as its first step; a
`try`/`catch` middleware registered ahead of that call sits *outside* it and no longer sees a
write-path `DbUpdateException` at all. Nothing breaks loudly when this happens — the status stays
`409` and only the body changes — so a consumer that classified failures itself finds out from its
own tests, not from a compiler or a stack trace.

**To keep provider-specific classification, register your own `IExceptionHandler` before
`AddIyuMainServer`.** The exception-handling middleware calls handlers in registration order and
stops at the first one that returns `true`, so ordering is the whole mechanism:

```csharp
// Yours is asked first; whatever it declines falls through to the provider-neutral 409 above.
builder.Services.AddExceptionHandler<SqlStateExceptionHandler>();
builder.Services.AddIyuMainServer<AppDbContext>(...);
```

Answer only what you recognise — return `false` for everything else rather than mapping unknown
failures yourself, and the layering stays clean: your handler owns the cases your provider lets you
name, this framework owns the rest. Registering it *after* `AddIyuMainServer` reverses the order and
the neutral `409` wins instead.

A request body a client sends *before* `SaveChangesAsync` is even reached can also fail — a
malformed EDM literal (e.g. a `DateTimeOffset` string with no UTC offset) fails OData's own body
binder, not the database. `Post`/`Patch` still answer `400`, but with a generic, sanitized message
in place of the binder's own error: internal vocabulary like the `Edm.*` type name or the
underlying exception's type never reaches the response. An ordinary `[Required]`/annotation-driven
validation failure is unaffected — it never carried that vocabulary to begin with, and is returned
exactly as authored.

### Query failure responses

An invalid query option — an unknown property in `$select` or `$filter`, a malformed literal — is
answered `400` with an OData error whose `message` says what is wrong with the request. Outside the
Development environment that is all it carries: the `innererror` the OData query layer would add
(the exception type and its stack trace) is withheld, because it describes the server rather than
the request. In Development it is written, so the cause is one response away while building.

To decide independently of the environment, set `IncludeODataErrorDetails` — `true` always writes
the details, `false` never does:

```csharp
builder.Services.AddIyuMainServer<AppDbContext>(db => ..., options =>
{
    options.IncludeODataErrorDetails = false;   // e.g. a Development host exposed to testers
});
```

The `message` keeps naming the type the query was bound against. That is the EDM type name,
which `$metadata` already publishes to the same caller.

### Keeping a stored value off the API surface

Every public property of a read type is reachable through `$data` and GraphQL. For a
value that is stored but must never leave the server — a password hash, a client secret —
subtract it from the model:

```csharp
configure: options =>
{
    RegisterEntities(options);   // registration you may not own — see below

    options.ODataModel.Exclude<AccountExt>(x => x.PasswordHash);
    options.GraphQL.Exclude<AccountExt>(x => x.PasswordHash);
}
```

**Name the read type.** It is the type both surfaces expose, and — because request bodies
bind to it — excluding it closes reads and writes together: `$select`, `$filter` and
`$orderby` naming the property are rejected, the GraphQL schema has no such field, and a
`POST`/`PATCH` carrying it fails before anything is stored. The write type is not part of
the exposed model, so naming it excludes nothing; both builders refuse it at startup rather
than let the call quietly do nothing.

The property is *removed*, not blanked. A blank value would be indistinguishable from "this
row has no value", and would still let a caller recover the real one a character at a time
with `$filter=startswith(...)`.

### Per-entity authorization

Beyond authentication, a caller may need a specific claim to touch an entity at all. Declare the
entity's policies **once**, and every surface that serves it enforces them — ASP.NET Core authorization
policy names, the same ones `AddIyuIdentity`'s `permissionCatalog` registers:

```csharp
RegisterGeneratedEntities(options);   // OData sets and GraphQL fields — generated or yours

options.Authorize<OrderExt>(read: "orders.read", write: "orders.write", delete: "orders.delete");
```

| Policy | OData set | GraphQL query field |
|---|---|---|
| `read` | `GET`, and every query that reaches it through a navigation — `$expand`, and a path in `$filter`, `$orderby`, `$apply`, `$compute` or `$select` | the field, and the object type wherever it is selected |
| `write` | `POST`, `PATCH` — and `DELETE` when `delete` is not given | — (GraphQL serves reads only) |
| `delete` | `DELETE`, when deleting is a different permission from editing | — |

A policy belongs to the data, not to the transport it travels over. Declared per surface, a second
surface was a second place to forget it, and the forgotten one was open under the fallback policy — so
there is no per-surface declaration. The read type is the key because it is what every surface registers;
the declaration may come before or after the registration. A declaration for a type no surface serves, or
a second declaration for one type, stops the host at startup. A policy left `null` leaves that access under
the host's `FallbackPolicy`.

The first declaration wires what enforces it — the MVC `AuthorizeFilter` on the OData controllers, and the
bridge HotChocolate needs to evaluate a policy against `IAuthorizationService` (HotChocolate ships the
`.Authorize(policy)` descriptor but no handler that checks it against ASP.NET Core's authorization) — so a
host that declares nothing pays for neither.

### How far a read policy reaches

A read policy protects the **data**, not the one route you declared it on. Both surfaces enforce it
that way, and both did not always — the paragraphs below describe what the framework does now,
because the difference is invisible from a successful response.

**OData — every set a query reaches through a navigation is checked.** A set's read policy is
enforced by an `AuthorizeFilter` on the restricted set's own controller action, and a request that
reaches the set through a navigation is served by the *addressed* set's action without ever entering
the other one. So the check runs separately, against each set a navigation in the query reaches, at
any depth and in whichever option names it:

```http
GET /$data/Orders?$expand=Customer
GET /$data/Orders?$apply=groupby((Customer/Name),aggregate($count as n))
GET /$data/Orders/$count?$filter=startswith(Customer/TaxId,'12')
GET /$data/Orders?$orderby=Customer/CreditLimit desc
```

If `Customers` carries a `readPolicy` the caller does not satisfy, each of these is refused even
though `Orders` is open — `401` without an identity, `403` with one, the same split `Customers`
produces on its own route. Printing a value (`$expand`, `$select`, `$compute`, grouping or
aggregating in `$apply`), testing it (`$filter`, where a count turns a guess into an answer) and
sorting by it (`$orderby`) are all reading it. The options nested in an expand, and what a parameter
alias stands for, are read the same way. The check runs before the query options are bound, so a
refusal costs no query.

A query option is read with or without its `$`. Given under both spellings at once
(`$filter=…&filter=…`), it is refused with `400` — the check does not guess which one the query
would apply.

**Only what a set serves is reachable.** A read type may carry a navigation to a type no entity set
serves — a model the application keeps off its API. Such a navigation is not part of the model: it
is left out of `$metadata`, `$expand` naming it is `400`, and the GraphQL schema leaves the field out
too. There is no set to take a read policy from, so being reachable would mean being unprotected.
The CLR property is untouched, for your own code. Register the target as an entity pair to expose
it through the navigation, with that pair's read policy.

The policy that failed is handed on: `HttpContext.Features.Get<IIyuAuthorizationFailureFeature>()`
carries its `Policy` and `EntitySet`. A host that words its own 401/403 should read it before the
endpoint's authorization metadata, which names `Orders`' policy — one this caller may already hold.

It is deliberately **fail-closed** on a query it cannot interpret, or that uses a construct it has
no rule for: such a request is answered `400` rather than passed along, because an expression this
check rejects and the query pipeline later accepts would be a way around it. It parses with the
route's own resolver and settings, so it reads an expression the way the query does — a navigation
named in another case (`$expand=customer`, `$filter=customer/name eq 'x'`) is checked like
`Customer`, not refused.

Expand **depth** is not this framework's setting: nothing here sets `MaxExpansionDepth`, so the
ceiling is the one `EnableQueryAttribute` applies. Measured against it, that ceiling is **2** — a
third hop is refused with `400` and a message naming the limit. Raise it on your own
`EnableQueryAttribute` or `ODataValidationSettings` if your app needs more.

Authorization does not rest on that number either way: the check walks an expand to the bottom, so
a set is authorized however deep it is reached, and raising the ceiling does not widen what a
caller can see without the policy.

**GraphQL — the policy is on the object type, not only on the root field.** The read policy
attaches to the query field *and* to the read type it returns. A field on some other type that
returns the protected type reaches the data without the root field's resolver being involved, so a
selection through such a field is refused at that field's own path rather than answering `null`. A
caller that holds the policy is unaffected either way.

**GraphQL navigations resolve.** Every query field projects its selection onto the database query
(HotChocolate projections): only the requested columns are read, and a navigation in the selection —
`{ orders { nodes { number customer { name } } } }` — is loaded by that same query. A row whose
reference is empty answers `null` for the navigation. Paging is applied around the projection, so a
page is still a slice of the key ordering. The policy on the navigation's type, above, is what keeps a
caller without it from receiving the rows that query loads.

For the same reason a read type may be exposed **once**. `AddEntityPair` throws if the type already
backs another query field: two fields are two doors to the same data, and a reader of the schema could
not tell which one the entity's policy guards. Expose it once — the exception names the field that
already holds the type.

> **When this starts to matter.** Both paths open the moment a read type carries a navigation
> property to another read type, whether you wrote it or a generator emitted it. Until then no
> selection can traverse between read types and there is nothing for either check to refuse. Set the
> policies before that property exists rather than after — an expand path is not visible in a
> response that succeeded.

**A decimal column's digits are an input contract.** `[DecimalDigits(precision, scale)]` (`Iyu.Core.Attributes` —
mdd-booster puts it on a model's `decimal(p,s)` field, as it puts `[StringLength]` on a `string(n)`) refuses a value with
more than `scale` decimal places or `precision − scale` digits before the point: the data API answers `400 InvalidBody`
with a `ValidationFailed` entry naming the field, where the database would otherwise round the extra places away (the
caller reads back a number it never sent) or fail the write. Trailing zeros are not digits (`1.2000` fits two places);
`null` is `[Required]`'s to judge.

**`$search` — a searchable reference is searched only for a caller who may read it.** Marking a reference
navigation `[Searchable]` (a single related read type, not a collection) makes `$search` match that type's searched
text too — a list shown with its reference's name is found by that name. One step: the related type's own
navigations are not followed, and a reference to a type no set serves is not searched. When the caller may not read
the referenced set, that reference is **left out** of the search rather than refusing it — the reference is the
server's declaration, so refusing would take search away from everyone outside that one set — and the response says
which references were left out in an `Iyu-Search-Excluded` header, so a client does not mistake a narrower search
for an empty result.

**The same search outside OData — `IyuSearch`.** An endpoint that starts from «the rows the list shows for this
search» — an export, a template download, a report, a bulk action — takes the search text the list sends and asks the
host's `IyuSearch` for the same selection, instead of writing a second search that drifts from the declarations:

```csharp
app.MapGet("/api/assets/export", async (string? search, ClaimsPrincipal user, IyuSearch iyuSearch, AppDbContext db) =>
{
    var filter = await iyuSearch.CreateFilterAsync<AssetExt>(search, user);   // ArgumentException if not valid $search
    var rows = await db.Set<AssetExt>().Where(filter.Predicate).ToListAsync();
    // filter.ExcludedNavigations: the references left out for this caller — what the list names in Iyu-Search-Excluded
    ...
});
```

The grammar is OData's `$search` (words, quoted phrases, `AND` / `OR` / `NOT`, juxtaposition as `AND`), the
properties and the reference step are the binder's, and a reference the caller may not read is left out exactly as
in the list. It filters; it does not authorize — access to the read type itself is the endpoint's to enforce.

### Checking that nothing was left unprotected

Attaching a policy per entity is one thing; knowing you attached it **everywhere** is another, and
the two look identical from outside. An entity set with no policy and one whose policy the caller
happens to satisfy both answer `200` — the difference only shows when someone stands up a caller
who *should* be refused. An entity declared with `options.Authorize` is protected on every surface
that serves it; the report is what catches the entity that was never declared at all — including one a
new registration added since the last time anyone looked.

`IAuthorizationSurfaceReport` answers it from the registrations themselves:

```csharp
var report = app.Services.GetRequiredService<IAuthorizationSurfaceReport>();

// Pin it as a contract test — new entities and new surfaces widen it automatically.
Assert.Empty(report.Unprotected);
```

`Entries` is every (surface, entity, operation) triple with the policy attached to it, where the
operation is read, write, or delete; `Unprotected` is the subset whose policy is `null`. Registered
automatically by `AddIyuMainServer`.

A delete row reports the policy that actually runs: a set with no `deletePolicy` shows its
`writePolicy` there, not `null`. The row exists even then, so the report answers "what protects
deletes on this set" rather than leaving the reader to infer it.

> 🔴 **`null` means "not attached through this framework", not "reachable by anyone."**
> The report sees what `options.Authorize` declared.
> A policy applied some other way — an `[Authorize]` attribute on a hand-written controller, an MVC
> convention over controller models, endpoint metadata, a gateway in front — is **invisible here**.
>
> So an app that authorizes through a controller convention will see *every* entry come back
> `null`. That is not a finding, and reading it as one leads somewhere worse than not looking:
> a report that cries wolf gets ignored. **To make this report mean something, attach policies
> where the framework can see them** — `options.Authorize`. Doing so also removes the
> parallel entity→policy map such a convention needs, since the registration becomes the one place
> that knows.

Two entries are deliberately *not* emitted, because a row that can never be given a policy would
sit in `Unprotected` forever and make the empty-list assertion unusable:

- a set whose `readOnlyVerbs` refuse every write verb has no write half, and one that withdraws
  `Delete` alone keeps its write row but has no delete half;
- GraphQL emits reads only — it records a `mutationPrefix` but generates no mutations yet.

### Rules on the generic write path

`AddEntityPair` opens generic writes (OData `PATCH`, GraphQL) over an entity. Guarding a field on
one of those entry points does not guard the others, and does not guard the next one added — so a
rule like *"this field cannot change once the order is billed"* has to sit where every entry point
converges, at save time.

Derive from `EntityWriteRule<T>` and register by scanning:

```csharp
public sealed class OrderVatTypeLock : EntityWriteRule<Order>
{
    protected override void Apply(WriteRuleContext<Order> ctx)
    {
        if (!ctx.IsUpdated || !ctx.IsModified(nameof(Order.VatType))) return;
        if (ctx.Entity.BilledDate is not null)
            throw new DomainRuleException("Cannot change VAT handling after invoicing.");
    }
}

builder.Services.AddIyuWriteRules(typeof(Program).Assembly);
```

The context carries what a rule needs without re-deriving it: `Entity`, `IsAdded` / `IsUpdated`,
`IsModified(name)`, `Original<T>(name)` / `Current<T>(name)`, and `Db` for rules that write a row
of their own (a change log added there is saved by the same `SaveChanges`).

| Kind of rule | Shape |
|---|---|
| Conditional lock / permission | check `IsUpdated` + `IsModified`, then **throw** — the exception propagates unchanged, so your own domain exception and its response mapping stay yours |
| Default, derivation, normalization | check `IsAdded` (or both) and assign to `ctx.Entity` |
| Change log | read `Original<T>` / `Current<T>`, `ctx.Db.Add(...)` the record |

Notes that matter in practice:

- **`IsModified` is always `true` on an insert** — every property of a new row is being written.
  A lock that should only guard edits must test `IsUpdated` first.
- **Deletes are not dispatched.** A rule about a field's value has nothing to say about a row on
  its way out.
- **Rules run in one pass and do not see entries created by other rules** — a log row one rule adds
  is not dispatched to the others. ⚠ That guarantee covers *entries*, not *values*: a field another
  rule **assigned** is read from the live entry, so a rule that runs later does see it as modified.
  Scanning promises no order, so a pair where one rule assigns a field and another keys off that
  field being modified is order-dependent and can flip on a recompile. Keep such a pair in one rule,
  or verify it in both orders — see the boundary section below.
- A rule on a base type covers derived entities.
- **Scanning is the point, not a convenience.** A hand-written registration line per rule fails by
  omission, and an omitted rule is indistinguishable at runtime from one whose condition never
  fired: nothing throws, nothing logs, the invariant is simply not enforced.

#### What belongs here, and what does not

A consumer who first meets this primitive tends to count every `SaveChanges` interceptor they own
and expect all of them to move. Most do; some should not, and the line is worth stating rather than
letting each app rediscover it.

| The rule touches | Belongs here | Why |
|---|---|---|
| **fields of the entity it is declared for** | ✅ | This is the primitive. Lock, permission check, default, derivation, normalization, and the change-log read all live here |
| **creating another entity** (`ctx.Db.Add(...)`) | ✅ | Supported, and the change-log shape above is exactly this — the new rows are saved by the same `SaveChanges` |
| **modifying another entity's fields** | ❌ | Recomputing a parent's state from the children in this save is an aggregate concern, not a field rule — it reads entries this rule cannot see and would make the one-pass, order-free guarantee a lie |
| **anything after the save** — a second `SaveChanges`, work that needs the generated keys | ❌ | `Apply` runs *before* the write. Post-save work is `SavedChanges`/`SavedChangesAsync` on an interceptor, which is also where a second save round belongs |

The two on the right are not gaps to be filled later; they are a different axis, and a plain
`ISaveChangesInterceptor` remains the right home for them. **Moving every rule is not the goal** —
moving the ones that are field rules is, and the wiring those shed (the `SavingChanges` /
`SavingChangesAsync` double override, the `ChangeTracker.Entries<T>()` walk, the null guard, and
the hand-written registration line) is the same for every one that moves.

> ⚠ **The one and only ordering hazard, stated plainly.** A rule that assigns to a field makes that
> field `IsModified`, and a rule dispatched **after** it on the same entry sees that — a rule
> dispatched before does not. So a pair like *"copy this value in"* + *"normalize it if it changed"*
> produces different results depending on which was registered first, and **assembly scanning does
> not promise an order**: the pair can flip on a recompile, silently, with nothing thrown and
> nothing logged.
>
> This is narrower than it sounds — it needs two rules on the same entity where one writes a field
> the other keys off. If you have that pair, the reliable fix is to make it **one rule** (assign,
> then normalize, in the order you wrote). If you keep it as two, pin it with a test that runs both
> registration orders rather than reasoning about it. Both directions of this behavior are pinned by
> a test in this repo, so a change to it will be a deliberate one.

### Making one property read-only

A domain field can genuinely need to change — just never through the generic write path.
A state machine's current-state field, say, where a dedicated action endpoint is what
should apply a transition (and log it), while a plain `PATCH` naming the field directly
would let a client skip that endpoint entirely. `Exclude<T>()` above is the wrong tool: it
closes reads too, and this field should stay fully queryable.

```csharp
options.ODataModel.ExcludeFromWrite<OrderExt>(x => x.Status);
```

Unlike `Exclude<T>()`, the property stays in the model — `$select`/`$filter`/`$orderby`
are unaffected — and instead picks up the standard `Org.OData.Core.V1.Computed` term on
`$metadata` ("server-supplied, do not send on insert/update"). A `POST` naming it anyway
is not rejected: the value is silently dropped from what the generic controller copies
onto the write entity, the same way `Id`/`CreatedAt`/`UpdatedAt` already are. A `PATCH`
naming it is dropped on the same terms *unless the whole request is unwritable* — see
[What a `PATCH` can store](#what-a-patch-can-store) below. A write straight to the
write-side `DbSet` — a dedicated endpoint reached through your own controller action, for
instance — is unaffected; it never goes through the generic controller's copy step at all.

**`$metadata` says what an update can store.** `Computed` is not only for properties you
mark: every property a generic write cannot store carries it — one the read type has and the
write type does not (what a view derives: a lookup, a rollup, a computed column), `CreatedAt`
and `UpdatedAt`, and a row version (`[Timestamp]`). The term is decided by the same rule that
answers `UnwritableProperty`, so a client reading the metadata to choose what to offer for
editing sees exactly what an update would refuse. The key is not marked — a create may supply it.

Same read-type rule as `Exclude<T>()`, for the same reason: request bodies bind to
`TRead`, so name that side. Also order-independent and callable after the fact from a
generated registration, exactly like `Exclude<T>()`.

### What a `PATCH` can store

A partial update names properties on `TRead`, and not all of them have somewhere to go:
a derived column the view computes has no counterpart on `TWrite`, `ExcludeFromWrite`
marks one deliberately, and `Id`/`CreatedAt`/`UpdatedAt` are the server's.

Those are **dropped in silence whenever the same request also carries something writable**.
That is what lets a client read an object and send it back whole, derived fields included,
and still have the one field it edited applied — the ordinary round-trip, and the reason
the drop exists at all.

When **every** property in the request is unwritable, the update is refused with `400` and
a body keyed by property:

```json
{ "error": { "details": [
  { "target": "ItemCount", "message": "The property is read-only: it is derived and has no stored counterpart." }
] } }
```

The distinction the caller gets from this is the one they cannot draw themselves: a value
refused because it is derived, a value refused because the model locks it, and a value
refused because the server owns it read the same from outside. Answering `204` to a
request that could not have stored anything reads as "accepted, and the field is
protected" — which is a different fact, and callers have acted on it.

Two boundaries are deliberate:

- **An empty body is still `204`.** Nothing was sent, so nothing was refused.
- **A property the model never declares never reaches this.** Deserialization refuses it
  first, and the answer is `UnknownProperty` naming it (below). Only a property `TRead` declares
  gets this far.

### Error responses

Every refusal the generic controller makes is an OData error, `{"error":{"code","message"}}`, with a
code from `ODataErrorCodes` — branch on the code, not on the message text:

| Status | `error.code` | When |
|---|---|---|
| `400` | `InvalidBody` | the body is missing or could not be read, or a value fails the model's rules — `details[]` name each property in `target` |
| `400` | `UnknownProperty` | the body names a property the set's type does not declare — `details[]` name each one in `target`, nested ones by path (`Survey.Grde`) |
| `400` | `UnwritableProperty` | every property a `PATCH` sent is one the write side does not accept |
| `400` | `InvalidQuery` | a query option cannot be applied — an unknown property, a malformed literal, a limit exceeded, a query this server cannot interpret or one that gives an option under both spellings (`$filter` and `filter`) |
| `400` | `SharedKeyRequired` | a set that shares its key was posted to without a key |
| `404` | `KeyNotFound` | `GET`, `PATCH` or `DELETE` by a key that names no row |
| `405` | `ReadOnlySet` | the set is registered read-only for this verb |
| `409` | `SharedKeyPrincipalMissing` | a shared-key row names a principal that does not exist |
| `409` | `SharedKeyRowExists` | the principal already has its one shared-key row |
| `412` | `PreconditionFailed` | a `PATCH` or `DELETE` sent `If-Match` naming a version the row no longer has (below) |

Each `details[]` entry carries its own `code` from `ODataErrorDetailCodes`. Where a refusal has one cause,
that is the refusal's code (`UnknownProperty`, `UnwritableProperty`, `SharedKeyRequired`). `InvalidBody`
has two, told apart by the entry: `Unconvertible` — the value could not be read as the property's type, and
the message is this framework's own sentence — and `ValidationFailed` — the value was read but a rule the
model declares refuses it, and the message is the model's. A client that shows refusals in its own language
replaces the first and keeps the second, without reading either text.

The two query-layer answers come from `IyuEnableQueryAttribute`, which the generic `Get` actions carry
in place of `[EnableQuery]`; a controller that overrides `Get` puts `[IyuEnableQuery]` on the override
to keep them. Authorization refusals (`401`/`403`) and the database errors `IyuWriteExceptionHandler`
answers are outside this table.

### Optimistic concurrency: `@odata.etag` and `If-Match`

A set whose read type has a concurrency property (`[ConcurrencyCheck]` or `[Timestamp]`) answers each
entity with `@odata.etag`, and a read by key also sends it as the `ETag` header. A `PATCH` or `DELETE`
that sends that value back as `If-Match` is applied only if the row is still that version; otherwise it
is refused with `412` `PreconditionFailed` and nothing is written — read the row again and decide.

- **Without `If-Match` the write is unconditional**, as it always was. `If-Match: *` only requires the
  row to exist.
- **A set with no concurrency property is unaffected** — it carries no ETag and ignores the header.
- If the write type maps the same property as an EF concurrency token (`IsRowVersion`, or
  `[ConcurrencyCheck]`), a change landing between the check and the save is caught by the save and
  answered with the same `412`.
- **The version travels in headers, never in the body.** A write type's concurrency token is the
  database's: a `POST` or `PATCH` body that carries it does not set it. Sent alongside other changes it is
  dropped; sent alone it is refused with `400` `UnwritableProperty`. (A `rowversion` column would refuse
  an explicit value anyway.) With distinct read and write types, give the token the same name on both —
  the ETag is built from the read type and the save check runs on the write type.
- **Either row-version shape works.** SQL Server's `[Timestamp] byte[]` and the `[Timestamp] uint` EF maps
  to PostgreSQL's `xmin` both round-trip: OData has no unsigned 32-bit type, so a `uint` version is published
  as a 64-bit integer, and `If-Match` is compared in the property's own type.
- **OData only.** The GraphQL endpoint carries no ETag — it emits reads only (see below), so writes, and
  with them `If-Match`, go through the OData set.

### The namespace the model is published under

`$metadata`, each payload's `@odata.type` and any type-cast segment name types under one EDM
namespace, `Default` — not the CLR namespace the types are declared in, which would publish how the
application organises its code to every caller.

```csharp
options.ODataModel.Namespace = "Shop";   // another name
options.ODataModel.Namespace = null;     // each type's CLR namespace (the behaviour before this setting existed)
```

Two exposed types with the same name cannot share a namespace; the model refuses to build and names
them.

### How many rows a response carries

A read of an entity set returns at most **1000 rows per response**, and a `$top` above **1000** is
refused with `400`. A result larger than a page is cut there, and the response carries
`@odata.nextLink` — the URL of the next page — so a client that wants the whole set follows links
until none is returned. `$count=true` still reports the full total. Nothing is lost, only split;
what the limit prevents is one request occupying the database, the server's memory and the
response with an entire table that grows without bound.

Both numbers are defaults on the model builder, and a set can replace them with its own:

```csharp
options.ODataModel.DefaultMaxTop   = 5000;   // null: no $top ceiling
options.ODataModel.DefaultPageSize = 500;    // null: one response holds every row

options.ODataModel.Page("Readings", maxTop: 200, pageSize: 200);   // a history table, narrower
options.ODataModel.Page("Units",    maxTop: null, pageSize: null); // a small list, always whole
```

The limits are carried on each set's read type as OData model-bound query settings, which is what
the query layer reads — so a set's page size also applies where its type is returned as a
collection inside another set's `$expand`.

**GraphQL query fields carry their own bounds.** Each field is a cursor connection: a request that
names no `first`/`last` gets **100** rows, and one asking for more than **1000** is refused. The two
surfaces name a collection differently — a set on one, a query field on the other — so each is
configured on its own builder, and setting a set's OData page does not change its GraphQL field:

```csharp
options.GraphQL.MaxPageSize     = 2000;
options.GraphQL.DefaultPageSize = 200;    // must not exceed MaxPageSize

options.GraphQL.Page("readings", maxPageSize: 200, defaultPageSize: 50);
options.GraphQL.Page("units",    maxPageSize: 500, defaultPageSize: 500); // a small list read whole
```

A connection always has a page, so neither value can be `null`; a small list is served whole by a
page at least as large as the list. HotChocolate's cost analysis applies on top of these bounds — a
large page of a wide selection can exceed the executor's maximum cost (1000 by default) and be
refused, so a field with a large page may also need `ModifyCostOptions`.

### Restricting write verbs

A set backed entirely by a read-only view, or an audit-trail entity only the system itself
should write, refuses some or all of POST/PATCH/DELETE:

```csharp
options.ODataModel.AddEntityPair<OrderSummaryExt, OrderSummary>(
    "OrderSummaries", ODataVerb.Post, ODataVerb.Patch, ODataVerb.Delete);
```

The restriction is advertised on `$metadata` via the standard OData Capabilities vocabulary
(`Org.OData.Capabilities.V1.InsertRestrictions`/`UpdateRestrictions`/`DeleteRestrictions`) and
enforced by the generic controller with `405 Method Not Allowed` — a client that reads the
metadata and one that skips it are rejected identically.

**When the registration is not yours to edit**, e.g. a single generated file that calls
`AddEntityPair(setName)` once per set with no per-call-site control, restrict the set
afterward instead of at registration:

```csharp
RegisterGeneratedEntities(options);   // registration you may not own

options.ODataModel.Restrict("DemoDataProvenances", ODataVerb.Post, ODataVerb.Patch, ODataVerb.Delete);
```

`Restrict` requires the set to already be registered — it throws if it is not — and reaches
`$metadata` and controller enforcement identically to declaring `readOnlyVerbs` at
`AddEntityPair` time, since both read the registry's live state rather than a value captured
at registration.

### A set that is only read

A pair restricted on every verb still needs a write type. A model with no table of its own — a view
that joins or aggregates other sets — has none to give, and it does not have the audit timestamps a
written entity carries either. Register it by its read type alone:

```csharp
public sealed class OrderSummary : IKeyed          // a key, and what the view returns
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = "";
    public int Lines { get; set; }
}

options.ODataModel.AddReadOnlySet<OrderSummary>("OrderSummaries");

public sealed class OrderSummariesController(AppDbContext db) : IyuODataReadOnlyController<OrderSummary>(db);
```

Map the type in your `DbContext` as you would any view (`ToView("order_summary")`, the key on `Id`).
Reading it is the same as reading a pair: query options, paging (`Page`), `$expand`, and the entity's
read policy (`options.Authorize<OrderSummary>(read: …)`). Writing is refused on every verb with the same
`405` `ReadOnlySet` a restricted pair answers, and `$metadata` says so twice — the set carries the three
Capabilities restrictions and every property but the key is `Computed`. The restriction cannot be lifted
with `Restrict` later, and `ExcludeFromWrite` or `DeclareSharedKey` on such a set is refused at startup:
there is no write type for any of them to mean something for.

`IKeyed` is the whole contract — a stable `Guid Id`. `IyuEntity` is `IKeyed` plus the timestamps the
runtime maintains on what it writes, so a read/write pair's types are unchanged.

On the GraphQL surface (`Iyu.MainServer.GraphQL`) the same model is
`options.GraphQL.AddReadOnlySet<OrderSummary>("orderSummaries")`: a query field like a pair's — a cursor
connection in key order, the selection projected, `Page` bounds, the entity's read policy — with no
mutation prefix, and `IsReadOnly("orderSummaries")` answers `true`. A navigation from another type to a
keyed model that no field serves is left out of the schema, as one to an unserved entity is.

### A set whose key is not its own

Some types hold optional extra facts about *one* row of another type rather than a collection of
them: a maintenance profile for an asset, a verification record for a document. The natural key
for such a row is the other row's key, and declaring that changes exactly one thing — who chooses
it.

```csharp
options.ODataModel.AddEntityPair<AssetExt, Asset>("Assets");
options.ODataModel.AddEntityPair<MaintenanceProfileExt, MaintenanceProfile>("MaintenanceProfiles");

options.ODataModel.DeclareSharedKey("MaintenanceProfiles", "Assets");
```

Reads are untouched — the pair is already expressible and `$expand` already reaches it. What the
declaration changes is the generic `POST`:

| Request | Without the declaration | With it |
|---|---|---|
| No key in the body | `201`, key invented by the server | **`400`** — the key is not the server's to choose |
| A key naming no `Assets` row | `201`, a row belonging to nothing | **`409`** |
| A key whose row already exists | whatever a key collision surfaces as | **`409`** — at most one row per principal |
| Everything else | unchanged | unchanged |

Three declarations are refused where they are written, because no request could make them true: a
set cannot share its key with itself, both sets must already be registered, and a principal that
already shares *its* key with a third set is rejected — a chain has no unambiguous owner of the key.

**This is a check, not a constraint.** Under a relational provider the foreign key enforces the
same relationship at save time regardless; what the check adds is a stated status and reason in
place of whatever a constraint violation would otherwise surface as. Both layers are wanted — a
check alone races, and a constraint alone explains nothing.

#### Choosing between this and an ordinary one-to-one

Both shapes give one row at most per target, so the question is not "how many" but **whose key it
is**:

| Ask | Shared key | Ordinary one-to-one |
|---|---|---|
| Can the row exist before the other one does? | No — it *is* that row's extra facts | Yes, if the foreign key is nullable |
| What happens when the other row is deleted? | It goes too | Whatever the foreign key says |
| Does the row have an identity callers quote? | No — quoting the principal's key names it | Yes, its own |
| Can it later point at a different target? | No — that would not be an edit, it would be a different row | Yes, by updating the key |

Getting this wrong is not a code change to undo: the key *is* the row's identity, so moving between
the two shapes afterwards is a schema migration. When both readings fit, the ordinary one-to-one is
the weaker claim and the safer default — a shared key can always be adopted later, while giving one
up means rekeying rows that already exist.

> A generator may produce such a pair from a model declaration rather than hand-written
> registration. That does not change anything above: what reaches this framework is the pair and
> the declaration, and the write contract is the same either way.

### Field descriptions

A read type property carrying `[Display(Description = "...")]` — the standard
`System.ComponentModel.DataAnnotations` attribute, and what a generator's `@help`-style
metadata typically becomes — surfaces automatically on both API surfaces, no extra call
needed: OData exposes it as the standard `Org.OData.Core.V1.Description` term on
`$metadata`, and GraphQL exposes it as the field's `description` in schema introspection.
A property without the attribute keeps no description on either surface.

Both calls work after the pair is registered, which is the point: a registration you cannot
edit — one produced by a tool, or shared across several hosts — can still be subtracted from.

## Integration testing (TestServer)

The generated OData controllers live in the server assembly, but MVC discovers
controllers by walking the **entry assembly**'s closure. In production the entry
assembly *is* the server, so discovery finds them. Under a test host the entry
assembly is `testhost`, whose closure does not include the server — so without
help every endpoint silently returns **404**.

`AddIyuMainServer` handles this automatically: it registers the `TContext`
assembly, the registration callback's declaring assembly, and the assembly
serving `$metadata` as application parts. The standard method-group form
therefore just works over `TestServer` — entity sets, `$metadata` and the
service document alike:

```csharp
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseTestServer();
builder.Services.AddIyuMainServer<AppDbContext>(
    configureDb: db => db.UseSqlite(conn),
    configure: ApiRegistration.RegisterGeneratedEntities); // method group → server assembly
```

If your controllers live in yet another assembly, or you pass the callback as a
**lambda wrapper** (whose declaring assembly is the caller, not the server), name
the controller-hosting assemblies explicitly — registration is deduplicated, so
this never double-registers:

```csharp
configure: options =>
{
    options.ControllerAssemblies.Add(typeof(SomeGeneratedController).Assembly);
    ApiRegistration.RegisterGeneratedEntities(options);
}
```

## File gateway (`Iyu.FileServer`)

A standalone host that moves bytes and nothing else. It holds no database: every
request carries an HMAC-SHA256-signed `FileAccessToken` minted by whoever owns
the attachment metadata, and the token names the storage key, so the gateway
cannot be redirected to another object.

```csharp
builder.Services.AddIyuFileGateway(
    gw =>
    {
        gw.SigningKey = builder.Configuration["Files:SigningKey"]!; // ≥32 bytes, shared with the minter
        gw.MaxBytes = 50L * 1024 * 1024;
        gw.AllowedContentTypes = ["application/pdf", "image/png"];  // empty = allow all
    },
    (FileSystemOptions fs) => fs.RootPath = "/var/attachments");     // or AzureBlobOptions

var app = builder.Build();
app.MapIyuFileGateway();   // PUT / GET / DELETE at gw.RoutePrefix (default "/files")
```

Behaviour worth knowing before deploying it:

- **`MaxBytes` is the authority on its own endpoint.** The upload handler aligns
  the server's per-request body limit to it, so the host's global default
  (30,000,000 bytes on Kestrel, HTTP.sys and IIS in-process) does not silently
  cap uploads below it. Ceilings the gateway cannot raise still apply and must be
  configured by the operator: IIS out-of-process (`maxAllowedContentLength`) and
  any reverse proxy.
- **An upload token writes once.** A token minted for `FileTokenOp.Upload` creates
  the object at its key; presented again while it is still valid — or for a key
  that already holds an object — it is refused with `409` and
  `{"error":"already_exists"}`, and what is stored is left as it was. What your
  application recorded when the upload finished (size, hash) therefore stays true
  of the object. An upload that was refused part-way stored nothing, so the same
  token can retry it; a client that lost the response to an upload that did
  succeed sees `409` on its retry, and can treat it as done. For a key whose
  content is meant to change — a template, an avatar — mint the token for
  `FileTokenOp.Replace` instead: it writes over whatever the key holds, and
  creates the object when there is none.
- **Rejections are structured**, so a caller can tell them apart:
  `413` with `{"error":"too_large"}`; `409` with `{"error":"already_exists"}`; `400` with
  `{"error":"content_type_not_allowed"}` or `{"error":"content_type_required"}`.
  An oversized body answers `413` whichever layer noticed it — the header check,
  the gateway's stream ceiling, or the host's guard.
- **`AllowedContentTypes` checks the type the token declares**, not the bytes
  that arrive. It enforces a policy the minter committed to; it does not sniff
  the payload. Matching compares `type/subtype` and ignores parameters, so
  `image/jpeg; charset=binary` satisfies an entry of `image/jpeg`; wildcards are
  not expanded. Once non-empty it fails closed — a token declaring no content
  type is rejected rather than waved through.
- **Downloads support `Range`**, so a large transfer can resume and a media
  client can seek. This is verified end-to-end against the filesystem backend;
  on Azure Blob it depends on the SDK's read stream reporting its length when
  opened, which is untested here (see Status). Uploads are a single request with
  **no resume**: a dropped connection restarts from zero, which is why the
  default limit is sized for document attachments rather than bulk media.
- **A missing object is 404, not 500.** Absence is a normal state — a key can be
  deleted while a still-valid token is in flight.
- **A write never damages the object it replaces.** The filesystem backend writes
  beside the object and moves the result into place, so a reader never sees a
  half-written file and an upload that fails part-way leaves what was stored there.

`IAttachmentStorage` beyond the gateway, for code that stores bytes itself:

- **`TryCreateAsync`** writes only if nothing is stored at the key and returns
  `false` otherwise, having written nothing — one step in the backend
  (`If-None-Match: *` on Blob; on disk, a move that does not overwrite on Windows
  and a hard link elsewhere), so of two concurrent writers exactly one wins. This
  is the write for an object that must not change once stored: a content-addressed
  key, or one another process may already be reading. Off Windows the filesystem
  backend therefore needs a storage root on a volume that supports hard links; on
  one that does not, the create fails and says so.
- **`OpenLocalFileAsync`** gives the object as a file path, for a tool that opens a
  path rather than a stream (a converter, a renderer, a scanner run as a process).
  The filesystem backend hands out the stored file itself, with no copy; any other
  backend gets a temporary copy by default. Dispose the result — a copy is deleted
  then, the stored file is not. Read the file; do not change it.
- Every rejection is logged under the category `Iyu.FileServer.FileGateway`
  (`FileGatewayExtensions.LogCategory`) so it can be filtered independently.
  Tokens are never logged. Successful transfers are not logged either — that is
  the host's request log.

## Chat (`Iyu.Server.Chat`)

A thin adapter over BareChat (`PackageReference Include="BareChat"`) that maps iyu's own claim
conventions onto BareChat's user context, gated behind one configuration flag:

```csharp
builder.Services.AddIyuChat(builder.Configuration);   // reads the "Chat" section

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseIyuChat();   // must come after host authentication — BareChat reads HttpContext.User
```

```jsonc
// appsettings.json
"Chat": {
  "Enabled": true
  // every other BareChatOptions field is valid here too — the section binds straight into it
}
```

**`Chat:Enabled` is the only flag this adapter adds.** `false` or a missing section makes both
`AddIyuChat` and `UseIyuChat` complete no-ops — no services registered, no routes mounted. When
`true`, the rest of the `Chat` section binds directly into `BareChatOptions`, so every option
BareChat itself exposes is reachable here without this package re-declaring it.

**Display names, not login IDs.** iyu's claim convention puts the login ID in `ClaimTypes.Name`
and the human-readable name in `ClaimTypes.GivenName`; BareChat's default provider shows
`Identity.Name` (the login ID) as the chat display name. `IyuChatAuthProvider` replaces that
default to prefer `GivenName`, falling back to the login ID only when no given-name claim is
present. The user identifier stays `ClaimTypes.NameIdentifier`, BareChat's own standard.

For everything past these two calls — message model, moderation, storage — see BareChat's own
documentation; this package only adapts identity and toggles the feature on.

## Scheduled reports (`Iyu.VaultAi`)

A hosted scheduler plus embedded viewer SPA that generates Markdown reports via an LLM agent
and serves them at a mounted route. Registration is conditional: both
`AddVaultAiReports(configuration)` and `UseVaultAiReports()` no-op (the feature is entirely
absent — no hosted service, no routes) when the `VaultAi:Url` configuration key is blank, so
leaving the section out of `appsettings.json` is how a consumer opts out.

```csharp
builder.Services.AddVaultAiReports(builder.Configuration);   // reads the "VaultAi" section

var app = builder.Build();
app.UseVaultAiReports();   // API + SPA at VaultAiSettings.BasePath (default "/vault-ai-reports")
```

```jsonc
// appsettings.json
"VaultAi": {
  "Url": "https://vault-ai.internal",
  "Token": "...",
  "ReportAgentId": "...",
  "ReportPath": "reports"   // see below
}
```

**`ReportPath` accepts an absolute path, and that is the supported way to keep report history
across deployments.** When it is relative (the default, `"reports"`), both the scheduler and
the API middleware resolve it under `IWebHostEnvironment.ContentRootPath` — the app's own
folder. A deployment pattern that replaces that folder on every release (a zero-downtime
release-folder swap, or an Azure App Service slot swap) therefore loses everything written
there, because the runtime-generated `reports/*/output` and `reports/*/logs` are not part of
the publish payload and the old folder is discarded. Setting `ReportPath` to an absolute path
outside the deploy lifecycle (e.g. `"C:/data/vault-ai-reports"`) sidesteps this entirely —
`Path.IsPathRooted` is checked before combining with `ContentRootPath`, so a rooted value is
used exactly as given, in both `ReportSchedulerService` and the middleware's request handling.

Report folders are content-addressed by name (`{ReportPath}/{slug}/info.json` +
`prompt.md`, with `output/*.md` and `logs/*.log` populated at runtime) and are not created by
this package — a consumer seeds them ahead of the first scheduled run.

## Document templates (`Iyu.Report`)

A thin DI wrapper around [DocuChef](https://github.com/iyulab/DocuChef)'s
`Chef`/`IRecipe`/`IDish` API — nothing more. It holds no template storage and maps no
endpoints; the consumer loads a template, binds data, and saves the result:

```csharp
builder.Services.AddIyuReport();   // registers DocuChef's Chef as a scoped service

// wherever the report is generated:
using var recipe = chef.LoadExcelTemplate(templateStream);
recipe.AddVariable("Title", "Shipment Slip");
recipe.AddVariable("Items", items);   // any bindable value — collections included

using var dish = recipe.CookDish();
dish.SaveAs(outputStream);
```

**Not the same thing as `Iyu.VaultAi`'s "scheduled reports"** — `Iyu.VaultAi` schedules and
generates its own report content; `Iyu.Report` fills an Office document template (Excel,
for now) with data the caller supplies, on demand. Independent modules, independent
dependency footprint (`Iyu.Report` has no `Iyu.Core` reference and no
`Microsoft.AspNetCore.App` framework reference), no shared code.

**Non-goals: no template registry, no generation-history tracking, no endpoint mapping.**
Deciding which templates exist and where they live, recording who generated what output and
when, and exposing report generation over HTTP are all consumer-application concerns —
`Iyu.Report` only turns a template stream plus data into an output stream, and stops there by
design, not by omission.

The parts a registry and a history are made of are already here. A template is a file —
`Iyu.FileServer` stores it, and an admin replacing it is an upload with a `FileTokenOp.Replace` token. Which template a document
uses is a row of your own model (document code → attachment, active flag, version), and so is
each generated output (who, when, which template, the output's attachment) — declared like any
other entity, it gets the same read/write API and policies as the rest of your data. Rendering is
the glue between them: open the active template's stream, load it as a recipe and `CookDish()` it with
your data (above), save the result as an attachment, insert the history row.

For everything past `AddIyuReport()` — template syntax, binding rules, supported formats —
see [DocuChef's own documentation](https://github.com/iyulab/DocuChef); this package
does not wrap or re-document that surface.

## Document conversion (`Iyu.DocConvert`)

`IDocumentConverter.ConvertToPdfAsync(source, sourceContentType)` — converts an Office or
OpenDocument file to PDF. One built-in implementation, backed by
[Gotenberg](https://gotenberg.dev) (a self-hosted, MIT-licensed HTTP wrapper around
LibreOffice — not a commercial dependency):

```csharp
builder.Services.AddIyuDocConvert(o => o.BaseUrl = "http://localhost:3000");

// wherever a PDF is needed:
using var pdf = await converter.ConvertToPdfAsync(
    docxStream, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
```

Requires a running Gotenberg instance — `docker run --rm -p 3000:3000 gotenberg/gotenberg:8`
is the whole setup; this fits the on-prem-first deployment `AddIyuMainServer` already assumes
(the same pattern as pointing `UseSqlServer` at a connection string). `sourceContentType` must
be one of the MIME types `GotenbergDocumentConverter` maps to a LibreOffice-readable extension
(`.docx`/`.xlsx`/`.pptx`, legacy `.doc`/`.xls`/`.ppt`, OpenDocument `.odt`/`.ods`/`.odp`,
`.csv`/`.txt`/`.rtf`) — an unrecognized type throws `NotSupportedException` rather than
guessing. `source` is read but not disposed; the caller keeps ownership, same convention as
`IAttachmentStorage.SaveAsync`.

**A failure is one of four things, whichever backend is behind the interface:**

| Thrown | It means | Retry? |
|---|---|---|
| `NotSupportedException` | the content type is not one this converter handles — nothing was sent | no |
| `DocumentConverterUnavailableException` | the converter could not be reached, did not answer within `GotenbergOptions.Timeout`, or answered `502`, `503`, `504`, `408` or `429` | yes — nothing is wrong with the document |
| `DocumentConversionFailedException` | the converter received the document and could not convert it (any other status — a damaged file; Gotenberg answers `500` for a document LibreOffice cannot convert) | no |
| `OperationCanceledException` | your own `CancellationToken` was cancelled | — |

So an endpoint that serves a converted document can answer "conversion is unavailable" (a `502` or `503`)
for the second, a client error for the third, and leave everything else to its global handler — without
knowing the converter speaks HTTP. The transport's own exception is the `InnerException`. A timeout is
reported as unavailable, not as cancellation, although the HTTP client raises the same type for both.

**Not part of `Iyu.Report`.** `Iyu.Report` fills a template with data; `Iyu.DocConvert` takes
an already-produced Office file and renders it to PDF — the two compose (bind a template, then
convert the result) but neither depends on the other. `Iyu.DocConvert` has no `Iyu.Core`
reference and no `Microsoft.AspNetCore.App` framework reference, same independence as
`Iyu.Report`.

## Upgrading

Per-release changes — including every breaking change and the packages each release
actually touched — are in
[CHANGELOG.md](https://github.com/iyulab/iyu-framework-v5/blob/main/CHANGELOG.md), a copy
of which ships inside every package.

All twelve `Iyu.*` packages share one version, so a new number does not by itself mean the
code you depend on moved. Each release entry opens with **Packages affected**; if yours is
not listed, the upgrade is a version bump and nothing else. When skipping releases, read
every entry between your current version and the target — each one states its own breaking
changes only.

## Build & test

```bash
dotnet build IyuFramework.slnx
dotnet test  IyuFramework.slnx
```

All warnings are treated as errors across every project in the solution.

## Status

Version **0.38.0**. Unit and integration tests run against every project on each
build, and warnings are errors. The OData/GraphQL runtime, identity, attachments, chat, and
scheduled-report modules are all in place and consumed in production.

Known gaps, in rough priority order:

- `Iyu.Report` is new and validated against an anonymized template fixture covering the
  structural complexity DocuChef exposes (merged-cell headers, variable-row tables,
  free-text blocks, multi-sheet duplication) — it has not yet been validated against a
  production template.
- `Iyu.DocConvert` is new. `GotenbergDocumentConverter` is covered by unit tests against a
  stubbed HTTP handler (request shape, content-type mapping, error surfacing); it has not yet
  been round-tripped against a live Gotenberg instance in this repo's own test suite.
- The Azure Blob storage backend has no automated coverage — the test suite uses
  a fake and the local filesystem backend. Backend-specific failure modes are
  therefore not caught here.
- Resumable (chunked) upload is **not** supported. A GB-scale upload that drops
  restarts from zero. Adding it means a resumable protocol plus session state,
  which the gateway deliberately does not have today.
- The gateway has no request-timeout or minimum-data-rate policy of its own, so
  a slow but legitimate upload is subject to the host's slow-POST defences
  (Kestrel's `MinRequestBodyDataRate`, 240 bytes/second by default).
