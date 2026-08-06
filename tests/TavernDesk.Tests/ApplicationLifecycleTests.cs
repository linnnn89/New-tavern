using TavernDesk.App.Services;

namespace TavernDesk.Tests;

public sealed class ApplicationLifecycleTests
{
    [Fact]
    public void SecondApplicationInstanceIsRejectedUntilFirstExits()
    {
        var gateName = $@"Local\TavernDesk.Tests.{Guid.NewGuid():N}";

        using (var first = SingleInstanceGate.TryAcquire(gateName))
        {
            using var second = SingleInstanceGate.TryAcquire(gateName);

            Assert.True(first.IsPrimaryInstance);
            Assert.False(second.IsPrimaryInstance);
        }

        using var restarted = SingleInstanceGate.TryAcquire(gateName);
        Assert.True(restarted.IsPrimaryInstance);
    }
}
