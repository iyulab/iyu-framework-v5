using BoardChat;
using BoardChat.Entities;
using BoardChat.Server;
using Iyu.UBoard;
using Iyu.UBoard.EntityFrameworkCore;
using Iyu.UBoard.MainServer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

// A host that serves dummy sales over OData and lets people build boards from them by talking to a model. Everything below is
// switched on, not written: the entities are generated from model/tables.m3l.md, the routes are the libraries'.
var builder = WebApplication.CreateBuilder(args);
var database = builder.Configuration["Database"] ?? Path.Combine(AppContext.BaseDirectory, "boardchat.db");

builder.Services.AddAuthentication("Sample").AddScheme<AuthenticationSchemeOptions, SampleUserHandler>("Sample", null);
builder.Services.AddAuthorization(o => o.AddPolicy("finance", p => p.RequireRole("finance")));
builder.Services.AddIyuMainServer<SalesDb>(
    db => db.UseSqlite($"Data Source={database}").UseIyuBoardTables(),
    options =>
    {
        ApiRegistration.RegisterGeneratedEntities(options);
        options.AuthorizeProperty<OrderExt>(x => x.Margin, "finance");
        options.AuthorizeProperty<CustomerExt>(x => x.Email, "finance");
    });
builder.Services.AddIyuUBoard<SalesDb>(boards =>
{
    boards.Catalog(showUnreadable: true);
    // The assistant's model is any OpenAI-compatible service, named in configuration (Model__BaseUrl, Model__ApiKey, Model__Name; see the
    // README for Model__MaxOutputTokens and Model__ExtraBody, which a model that reasons before it answers needs).
    var model = builder.Configuration.GetSection("Model");
    if (model["BaseUrl"] is { Length: > 0 } baseUrl)
        boards.Model(m =>
        {
            (m.BaseUrl, m.ApiKey, m.ModelName) = (new Uri(baseUrl), model["ApiKey"], model["Name"]);
            m.MaxOutputTokens = model.GetValue("MaxOutputTokens", m.MaxOutputTokens);
            if (model["ExtraBody"] is { Length: > 0 } extra) m.ExtraBody = JsonNode.Parse(extra)!.AsObject();
        });
});
builder.Services.AddIyuUBoardMainServerCatalog();

var app = builder.Build();
SampleData.Create(app.Services);
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseIyuMainServer();
app.UseAuthorization();
app.MapIyuUBoard();
app.Run();
