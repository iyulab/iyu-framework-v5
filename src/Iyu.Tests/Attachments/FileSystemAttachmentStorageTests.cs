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
        using var sr = new StreamReader(Assert.IsAssignableFrom<Stream>(read));
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

    [Fact]
    public async Task OpenRead_returns_null_when_the_file_is_absent()
    {
        const string key = "2026/07/bbbb";
        await _storage.SaveAsync(Bytes("x"), key, null);
        await _storage.DeleteAsync(key);

        Assert.Null(await _storage.OpenReadAsync(key));
    }

    [Fact]
    public async Task OpenRead_returns_null_when_the_key_prefix_was_never_written()
    {
        // Keys are path-shaped, so an object that never existed presents as an absent directory rather
        // than an absent file — a distinct exception the backend has to absorb just the same.
        Assert.Null(await _storage.OpenReadAsync("1999/01/never-written"));
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

    /// <summary>A stream that yields some bytes, then fails — an upload cut off part-way.</summary>
    private sealed class FailingStream(string prefix) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(prefix);
        private bool _served;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served) throw new IOException("connection dropped");
            _served = true;
            Array.Copy(_bytes, 0, buffer, offset, _bytes.Length);
            return _bytes.Length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private async Task<string> ReadAsync(string key)
    {
        await using var read = await _storage.OpenReadAsync(key);
        using var sr = new StreamReader(Assert.IsAssignableFrom<Stream>(read));
        return await sr.ReadToEndAsync();
    }

    /// <summary>
    /// Replacing an object with a write that fails part-way leaves the object as it was. Before, the bytes went
    /// into the object's own file and the failure handler deleted it — a cut-off upload destroyed what was there.
    /// </summary>
    [Fact]
    public async Task A_failed_overwrite_leaves_the_stored_object_and_no_partial_file()
    {
        const string key = "2026/10/keep";
        await _storage.SaveAsync(Bytes("original"), key, null);

        await Assert.ThrowsAsync<IOException>(() => _storage.SaveAsync(new FailingStream("half"), key, null));

        Assert.Equal("original", await ReadAsync(key));
        Assert.Equal(["keep"], Directory.GetFiles(Path.Combine(_root, "2026", "10")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Save_replaces_an_existing_object()
    {
        const string key = "2026/10/replace";
        await _storage.SaveAsync(Bytes("one"), key, null);
        await _storage.SaveAsync(Bytes("two"), key, null);
        Assert.Equal("two", await ReadAsync(key));
    }

    [Fact]
    public async Task TryCreate_writes_once_and_then_refuses_without_writing()
    {
        const string key = "2026/10/once";
        Assert.True(await _storage.TryCreateAsync(Bytes("first"), key, null));
        Assert.False(await _storage.TryCreateAsync(Bytes("second"), key, null));
        Assert.Equal("first", await ReadAsync(key));
    }

    /// <summary>The check and the write are one step: of many concurrent creators, exactly one wins.</summary>
    [Fact]
    public async Task Concurrent_creators_of_one_key_produce_exactly_one_winner()
    {
        const string key = "2026/10/race";
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(i => Task.Run(() => _storage.TryCreateAsync(Bytes($"writer-{i}"), key, null))));

        Assert.Single(results, r => r);
        Assert.StartsWith("writer-", await ReadAsync(key));
        Assert.Equal(["race"], Directory.GetFiles(Path.Combine(_root, "2026", "10")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_failed_create_leaves_no_object()
    {
        const string key = "2026/10/cut";
        await Assert.ThrowsAsync<IOException>(() => _storage.TryCreateAsync(new FailingStream("half"), key, null));
        Assert.Null(await _storage.OpenReadAsync(key));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "2026", "10")));
    }

    /// <summary>On local disk the stored file itself is handed out — no copy — and disposing leaves it in place.</summary>
    [Fact]
    public async Task OpenLocalFile_hands_out_the_stored_file_and_disposing_keeps_it()
    {
        const string key = "2026/10/local";
        await _storage.SaveAsync(Bytes("bytes"), key, null);
        var stored = Path.Combine(_root, "2026", "10", "local");

        IAttachmentStorage storage = _storage;
        var local = await storage.OpenLocalFileAsync(key);
        Assert.NotNull(local);
        Assert.Equal(stored, local!.Path);
        await local.DisposeAsync();
        Assert.True(File.Exists(stored));

        Assert.Null(await storage.OpenLocalFileAsync("2026/10/absent"));
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
