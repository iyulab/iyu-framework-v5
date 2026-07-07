using System;
using System.IO;
using System.Linq;
using Iyu.VaultAi;
using Xunit;

namespace Iyu.Tests.VaultAi;

public class VaultAiReportsApiTests
{
    [Theory]
    [InlineData("/api/vault-ai/reports/01-daily", "01-daily", null, null)]
    [InlineData("/api/vault-ai/reports/01-daily/generate", "01-daily", "generate", null)]
    [InlineData("/api/vault-ai/reports/01-daily/outputs/20260611-0700.md",
        "01-daily", "outputs", "20260611-0700.md")]
    public void TryParseSegments_ValidPaths_Parses(
        string path, string folder, string? action, string? file)
    {
        var ok = VaultAiReportsApi.TryParseSegments(path, out var f, out var a, out var fl);

        Assert.True(ok);
        Assert.Equal(folder, f);
        Assert.Equal(action, a);
        Assert.Equal(file, fl);
    }

    [Theory]
    [InlineData("/api/vault-ai/reports/..")]                      // 상위 디렉터리 탈출
    [InlineData("/api/vault-ai/reports/../outputs/x.md")]
    [InlineData("/api/vault-ai/reports/.")]
    [InlineData("/api/vault-ai/reports/01-daily/outputs/..")]
    [InlineData("/api/vault-ai/reports/a%2Fb")]                   // 인코딩 슬래시 잔존
    [InlineData("/api/vault-ai/reports/")]
    [InlineData("/other/path")]
    public void TryParseSegments_TraversalOrInvalid_Rejected(string path)
    {
        Assert.False(VaultAiReportsApi.TryParseSegments(path, out _, out _, out _));
    }

    [Fact]
    public void PruneLogs_KeepsNewestUpToLimit_DeletesOldest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vaultai-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 슬롯 시각이 파일명에 인코딩됨 — 사전식 정렬이 시간순과 일치
            for (int day = 1; day <= 35; day++)
                File.WriteAllText(Path.Combine(dir, $"202606{day:00}-0900.log"), "x");

            VaultAiReportsApi.PruneLogs(dir, keep: 30);

            var remaining = Directory.GetFiles(dir, "*.log")
                .Select(Path.GetFileName)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(30, remaining.Count);
            // 가장 오래된 5개(01~05)는 삭제, 최신 30개(06~35)는 보존
            Assert.Equal("20260606-0900.log", remaining.First());
            Assert.Equal("20260635-0900.log", remaining.Last());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PruneLogs_BelowLimit_KeepsAll()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vaultai-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 1; i <= 10; i++)
                File.WriteAllText(Path.Combine(dir, $"2026061{i % 10}-0900.log"), "x");

            VaultAiReportsApi.PruneLogs(dir, keep: 30);

            Assert.Equal(10, Directory.GetFiles(dir, "*.log").Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
