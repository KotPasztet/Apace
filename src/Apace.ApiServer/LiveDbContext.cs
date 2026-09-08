using Microsoft.EntityFrameworkCore;
using Apace.ApiServer.Models;

namespace Apace.ApiServer;

public class LiveDbContext : DbContext
{
    public LiveDbContext(DbContextOptions<LiveDbContext> options)
        : base(options)
    {
    }

    public DbSet<Account> Accounts { get; set; }
}
