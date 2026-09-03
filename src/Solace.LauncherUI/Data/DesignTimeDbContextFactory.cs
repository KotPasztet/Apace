using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Solace.LauncherUI.Data;

/// <summary>
/// Allows `dotnet ef migrations add` to work without bootstrapping the whole app
/// (Program.cs registers the DbContext as scoped services, which the design-time
/// host cannot resolve from the root provider).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseSqlite("DataSource=Data/app.db;Cache=Shared");

        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
