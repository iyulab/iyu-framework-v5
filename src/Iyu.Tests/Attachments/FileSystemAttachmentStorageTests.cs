using System.Text;
using Iyu.Core.Attachments;
using Iyu.FileServer;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Attachments;

public sealed class FileSystemAttachmentStorageTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemAttachmentStorage _storage;

    public FileSystemAttachmentStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "iyu-fs-store-" + Guid.NewGuid().ToString("N"));
        _storage = new FileSystemAttachmentStorage(new FileSystemOptions { RootPath = _root });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Stream Bytes(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Save_then_read_round_trips_and_creates_nested_dirs()
    {
        const string key = "2026/07/deadbeefdeadbeefdeadbeefdeadbeef";
        var returned = await _storage.SaveAsync(Bytes("hello"), key, "text/plain");

        Assert.Equal(key, returned);
        Assert.True(File.Exists(Path.Combine(_root, "2026", "07", "deadbeefdeadbeefdeadbeefdeadbeef")));

        await using var read = await _storage.OpenReadAsync(key);
        using var sr = new StreamReader(read);
        Assert.Equal("hello", await sr.ReadToEndAsync());
    }

    [Fact]
    public async Task Delete_removes_object()
    {
        const string key = "2026/07/aaaa";
        await _storage.SaveAsync(Bytes("x"), key, null);
        await _storage.DeleteAsync(key);
        Assert.False(File.Exists(Path.Combine(_root, "2026", "07", "aaaa")));
    }

    [Fact]
    public async Task Delete_absent_is_noop()
    {
        var ex = await Record.ExceptionAsync(() => _storage.DeleteAsync("2026/07/missing"));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("2026/../../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x")]
    public async Task Traversal_or_rooted_keys_are_rejected(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.SaveAsync(Bytes("x"), key, null));
    }

    [Fact]
    public void RootPath_required()
    {
        Assert.Throws<ArgumentException>(() => new FileSystemAttachmentStorage(new FileSystemOptions { RootPath = " " }));
    }

    [Fact]
    public void AddIyuFileGateway_filesystem_overload_registers_fs_storage()
    {
        var services = new ServiceCollection();
        services.AddIyuFileGateway(
            gw => { gw.SigningKey = "0123456789abcdef0123456789abcdef"; },
            fs => { fs.RootPath = _root; });
        var sp = services.BuildServiceProvider();

        Assert.IsType<FileSystemAttachmentStorage>(sp.GetService<IAttachmentStorage>());
        Assert.NotNull(sp.GetService<FileAccessTokenService>());
    }
}
