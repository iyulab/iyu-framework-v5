using Iyu.Core.Entities;
using Xunit;

namespace Iyu.Tests.Entities;

public class IyuEntityTests
{
    private sealed class Sample : IyuEntity { }

    [Fact]
    public void Default_Id_is_empty_guid_until_assigned()
    {
        var s = new Sample();
        Assert.Equal(Guid.Empty, s.Id);
        Assert.Equal(default, s.CreatedAt);
        Assert.Equal(default, s.UpdatedAt);
    }

    [Fact]
    public void Timestamps_are_plain_properties()
    {
        var now = DateTimeOffset.UtcNow;
        var s = new Sample { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        Assert.Equal(now, s.CreatedAt);
        Assert.Equal(now, s.UpdatedAt);
    }
}
