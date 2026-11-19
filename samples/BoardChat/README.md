# Board chat sample

A host that serves dummy sales data over OData, keeps the boards people build from it, and lets an assistant — any OpenAI-compatible
model — build them from a conversation. It is thin on purpose: the data model is `model/tables.m3l.md`, the entities, context and
controllers are generated from it (`Entities_gen`, `Server_gen`), and the routes come from libraries — `Iyu.MainServer` (OData at
`/$data`), `Iyu.UBoard` (boards at `/uboard/boards`, the source catalog at `/uboard/catalog`). `Program.cs` only switches those on.
The one route the sample writes is the assistant's, `/assistant/turn` (`Assistant.cs`): the loop and the model service behind it
are [IronHive.Agent](https://github.com/iyulab/ironhive-agent) and [IronProw](https://github.com/iyulab/iron-prow), and the page's
tools — the ones that check, save and read as the signed-in person — run in the page.

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

The assistant's model is named in configuration; without it the host still runs and the assistant's route is simply not there:

```
Model__BaseUrl=https://your-service.example/v1   # any OpenAI-compatible service
Model__ApiKey=...
Model__Name=the-model-to-use
Model__MaxOutputTokens=4096                      # optional: the most a reply may take
```

The host asks the model not to think before it answers (IronHive's `ReasoningEffort.None`, which it turns into the switches a
service understands): a model that reasons first spends the reply's tokens on it, and a long think can pass the time a gateway in
front of the service will wait.

The key, the model and the limits are the host's: a request from the page carries the conversation, the rules text and the page's
tools, and nothing else. The route turns away another site, anything that is not JSON, a body over 1 MiB and a tool that is not a
plain name.

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
