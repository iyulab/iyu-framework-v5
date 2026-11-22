# Board chat sample

A host that serves dummy sales data over OData, keeps the boards people build from it, and lets an assistant — any OpenAI-compatible
model — build them from a conversation. It is thin on purpose: the data model is `model/tables.m3l.md`, the entities, context and
controllers are generated from it (`Entities_gen`, `Server_gen`), and everything else comes from libraries — `Iyu.MainServer` (OData at
`/$data`), `Iyu.UBoard` (boards at `/uboard/boards`, the source catalog at `/uboard/catalog`, one assistant turn at
`/uboard/boards/{id}/turn`, and the **screen** that manages them at `/uboard/ui`). `Program.cs` only switches those on: the sample writes no
route and no page of its own. The assistant is `Iyu.UBoard.IronHive`: the loop and the model service behind it are
[IronHive.Agent](https://github.com/iyulab/ironhive-agent) and [IronProw](https://github.com/iyulab/iron-prow), and the tools that check,
save and read as the signed-in person run in the screen the library serves.

**This is a sample, not a template for production.** The sign-in is a stand-in: a request is made by the user named in the
`X-Sample-User` header, or by `analyst` when there is none. `finance` may read the protected columns (an order's `Margin`, a customer's
`Email`); `analyst` may not, and the assistant is told so. Who may make and change boards is `SampleBoardAccess` in `SampleUsers.cs`:
`analyst` and `finance` may; anyone else signed in (say, `viewer`) may open a board and ask the assistant about it, and nothing more. A `partner` may open only the boards
whose id starts with `shared-`, and the board list holds only those.

## Run it

You need .NET 10. The library builds its screen with Node 22.12 or later (a build without Node still runs; the screen's routes then say
so with a 503).

```
dotnet run --urls http://127.0.0.1:5188
```

and open `http://127.0.0.1:5188/uboard/ui`: the boards the signed-in person may open, and a new-board button when they may make one. A
browser sends no `X-Sample-User` header, so it is the analyst; to be someone else, add the header with a browser extension.

The database is `boardchat.db` next to the build output, or the file named by `--Database <path>`; it is filled with dummy orders
(about 640 over twelve months, five regions, twelve products, sixty customers) the first time it is created. Delete the file to start
over.

## The model

The assistant's model is named in configuration, which `Program.cs` reads and hands to `AddIyuUBoardIronHive` (the library reads none
itself); without it the host still runs, the screen has no chat, the turn route is not there, and `GET /uboard/access` says
`assistant: false`:

```
Model__BaseUrl=https://your-service.example/v1   # any OpenAI-compatible service
Model__ApiKey=...
Model__Name=the-model-to-use
Model__MaxOutputTokens=4096                      # optional: the most a reply may take
```

The host asks the model not to think before it answers (IronHive's `ReasoningEffort.None`, which it turns into the switches a
service understands): a model that reasons first spends the reply's tokens on it, and a long think can pass the time a gateway in
front of the service will wait.

The key, the model and the limits are the host's: a request from the screen carries the conversation, the rules text and the screen's
tools (each saying whether it edits), and nothing else. The route turns away another site, anything that is not JSON, a body over
1 MiB and a tool that is not a plain name; a board the person may not view is not there, and a person who may view a board but not
edit it is answered by an assistant that is offered none of the screen's editing tools.

## The browser run

`src/Iyu.UBoard/web/e2e/run.mjs` drives the screen in a real browser against this host, with a scripted model service standing in for the
model: it builds the sample, starts it, and walks a board from nothing to published, then a reader, a person who may only read, and the
page's Content-Security-Policy (a violation fails it). It needs Chrome, Chromium or Edge (`CHROME_PATH` names one) and prints
`E2E-OK steps=N`.

## What is generated, and how

`model/mdd.json` and `model/tables.m3l.md` are the input of [mdd-booster](https://github.com/iyulab/mdd-booster):

```
mdd build model
```

writes the two `_gen` folders. An order's read type is a view over the names it points to (region, customer, product). The generated
SQL creates it for a real database; this sample uses SQLite, whose `EnsureCreated` makes tables only, so `SampleData.cs` creates that
one view itself.

The board tables come from the library (`UseIyuBoardTables()` on the context's options), so the generated context needs no board
code.
