using LANCommander.Community.Server.RecompCatalog.Core;

namespace LANCommander.Community.Server.RecompCatalog.Tests;

public class StableIdTests
{
    [Fact]
    public void GeneratesStableCaseInsensitiveIds()
    {
        var first = StableId.Create("game:Owner/Repo:Title");
        var second = StableId.Create(" game:owner/repo:title ");

        Assert.Equal(first, second);
        Assert.NotEqual(Guid.Empty, first);
    }
}
