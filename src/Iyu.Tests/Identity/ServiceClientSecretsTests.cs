using Iyu.MainServer.Identity;
using Xunit;

namespace Iyu.Tests.Identity;

public class ServiceClientSecretsTests
{
    [Fact]
    public void Generate_ProducesClientId_AndVerifiableSecret()
    {
        var (clientId, secret, hash) = ServiceClientSecrets.Generate();
        Assert.StartsWith("svc_", clientId);
        Assert.False(string.IsNullOrWhiteSpace(secret));
        Assert.NotEqual(secret, hash);                 // 해시는 평문과 다름
        Assert.True(ServiceClientSecrets.Verify(secret, hash));
    }

    [Fact]
    public void Verify_RejectsWrongSecret()
    {
        var (_, _, hash) = ServiceClientSecrets.Generate();
        Assert.False(ServiceClientSecrets.Verify("wrong-secret", hash));
    }

    [Fact]
    public void Generate_IsUniquePerCall()
    {
        var a = ServiceClientSecrets.Generate();
        var b = ServiceClientSecrets.Generate();
        Assert.NotEqual(a.clientId, b.clientId);
        Assert.NotEqual(a.plaintextSecret, b.plaintextSecret);
    }
}
