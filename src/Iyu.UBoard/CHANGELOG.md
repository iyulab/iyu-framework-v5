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
