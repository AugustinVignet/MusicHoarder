using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
namespace MusicHoarder.Api.Tests.Review;

public sealed class CopyAuditModelCacheTests
{
    [Fact]
    public void SystemAndAnonymousAccessorModelsDoNotShareCapturedTenantFilters()
    {
        var options = new DbContextOptionsBuilder().UseNpgsql("Host=localhost;Database=unused_model_only").Options;
        using var anonymous = new MusicHoarderDbContext(options, new TestCurrentUserAccessor());
        using var system = new MusicHoarderDbContext(options);
        using var repeatedSystem = new MusicHoarderDbContext(options);
        Assert.NotSame(anonymous.Model, system.Model); Assert.Same(system.Model, repeatedSystem.Model);
    }

}
