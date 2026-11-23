using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UnderMineApi.Data;

/// <summary>
/// Design-time context for <c>dotnet ef</c>. Migrations only need the model, so a placeholder
/// connection string is enough; it keeps EF from starting the real API (which connects to the
/// database and runs startup migrations).
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__PostgreConnection")
            ?? "Host=localhost;Database=undermine;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}
