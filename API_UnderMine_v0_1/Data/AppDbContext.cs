using API_UnderMine_v0_1.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace API_UnderMine_v0_1.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<McAuthAccount> McAuthAccounts => Set<McAuthAccount>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<McAuthAccount>()
                .ToTable("authmeplugin")
                .HasKey(u => u.Id);

         
        }
    }
}
