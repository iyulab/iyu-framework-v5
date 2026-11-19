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
- A model proxy for an embedded assistant: `AddIyuUBoard(o => o.Model(m => { m.BaseUrl = …; m.ApiKey = …; m.ModelName = …; }))` and
  `app.MapIyuUBoard()` serve `POST /uboard/model/chat/completions`, which sends an OpenAI-style chat-completions request (any service
  that speaks that wire: a hosted one, a gateway, a local model) with the server's key and the configured model, and returns the reply
  as it came, a stream as a stream. The page cannot choose the model or the key, only the fields an assistant needs pass, and the
  reply's length is closed: a `max_tokens` or `max_completion_tokens` above `MaxOutputTokens` is lowered to it, a value that is not a whole
  number above zero is refused with 400, and a request that names none is given the limit in `OutputLimitField` (`max_tokens` unless the
  host says `max_completion_tokens`). A service that refuses answers 502 without its words, a rate limit 429, a slow
  service 504. The route requires a signed-in caller (or the policy named in `IyuUBoardOptions.Policy`), JSON, a request within
  `MaxRequestBytes`, and the same origin. A host that turns the model on without a key, an address or a model does not start; the
  message names what is missing, never a value. The package now references the ASP.NET Core shared framework for the route.
- Board routes: with `Boards()` on, `app.MapIyuUBoard()` also serves the board store under `{prefix}/boards` — make (`POST {id}?name=`, 201 or
  409 with the board that exists), read, save by revision (`PUT {id}?expected=`, 409 with the board as it now stands), rename, delete, the
  revision list and each revision, publish, withdraw, the publication history, the published revision (204 while nothing is published),
  the fallback list, and a paged board list. Bodies are the board as `{ id, name, revision, publishedRevision, document }`. The routes
  need a signed-in caller (or the policy in `IyuUBoardOptions.Policy`) and a same-origin request; a body must be JSON, a value the service
  refuses is 400, a document over 4 MiB is 413, and the history names the caller by name, else by the `sub` claim.
- Ready-made board tables in `Iyu.UBoard.EntityFrameworkCore`: `IyuBoardRow`, `IyuBoardRevisionRow` and `IyuBoardPublicationRow`
  (tables `IyuBoards`, `IyuBoardRevisions`, `IyuBoardPublications`), `ModelBuilder.ApplyIyuBoardTables()` for a context you write,
  `DbContextOptionsBuilder.UseIyuBoardTables()` to add them to a context that is generated and not edited, and
  `AddIyuUBoard<TContext>()` over them, so a host needs no row types of its own. The adapter now references
  `Microsoft.EntityFrameworkCore.Relational` (the mapping names tables).
