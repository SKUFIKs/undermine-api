using UnderMineApi.Models;
using Microsoft.EntityFrameworkCore;
using UnderMineApi.Domain.Entities;
using UnderMineApi.Services.Security;

namespace UnderMineApi.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<UnderMineApi.Domain.Entities.UserAccount> McAuthAccounts { get; set; }
        public DbSet<UnderMineApi.Domain.Entities.UserIpHistory> UserIpHistory => Set<UnderMineApi.Domain.Entities.UserIpHistory>();
        public DbSet<UnderMineApi.Domain.Entities.RolePermission> RolePermissions => Set<UnderMineApi.Domain.Entities.RolePermission>();
        public DbSet<ServerManifest> ServerManifests => Set<ServerManifest>();
        public DbSet<UserBans> UserBans => Set<UserBans>();
        public DbSet<UserBanEvidence> UserBanEvidence => Set<UserBanEvidence>();
        public DbSet<UserData> UserData => Set<UserData>();
        public DbSet<GlobalSetting> GlobalSettings => Set<GlobalSetting>();
        public DbSet<ServerConfigDetails> ServerConfigs => Set<ServerConfigDetails>();
        public DbSet<AdminAudit> AdminAudits { get; set; }
        public DbSet<UnderMineApi.Domain.Entities.BuildInstance> BuildInstances => Set<UnderMineApi.Domain.Entities.BuildInstance>();
        public DbSet<UnderMineApi.Domain.Entities.BuildFile> BuildFiles => Set<UnderMineApi.Domain.Entities.BuildFile>();
        public DbSet<UnderMineApi.Domain.Entities.BuildLog> BuildLogs => Set<UnderMineApi.Domain.Entities.BuildLog>();
        public DbSet<UnderMineApi.Domain.Entities.BuildPreset> BuildPresets => Set<UnderMineApi.Domain.Entities.BuildPreset>();
        public DbSet<UnderMineApi.Domain.Entities.BuildPresetMod> BuildPresetMods => Set<UnderMineApi.Domain.Entities.BuildPresetMod>();
        public DbSet<ClientReport> ClientReports => Set<ClientReport>();
        public DbSet<ClientReportFile> ClientReportFiles => Set<ClientReportFile>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<UnderMineApi.Domain.Entities.UserAccount>(entity =>
            {
                entity.HasIndex(u => u.Username).IsUnique();
                entity.Property(u => u.Role).HasDefaultValue("user");

                // Registration limits filter by device and by registration subnet on every
                // registration request.
                entity.Property(u => u.DeviceId).HasMaxLength(128);
                entity.Property(u => u.RegIpPrefix).HasMaxLength(64);
                entity.HasIndex(u => u.DeviceId);
                entity.HasIndex(u => u.RegIp);
                entity.HasIndex(u => u.RegIpPrefix);

                entity.HasOne(u => u.Stats)
                      .WithOne(s => s.Account)
                      .HasForeignKey<UserData>(s => s.AccountId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<UserIpHistory>(entity =>
            {
                entity.ToTable("user_ip_history");
                entity.Property(h => h.Ip).HasMaxLength(64);
                entity.HasIndex(h => new { h.UserId, h.Ip }).IsUnique();
                entity.HasIndex(h => new { h.UserId, h.LastSeen });

                entity.HasOne<UserAccount>()
                      .WithMany()
                      .HasForeignKey(h => h.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<RolePermission>(entity =>
            {
                entity.ToTable("role_permissions");
                entity.HasKey(e => new { e.Role, e.Permission });
                entity.Property(e => e.Role).HasMaxLength(50);
                entity.Property(e => e.Permission).HasMaxLength(100);

                entity.HasData(Permissions.ModeratorDefaults
                    .Select(permission => new RolePermission
                    {
                        Role = Roles.Moderator,
                        Permission = permission
                    })
                    .ToArray());
            });

            modelBuilder.Entity<ServerManifest>(entity =>
            {
                entity.ToTable("server_manifests");
                entity.Property(e => e.ServerName).HasMaxLength(100);
                entity.Property(e => e.BuildId).HasMaxLength(64);

                entity.HasOne(s => s.Config)
                      .WithOne(c => c.Server)
                      .HasForeignKey<ServerConfigDetails>(c => c.ServerId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<UserBans>(entity =>
            {
                entity.ToTable("user_bans");
                entity.HasOne<UnderMineApi.Domain.Entities.UserAccount>()
                      .WithMany()
                      .HasForeignKey(b => b.UserId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasMany(b => b.Evidence)
                      .WithOne(e => e.Ban)
                      .HasForeignKey(e => e.BanId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<UserBanEvidence>(entity =>
            {
                entity.ToTable("user_ban_evidence");
                entity.Property(e => e.FileName).HasMaxLength(255);
                entity.Property(e => e.ContentType).HasMaxLength(100);
                entity.Property(e => e.Sha256).HasMaxLength(64);
            });

            modelBuilder.Entity<GlobalSetting>(entity =>
            {
                entity.ToTable("global_settings");
                entity.HasKey(e => e.Key);
                entity.HasData(new GlobalSetting { Key = "Maintenance", Value = "false" });
            });

            modelBuilder.Entity<AdminAudit>(entity =>
            {
                entity.ToTable("admin_audits");
                entity.Property(e => e.Actor).HasMaxLength(100);
                entity.Property(e => e.Action).HasMaxLength(100);

                // Activity page: newest-first paging and date-range filters.
                entity.HasIndex(e => e.CreatedAt);
            });

            modelBuilder.Entity<UnderMineApi.Domain.Entities.BuildInstance>(entity =>
            {
                entity.ToTable("build_instances");
                entity.HasIndex(e => e.BuildId).IsUnique();
                // Admin list: ORDER BY created_at DESC, optional status filter.
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => new { e.Status, e.CreatedAt });
                entity.Property(e => e.Status).HasMaxLength(50);
                entity.Property(e => e.Name).HasMaxLength(100);
                entity.Property(e => e.LoaderType).HasMaxLength(50);

                entity.HasMany(e => e.Files)
                      .WithOne(f => f.BuildInstance)
                      .HasForeignKey(f => f.BuildInstanceId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.BuildPreset)
                      .WithMany()
                      .HasForeignKey(e => e.BuildPresetId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<UnderMineApi.Domain.Entities.BuildFile>(entity =>
            {
                entity.ToTable("build_files");
                entity.HasIndex(e => e.Sha256); // For dedup lookups
                entity.Property(e => e.Path).HasMaxLength(500);
            });

            modelBuilder.Entity<UnderMineApi.Domain.Entities.BuildLog>(entity =>
            {
                entity.ToTable("build_logs");
                entity.Property(e => e.Level).HasMaxLength(10);
                entity.HasIndex(e => e.BuildInstanceId); // Log reads are always per-build
                entity.HasIndex(e => e.CreatedAt);       // Retention deletes by age
                entity.HasOne(e => e.BuildInstance)
                      .WithMany()
                      .HasForeignKey(e => e.BuildInstanceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<UnderMineApi.Domain.Entities.BuildPreset>(entity =>
            {
                entity.ToTable("build_presets");
                entity.Property(e => e.Name).HasMaxLength(100);
                entity.Property(e => e.GameVersion).HasMaxLength(50);
                entity.Property(e => e.LoaderType).HasMaxLength(50);
                entity.Property(e => e.LoaderVersion).HasMaxLength(100);

                entity.HasMany(e => e.Mods)
                      .WithOne(m => m.BuildPreset)
                      .HasForeignKey(m => m.BuildPresetId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<UnderMineApi.Domain.Entities.BuildPresetMod>(entity =>
            {
                entity.ToTable("build_preset_mods");
                entity.Property(e => e.SourceType).HasMaxLength(50);
                entity.Property(e => e.TargetFolder).HasMaxLength(50);
                entity.Property(e => e.ModrinthSlugOrId).HasMaxLength(200);
                entity.Property(e => e.CustomFileName).HasMaxLength(500);
                entity.Property(e => e.CustomFileS3Key).HasMaxLength(500);
                entity.Property(e => e.Sha256Hash).HasMaxLength(100);
            });

            modelBuilder.Entity<ClientReport>(entity =>
            {
                entity.ToTable("client_reports");
                entity.Property(e => e.Kind).HasMaxLength(20);
                entity.Property(e => e.Status).HasMaxLength(20);
                entity.Property(e => e.DeviceId).HasMaxLength(128);
                entity.Property(e => e.Title).HasMaxLength(200);
                entity.Property(e => e.Summary).HasMaxLength(4000);
                entity.Property(e => e.AdminNote).HasMaxLength(4000);
                entity.Property(e => e.LauncherVersion).HasMaxLength(64);
                entity.Property(e => e.MinecraftVersion).HasMaxLength(64);
                entity.Property(e => e.Loader).HasMaxLength(64);
                entity.Property(e => e.ServerName).HasMaxLength(100);
                entity.Property(e => e.Os).HasMaxLength(200);
                entity.Property(e => e.ConsentVersion).HasMaxLength(32);
                entity.Property(e => e.ResolvedBy).HasMaxLength(100);
                entity.Property(e => e.Ip).HasMaxLength(64);

                // Admin list: newest first with optional status/kind/user filters.
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => new { e.Status, e.CreatedAt });
                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.DeviceId);

                entity.HasOne(e => e.User)
                      .WithMany()
                      .HasForeignKey(e => e.UserId)
                      .OnDelete(DeleteBehavior.SetNull);

                entity.HasMany(e => e.Files)
                      .WithOne(f => f.Report)
                      .HasForeignKey(f => f.ReportId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ClientReportFile>(entity =>
            {
                entity.ToTable("client_report_files");
                entity.Property(e => e.Kind).HasMaxLength(32);
                entity.Property(e => e.FileName).HasMaxLength(255);
                entity.Property(e => e.ContentType).HasMaxLength(100);
                entity.Property(e => e.Sha256).HasMaxLength(64);
                entity.HasIndex(e => e.ReportId);
            });
        }
    }
}