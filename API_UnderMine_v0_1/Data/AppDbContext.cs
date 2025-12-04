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
                .ToTable("authme")
                .HasKey(u => u.Id);
            modelBuilder.Entity<McAuthAccount>()
                .Property(u => u.Username).HasColumnName("username");
            modelBuilder.Entity<McAuthAccount>()
                .Property(u => u.RealName).HasColumnName("realname");
            modelBuilder.Entity<McAuthAccount>()
                .Property(u => u.PasswordHash).HasColumnName("password");
            modelBuilder.Entity<McAuthAccount>()
                .Property(u => u.regip).HasColumnName("regip");
            modelBuilder.Entity<McAuthAccount>()
                .Property(u => u.ip).HasColumnName("ip");
            modelBuilder.Entity<McAuthAccount>()
                .Property(u => u.regTime).HasColumnName("reg_time");
            modelBuilder.Entity<McAuthAccount>()
                .Property(u => u.logTime).HasColumnName("log_time"); 
            modelBuilder.Entity<McAuthAccount>()
                .Property(u => u.IsBanned).HasColumnName("IsBanned");

        }
    }
}
