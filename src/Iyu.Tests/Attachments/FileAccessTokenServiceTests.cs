using Iyu.Core.Attachments;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iyu.Tests.Attachments;

public sealed class FileAccessTokenServiceTests
{
    private const string Key = "0123456789abcdef0123456789abcdef"; // 32 bytes

    private static FileAccessToken Sample(DateTimeOffset exp) => new(
        AttachmentId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        StorageKey: "2026/07/abc",
        Op: FileTokenOp.Upload,
        FileName: "order.pdf",
        ContentType: "application/pdf",
        ExpiresAt: exp);

    [Fact]
    public void Sign_then_Validate_roundtrips()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-03T00:00:00Z"));
        var svc = new FileAccessTokenService(clock);
        var token = svc.Sign(Sample(clock.GetUtcNow().AddMinutes(5)), Key);

        Assert.True(svc.TryValidate(token, Key, out var result));
        Assert.NotNull(result);
        Assert.Equal("2026/07/abc", result!.StorageKey);
        Assert.Equal(FileTokenOp.Upload, result.Op);
        Assert.Equal("order.pdf", result.FileName);
    }

    [Fact]
    public void Validate_rejects_wrong_key()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-03T00:00:00Z"));
        var svc = new FileAccessTokenService(clock);
        var token = svc.Sign(Sample(clock.GetUtcNow().AddMinutes(5)), Key);
        Assert.False(svc.TryValidate(token, "ffffffffffffffffffffffffffffffff", out _));
    }

    [Fact]
    public void Validate_rejects_tampered_payload()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-03T00:00:00Z"));
        var svc = new FileAccessTokenService(clock);
        var token = svc.Sign(Sample(clock.GetUtcNow().AddMinutes(5)), Key);
        var tampered = "X" + token[1..];
        Assert.False(svc.TryValidate(tampered, Key, out _));
    }

    [Fact]
    public void Validate_rejects_expired()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-03T00:00:00Z"));
        var svc = new FileAccessTokenService(clock);
        var token = svc.Sign(Sample(clock.GetUtcNow().AddMinutes(5)), Key);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(svc.TryValidate(token, Key, out _));
    }

    [Fact]
    public void Validate_rejects_malformed()
    {
        var svc = new FileAccessTokenService();
        Assert.False(svc.TryValidate("", Key, out _));
        Assert.False(svc.TryValidate("no-dot", Key, out _));
        Assert.False(svc.TryValidate("a.b.c", Key, out _));
    }
}
