using Iyu.Core.Attachments;
using Iyu.Core.Entities;
using Xunit;

namespace Iyu.Tests.Attachments;

public sealed class ContractTests
{
    private sealed class TestAttachment : IyuAttachment { }

    [Fact]
    public void IyuAttachment_implements_contracts()
    {
        var a = new TestAttachment
        {
            Id = Guid.NewGuid(),
            FileName = "order.pdf",
            StorageKey = "2026/07/abc",
        };
        Assert.IsAssignableFrom<IAttachment>(a);
        Assert.IsAssignableFrom<IyuEntity>(a);
        Assert.Equal("order.pdf", a.FileName);
        Assert.Null(a.UploadedAt);
    }
}
