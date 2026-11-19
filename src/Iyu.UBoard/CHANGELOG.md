# Changelog — Iyu.UBoard

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package follows
[Semantic Versioning](https://semver.org/) — below 1.0, a minor release may contain breaking changes.

## [Unreleased]

### Added

- Board storage for a host that embeds a board viewer and editor: `AddIyuUBoard(o => o.Boards())` registers `IyuBoardService` over the
  host's `IBoardStore`. A board is an opaque JSON document with an append-only revision history; a save names the revision it worked
  from and a save made on a revision the board has moved past changes nothing and answers `Conflict` with the board as it stands. A
  revision is published by pointing the board at it, every publication and withdrawal adds a line to a history that only grows, and
  `FindLastAcceptedPublicationAsync` walks that history for the newest earlier publication a viewer accepts, since a lower revision
  number is not necessarily one that was ever published. `ListEarlierPublicationsAsync` gives that same list with the documents, for a
  host whose check of a document runs in the browser and not on the server. Rolling a publication back is publishing an older revision.
- The layer is explicit: nothing is registered until `AddIyuUBoard` is called, a call that names no part does not start, and `Boards()`
  without an `IBoardStore` does not start. The message names what is missing.
- The package depends on neither Entity Framework Core nor `Iyu.Core`; `Iyu.UBoard.EntityFrameworkCore` keeps the three tables in the
  application's own `DbContext`.
- The package references the ASP.NET Core shared framework for its routes.
- Board routes: with `Boards()` on, `app.MapIyuUBoard()` also serves the board store under `{prefix}/boards` — make (`POST {id}?name=`, 201 or
  409 with the board that exists), read, save by revision (`PUT {id}?expected=`, 409 with the board as it now stands), rename, delete, the
  revision list and each revision, publish, withdraw, the publication history, the published revision (204 while nothing is published),
  the fallback list, and a paged board list. Bodies are the board as `{ id, name, revision, publishedRevision, document }`. The routes
  need a signed-in caller (or the policy in `IyuUBoardOptions.Policy`) and a same-origin request; a body must be JSON, a value the service
  refuses is 400, a document over 4 MiB is 413, and the history names the caller by name, else by the `sub` claim.
- Who may do what to which board is the application's to say: `IBoardAccess` answers, per person, whether they may view, make, edit, publish or delete a board (`BoardAction`), and is told when a board was just made (`CreatedAsync`) so the application records its owner in its own permission model. Every board route asks it and the answer is enforced there: a board the caller may not view is 404 on every route (it looks like a board that does not exist), a thing they may not do to a board they can view is 403 and changes nothing, a list holds only the boards the caller may view, and a board's answer carries `can: { edit, publish, delete }` so a screen can show it read-only. `GET {prefix}/access` answers `{ create }` for a screen that offers a new-board button. The layer fails closed: `Boards()` without a registered `IBoardAccess` does not start (the message names it); a host with no board-level rules registers `EveryoneSignedInBoardAccess` by name. There is no access store and no sharing screen.
- Ready-made board tables in `Iyu.UBoard.EntityFrameworkCore`: `IyuBoardRow`, `IyuBoardRevisionRow` and `IyuBoardPublicationRow`
  (tables `IyuBoards`, `IyuBoardRevisions`, `IyuBoardPublications`), `ModelBuilder.ApplyIyuBoardTables()` for a context you write,
  `DbContextOptionsBuilder.UseIyuBoardTables()` to add them to a context that is generated and not edited, and
  `AddIyuUBoard<TContext>()` over them, so a host needs no row types of its own. The adapter now references
  `Microsoft.EntityFrameworkCore.Relational` (the mapping names tables).
- A source catalog: `AddIyuUBoard(o => o.Catalog())` adds `GET {prefix}/catalog`, the entities, properties (name, type, nullable, readable,
  and the members of an enum) and links a board can be built from, as the signed-in caller may read them. The port is `IBoardSourceCatalog`,
  which a host implements for the way it serves its data; a host that does not register one does not start. Without `showUnreadable` an
  entity or property the caller may not read is not listed; with it, it is listed and marked not readable, and nothing names the policy.
- `Iyu.UBoard.MainServer`, an adapter that is not packed yet: `AddIyuUBoardMainServerCatalog()` implements the catalog over the OData
  model of `Iyu.MainServer` and the read policies declared with `Authorize` and `AuthorizeProperty` (or the host's fallback policy), so the
  catalog agrees with what the OData surface would let that caller read.
