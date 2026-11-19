using Iyu.UBoard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Iyu.Tests.UBoard;

/// <summary>
/// The layer is explicit and fails closed: nothing is registered until it is turned on, a call that names no part does not start,
/// and a named part without what it needs does not start. The message says which piece is missing.
/// </summary>
public sealed class AddIyuUBoardTests
{
    private static HostApplicationBuilder Host() =>
        Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });

    private sealed class NoStore : IBoardStore
    {
        public Task<BoardChange> CreateAsync(string id, string name, string document, string createdBy, DateTimeOffset at, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BoardRecord?> FindAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BoardChange> SaveAsync(string id, string document, int expectedRevision, string createdBy, DateTimeOffset at, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BoardRevisionRecord?> FindRevisionAsync(string id, int revision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BoardRevisionRecord>> ListRevisionsAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BoardPublicationRecord?> PublishAsync(string id, int? revision, string publishedBy, DateTimeOffset at, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BoardPublicationRecord>> ListPublicationsAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task A_host_that_turns_the_board_store_on_with_a_store_starts_and_gets_the_service()
    {
        var builder = Host();
        builder.Services.AddSingleton<IBoardStore, NoStore>();
        builder.Services.AddIyuUBoard(o => o.Boards());
        using var app = builder.Build();

        await app.StartAsync();
        using var scope = app.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<IyuBoardService>());
        await app.StopAsync();
    }

    [Fact]
    public async Task A_call_that_names_no_part_does_not_start()
    {
        var builder = Host();
        builder.Services.AddIyuUBoard(_ => { });
        using var app = builder.Build();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());

        Assert.Contains("turn on at least one part", failure.ToString());
    }

    [Fact]
    public async Task The_board_store_without_a_store_does_not_start_and_the_message_names_it()
    {
        var builder = Host();
        builder.Services.AddIyuUBoard(o => o.Boards());
        using var app = builder.Build();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());

        Assert.Contains("IBoardStore", failure.ToString());
    }

    [Fact]
    public async Task The_store_may_be_registered_after_the_layer_is_turned_on()
    {
        var builder = Host();
        builder.Services.AddIyuUBoard(o => o.Boards());
        builder.Services.AddSingleton<IBoardStore, NoStore>();
        using var app = builder.Build();

        await app.StartAsync();
        await app.StopAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not json")]
    [InlineData("{\"open\":")]
    public async Task A_document_that_is_not_json_is_refused_before_the_store_is_reached(string document)
    {
        var service = new IyuBoardService(new NoStore(), TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("plant", "Plant", document, "ana"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync("plant", document, 1, "ana"));
    }

    [Fact]
    public async Task An_id_a_name_or_a_user_that_is_blank_or_too_long_is_refused_before_the_store_is_reached()
    {
        var service = new IyuBoardService(new NoStore(), TimeProvider.System);
        var tooLong = new string('x', IyuBoardService.MaxTextLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(" ", "Plant", "{}", "ana"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(" plant", "Plant", "{}", "ana"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(tooLong, "Plant", "{}", "ana"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("plant", tooLong, "{}", "ana"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("plant", "Plant", "{}", ""));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SaveAsync("plant", "{}", 0, "ana"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PublishAsync("plant", 0, "ana"));
    }
}
