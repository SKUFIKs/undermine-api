using UnderMineApi.Models;
using Microsoft.EntityFrameworkCore;

namespace UnderMineApi.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<McAuthAccount> McAuthAccounts => Set<McAuthAccount>();
        public DbSet<CoreData> CoreDatas => Set<CoreData>();

        public DbSet<TechnoConfig> TechnoConfigs => Set<TechnoConfig>();
        public DbSet<VanilaConfig> VanilaConfigs => Set<VanilaConfig>();
        public DbSet<GlobalSetting> GlobalSettings { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);


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

            ConfigureConfigEntity<CoreData>(modelBuilder, "config", "config_pkey");
            ConfigureConfigEntity<TechnoConfig>(modelBuilder, "TechnoConfig", "TechnoConfig_pkey");
            ConfigureConfigEntity<VanilaConfig>(modelBuilder, "VanilaConfig", "VanilaConfig_pkey");

            modelBuilder.Entity<GlobalSetting>(entity =>
            {
                entity.ToTable("global_settings"); 
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Key).HasMaxLength(100).IsRequired();
                entity.Property(e => e.Value).HasMaxLength(255);
            });
        }

        private void ConfigureConfigEntity<T>(ModelBuilder modelBuilder, string tableName, string primaryKeyName) where T : class
        {
            modelBuilder.Entity<T>(entity =>
            {
                entity.ToTable(tableName);
                entity.HasKey("Id").HasName(primaryKeyName);

               

                var properties = new[] { "Version", "ForgeVersion", "InstallLink", "ConfLink", "ShadersLink", "ManifestLink" };
                foreach (var prop in properties)
                {
                    entity.Property(prop).HasMaxLength(256).HasDefaultValue("NULL");
                }

                entity.Property("Version").HasMaxLength(25);
                entity.Property("ForgeVersion").HasMaxLength(25);

                entity.Property("LibraryExpectedChecksum").HasMaxLength(50);
                entity.Property("VersionExpectedChecksum").HasMaxLength(50);
                entity.Property("AssetsExpectedChecksum").HasMaxLength(50);

                entity.Property("UiVersion").HasMaxLength(256);
                entity.Property("ClientLink").HasMaxLength(256);
            });
        }
    }
}