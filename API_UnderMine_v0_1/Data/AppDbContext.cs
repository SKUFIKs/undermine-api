using API_UnderMine.Models;
using API_UnderMine_v0_1.Models;
using Microsoft.EntityFrameworkCore;

namespace API_UnderMine_v0_1.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<McAuthAccount> McAuthAccounts => Set<McAuthAccount>();
        public DbSet<CoreData> CoreDatas => Set<CoreData>();

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
            modelBuilder.Entity<CoreData>(entity =>
            {
                entity.ToTable("config");

                // Указываем имена колонок точно так, как в твоем CREATE TABLE
                entity.HasKey(e => e.Id).HasName("config_pkey");
                entity.Property(e => e.Id).HasColumnName("id");

                entity.Property(e => e.Version)
                      .HasColumnName("version") // в SQL было "version"
                      .HasMaxLength(25)
                      .HasDefaultValue("NULL");

                entity.Property(e => e.ForgeVersion)
                      .HasColumnName("forge") // в SQL было "forge"
                      .HasMaxLength(25)
                      .HasDefaultValue("NULL");

                entity.Property(e => e.InstallLink)
                      .HasColumnName("installLink") // в SQL было "installLink"
                      .HasMaxLength(256)
                      .HasDefaultValue("NULL");

                entity.Property(e => e.ConfLink)
                      .HasColumnName("confLink")
                      .HasMaxLength(256);

                entity.Property(e => e.KubejsLink)
                      .HasColumnName("kubejsLink")
                      .HasMaxLength(256);

                entity.Property(e => e.ShadersLink)
                      .HasColumnName("shadersLink")
                      .HasMaxLength(256);

                entity.Property(e => e.ManifestLink)
                      .HasColumnName("manifestLink")
                      .HasMaxLength(256)
                      .HasDefaultValue("NULL");

                entity.Property(e => e.LibraryExpectedChecksum)
                      .HasColumnName("libraryExpectedChecksum")
                      .HasMaxLength(50);

                entity.Property(e => e.VersionExpectedChecksum)
                      .HasColumnName("versionExpectedChecksum")
                      .HasMaxLength(50);

                entity.Property(e => e.AssetsExpectedChecksum)
                      .HasColumnName("assetsExpectedChecksum")
                      .HasMaxLength(50);

                entity.Property(e => e.UiVersion)
                      .HasColumnName("uiVersion")
                      .HasMaxLength(256);
                entity.Property(e => e.ClientLink)
                      .HasColumnName("clientLink")
                      .HasMaxLength(256);
            });
        }
    }
}