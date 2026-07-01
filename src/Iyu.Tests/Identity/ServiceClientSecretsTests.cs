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

    [Fact]
    public void Verify_ReturnsFalse_OnMalformedHash()
    {
        Assert.False(ServiceClientSecrets.Verify("secret", "not-a-hash"));
    }

    [Fact]
    public void Verify_ReturnsFalse_OnInvalidBase64()
    {
        Assert.False(ServiceClientSecrets.Verify("secret", "1.invalid!!.base64"));
    }

    [Fact]
    public void Verify_ReturnsFalse_OnEmptyHash()
    {
        Assert.False(ServiceClientSecrets.Verify("secret", ""));
    }
}
