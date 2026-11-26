using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PTCenter.Infrastructure.Persistence;
using Xunit;

namespace PTCenter.Tests;

public class IdentityStoreAvailabilityTests
{
    [Fact]
    public async Task Unavailable_identity_store_fails_closed_with_safe_503_and_valid_session_recovers()
    {
        using var host = new BookingTestHost();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        var token = await host.TokenAsync("Ceo");
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", db.Database.ProviderName);
        // Reproduce a real provider read failure in the disposable database, never on UAT SQL.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE AspNetUsers RENAME TO UnavailableUsers");
        try
        {
            var result = await client.GetAsync("/api/ceo/settings");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
            var body = await result.Content.ReadAsStringAsync();
            Assert.Contains("Authentication.Unavailable", body);
            Assert.DoesNotContain("Sqlite", body);
            Assert.DoesNotContain("UnavailableUsers", body);
            Assert.DoesNotContain("StackTrace", body);
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE UnavailableUsers RENAME TO AspNetUsers"); }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/ceo/settings")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ceo/settings")).StatusCode);
    }
}
