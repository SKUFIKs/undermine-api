using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnderMineApi.Data;

namespace UnderMineApi.Services
{
    public partial class BuildWorkerService
    {
        private async Task AddPresetContentAsync(
            UnderMineApi.Domain.Entities.BuildInstance build,
            string buildDir,
            AppDbContext context,
            IServiceScopeFactory scopeFactory,
            BuildProgressReporter progress,
            CancellationToken ct)
        {
            if (build.BuildPresetId == null)
            {
                return;
            }

            var preset = await context.BuildPresets
                .Include(p => p.Mods)
                .FirstOrDefaultAsync(p => p.Id == build.BuildPresetId, ct);
            if (preset == null)
            {
                return;
            }

            var modsDir = Path.Combine(buildDir, "mods");
            var shaderpacksDir = Path.Combine(buildDir, "shaderpacks");
            var resourcepacksDir = Path.Combine(buildDir, "resourcepacks");

            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(shaderpacksDir);
            Directory.CreateDirectory(resourcepacksDir);

            // Clear folders as per requirement
            if (Directory.Exists(modsDir))
            {
                foreach (var file in Directory.GetFiles(modsDir))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            if (Directory.Exists(shaderpacksDir))
            {
                foreach (var file in Directory.GetFiles(shaderpacksDir))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            if (Directory.Exists(resourcepacksDir))
            {
                foreach (var file in Directory.GetFiles(resourcepacksDir))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            var enabledMods = preset.Mods.Where(m => m.IsEnabled).ToList();

            using var scope = scopeFactory.CreateScope();
            var modrinthService = scope.ServiceProvider.GetRequiredService<ModrinthService>();
            var s3Storage = scope.ServiceProvider.GetRequiredService<S3StorageService>();

            await progress.LogAsync("info", $"Применяем содержимое пресета: {enabledMods.Count} элементов", ct);

            foreach (var mod in enabledMods)
            {
                ct.ThrowIfCancellationRequested();
                if (mod.SourceType == "Modrinth")
                {
                    var version = await modrinthService.GetCompatibleVersionAsync(mod.ModrinthSlugOrId!, preset.GameVersion, preset.LoaderType, ct);
                    if (version != null)
                    {
                        var targetDir = mod.TargetFolder switch
                        {
                            "shaderpacks" => shaderpacksDir,
                            "resourcepacks" => resourcepacksDir,
                            _ => modsDir
                        };
                        var (localPath, file) = await modrinthService.DownloadVersionAsync(version, targetDir, ct);
                        await progress.LogAsync("info", $"Скачан Modrinth: {Path.GetFileName(localPath)}", ct);
                    }
                    else
                    {
                        await progress.LogAsync("warning", $"Не найдена совместимая версия: {mod.ModrinthSlugOrId}", ct);
                    }
                }
                else if (mod.SourceType == "CustomUpload")
                {
                    if (!string.IsNullOrEmpty(mod.CustomFileS3Key))
                    {
                        var targetDir = mod.TargetFolder switch
                        {
                            "shaderpacks" => shaderpacksDir,
                            "resourcepacks" => resourcepacksDir,
                            _ => modsDir
                        };
                        var fileName = mod.CustomFileName ?? Path.GetFileName(mod.CustomFileS3Key);
                        var localPath = Path.Combine(targetDir, fileName);
                        await s3Storage.DownloadFileAsync(mod.CustomFileS3Key, localPath, ct);
                        await progress.LogAsync("info", $"Скачан кастомный файл: {fileName}", ct);
                    }
                }
            }
        }
    }
}
