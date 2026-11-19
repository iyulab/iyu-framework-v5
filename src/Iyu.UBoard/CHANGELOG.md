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
- An assistant for a board's chat, behind a port: `IBoardAssistant` takes one turn of a conversation about the open board — the conversation so far, the board as stored, and the tools the caller can run — and answers with the tools to run or the assistant's words. The package depends on no model library: the model, its key and the loop that talks to it are an implementation's to provide. A turn is state-free (the history an answer returns is handed back, unread, with the next request) and the tools run where the caller is, as the person, so one question can take several turns. With an implementation registered, `MapIyuUBoard` also serves `POST {prefix}/boards/{id}/turn`; with none the route is not mapped.
- Every turn is asked of `IBoardAccess` before the assistant hears of it. A caller who may not view the board gets 404 whatever they sent; a caller who may view but not edit gets an answer-only turn: a tool the screen did not declare as only reading (`edits: false` on its declaration) is withheld from the assistant, and the answer says `answerOnly: true`. The declaration is the screen's own word, so this keeps the assistant from being offered an edit the person may not make and does not itself stop one — saving a revision is still refused with 403. A board that has not been saved yet is an empty one that its first save would make, so whoever may make boards may start it. A request must be JSON, same-origin and at most 1 MiB (counted as it is read), name at most 32 tools in plain words, and be either a message or the results of the tools the last answer asked for; anything else is 4xx and never reaches the assistant. A failure is answered with its kind only (`BoardAssistantException.Kind`; 429 for `ratelimit`, 502 otherwise) and what the service said stays in the host's log.
- `GET {prefix}/access` now answers `{ create, assistant }`: `assistant` is whether an `IBoardAssistant` is registered, so a screen leaves its chat out when there is none.
- `Iyu.UBoard.IronHive`, an adapter that is not packed yet: `AddIyuUBoardIronHive(o => { o.BaseUrl = …; o.ApiKey = …; o.Model = … })` registers an `IBoardAssistant` that runs `IronHive.Agent` over a model service reached through IronProw — any OpenAI-compatible service. The address, key, model and the most a reply may take are the host's alone and are set only there (the adapter reads neither the environment nor configuration on its own); a host that names no address or model does not start, and the message names what is missing, never a value. The assistant is asked not to think before it answers, a failure reaches the caller as its kind only (`ratelimit`, `timeout`, `network`, `authentication`, `contextlimit`, `guard`) and cancelling the request ends the turn. The model service is built on a provider of the adapter's own, so it is not the host's `IChatClient`.
