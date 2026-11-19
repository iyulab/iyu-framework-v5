# Board embedding sample

A host that keeps boards for an embedded board viewer, built on `Iyu.UBoard` and `Iyu.UBoard.EntityFrameworkCore`, with a page that
draws the boards with `@iyulab/u-board` and edits them through the tools of `@iyulab/u-board-mcp` — no model involved: the page calls
`patch_board` itself, the way a model's tool call would.

**This is a sample, not a template for production.** There is no sign-in: every request is made by one fixed development user, and
nothing here decides who may read or change a board. Put your own authentication and your own rule in front of the routes.

## What it shows

- **One host, two sessions.** Sessions A and B each hold a store of the board. A saves; B, still on the older revision, is refused
  by the host and its store puts the board back as it was; B reloads and saves.
- **Revisions and publication.** Every save is a new revision. Publishing points the board at a revision; publishing an older one
  is the rollback. What readers see is the published revision.
- **A fallback.** Something that skips the viewer's check saves a document the viewer refuses, and it gets published. The reader
  checks what it was given, and falls back through the revisions published before it to the newest one it accepts.

## The seam, and what the page has to carry

A viewer's document store calls `save(document)` with the document only. The host wants to know which revision the save worked from,
so `web/src/host.js` carries it: it starts at the revision the page loaded, goes to the host as `?expected=`, and moves when the host
accepts. When the host answers `409`, `save` rejects and the store undoes the change. The store counts one revision per change and the
host counts one per accepted save, so they stay equal as long as every change is exactly one save.

The routes in `Program.cs` are the whole of the host's side: create, read, save on a revision, list revisions, publish, withdraw,
read what is published, and list what was published before it. The tables are in `BoardTables.cs`.

## Run it

You need .NET 10 and Node 22.12 or later.

```
cd web
npm install
npm run build          # writes the page to ../wwwroot (not committed)
cd ..
dotnet run --urls http://127.0.0.1:5088
```

Open the address, press **Create it**, and use the buttons. The database is `boards.db` next to the build output, or the file
named by `--Database <path>`; delete it to start over.

The npm packages are pinned to exact versions on purpose: the board language is below 1.0, and a minor release of it may change what a
board may contain.
