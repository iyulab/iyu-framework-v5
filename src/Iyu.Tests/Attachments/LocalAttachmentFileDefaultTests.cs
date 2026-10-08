using System.Text;
using Iyu.Core.Attachments;
using Xunit;

namespace Iyu.Tests.Attachments;

/// <summary>
/// The default <see cref="IAttachmentStorage.OpenLocalFileAsync"/> — what a backend that keeps no local file gets
/// without writing anything: a temporary copy, deleted on dispose.
/// </summary>
public sealed class LocalAttachmentFileDefaultTests
{
    [Fact]
    public async Task A_backend_without_local_files_gets_a_temporary_copy_deleted_on_dispose()
    {
        IAttachmentStorage storage = new FakeAttachmentStorage();
        await storage.SaveAsync(new MemoryStream(Encoding.UTF8.GetBytes("payload")), "k", null);

        string path;
        await using (var local = await storage.OpenLocalFileAsync("k"))
        {
            Assert.NotNull(local);
            path = local!.Path;
            Assert.Equal("payload", await File.ReadAllTextAsync(path));
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task An_absent_object_has_no_local_file()
    {
        IAttachmentStorage storage = new FakeAttachmentStorage();
        Assert.Null(await storage.OpenLocalFileAsync("missing"));
    }

    [Fact]
    public async Task The_fake_creates_once()
    {
        IAttachmentStorage storage = new FakeAttachmentStorage();
        Assert.True(await storage.TryCreateAsync(new MemoryStream([1]), "k", null));
        Assert.False(await storage.TryCreateAsync(new MemoryStream([2]), "k", null));
    }
}
