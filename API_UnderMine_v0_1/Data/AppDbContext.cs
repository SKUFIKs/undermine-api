using API_UnderMine_v0_1.Models;
using Microsoft.EntityFrameworkCore;

namespace API_UnderMine_v0_1.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<McAuthAccount> McAuthAccounts => Set<McAuthAccount>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Основная настройка таблицы
            modelBuilder.Entity<McAuthAccount>(entity =>
            {
                entity.ToTable("authmeplugin");

                entity.HasKey(e => e.Id)
                      .HasName("authmeplugin_pkey"); 

                entity.HasIndex(e => e.Username)
                      .IsUnique()
                      .HasDatabaseName("authmeplugin_username_key");

                entity.Property(e => e.Username).HasMaxLength(255);
                entity.Property(e => e.RealName).HasMaxLength(255);
                entity.Property(e => e.Password).HasMaxLength(255);
                entity.Property(e => e.Email).HasMaxLength(255);
                entity.Property(e => e.DeviceId).HasMaxLength(255);

                entity.Property(e => e.Ip).HasMaxLength(40);
                entity.Property(e => e.RegIp).HasMaxLength(40);

                entity.Property(e => e.Totp).HasMaxLength(32);

                entity.Property(e => e.RegDate)
                      .HasDefaultValue(0L);

                entity.Property(e => e.X)
                      .HasDefaultValue(0.0);

                entity.Property(e => e.Y)
                      .HasDefaultValue(0.0);

                entity.Property(e => e.Z)
                      .HasDefaultValue(0.0);

                entity.Property(e => e.World)
                      .HasMaxLength(255)
                      .HasDefaultValue("world");

                entity.Property(e => e.IsLogged)
                      .HasDefaultValue((short)0);

                entity.Property(e => e.HasSession)
                      .HasDefaultValue((short)0);

                entity.Property(e => e.IsBanned)
                      .HasDefaultValue(false);
            });
        }
    }
}