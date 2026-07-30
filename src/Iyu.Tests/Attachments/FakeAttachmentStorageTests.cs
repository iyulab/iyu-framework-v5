using System.Text;
using Xunit;

namespace Iyu.Tests.Attachments;

public sealed class FakeAttachmentStorageTests
{
    [Fact]
    public async Task Save_then_read_roundtrips()
    {
        var storage = new FakeAttachmentStorage();
        var bytes = Encoding.UTF8.GetBytes("hello");
        await storage.SaveAsync(new MemoryStream(bytes), "k1", "text/plain", default);

        await using var read = await storage.OpenReadAsync("k1", default);
        using var ms = new MemoryStream();
        await Assert.IsAssignableFrom<Stream>(read).CopyToAsync(ms);
        Assert.Equal("hello", Encoding.UTF8.GetString(ms.ToArray()));

        await storage.DeleteAsync("k1", default);
        Assert.False(storage.Objects.ContainsKey("k1"));
    }

    [Fact]
    public async Task OpenRead_returns_null_for_an_absent_key()
    {
        // The double must mirror the contract, not merely satisfy the compiler: it previously threw
        // KeyNotFoundException here, which no real backend does, so the gateway's absence path went untested.
        var storage = new FakeAttachmentStorage();

        Assert.Null(await storage.OpenReadAsync("never-written", default));
    }
}
