# Board chat sample

A host that serves dummy sales data over OData, keeps the boards people build from it, and lets an assistant — any OpenAI-compatible
model — build them from a conversation. It is thin on purpose: the data model is `model/tables.m3l.md`, the entities, context and
controllers are generated from it (`Entities_gen`, `Server_gen`), and every route comes from a library — `Iyu.MainServer` (OData at
`/$data`), `Iyu.UBoard` (boards at `/uboard/boards`, the model proxy at `/uboard/model/chat/completions`, the source catalog at
`/uboard/catalog`). `Program.cs` only switches those on; no route is mapped by hand.

**This is a sample, not a template for production.** The sign-in is a stand-in: a request is made by the user named in the
`X-Sample-User` header, or by `analyst` when there is none. `finance` may read the protected columns (an order's `Margin`, a customer's
`Email`); `analyst` may not, and the catalog shows those columns as locked.

## Run it

You need .NET 10. The page is built with Node 22.12 or later.

```
cd web
npm install
npm run build          # writes the page to ../wwwroot (not committed)
cd ..
dotnet run --urls http://127.0.0.1:5188
```

The database is `boardchat.db` next to the build output, or the file named by `--Database <path>`; it is filled with dummy orders
(about 640 over twelve months, five regions, twelve products, sixty customers) the first time it is created. Delete the file to start
over.

## The model

The assistant's model is named in configuration; without it the host still runs and the model route is simply not there:

```
Model__BaseUrl=https://your-service.example/v1   # the part before /chat/completions
Model__ApiKey=...
Model__Name=the-model-to-use
```

Two more settings matter for a model that **reasons before it answers**. It spends the reply's tokens thinking first, so a board
can run out of room before any answer arrives, and a long think can pass the time a gateway in front of the service will wait (a
`524` or `504`, which the host reports as `502`). Give it room, and turn the thinking off where the service has a switch for it:

```
Model__MaxOutputTokens=8192                                                        # default 4096
Model__ExtraBody={"chat_template_kwargs":{"enable_thinking":false}}                # fields the host adds to every request
```

`ExtraBody` is the host's, not the page's: the page cannot send those fields and cannot change them.

The key stays on the server: the page talks to `/uboard/model/chat/completions`, which sends the request on with the server's key
and model.

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
