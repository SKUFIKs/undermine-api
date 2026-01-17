using API_UnderMine.Models;
using API_UnderMine.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace API_UnderMine_v0_1.Controllers;

[ApiController]
[Route("api/v0.1")]
[Authorize]
public class ReceivingServerData : ControllerBase
{
    private readonly ILogger<ReceivingServerData> _logger;
    private readonly ConfigCacheService _cache;

    public ReceivingServerData(ILogger<ReceivingServerData> logger, ConfigCacheService cache)
    {
        _logger = logger;
        _cache = cache;
    }

    [HttpGet("server_info")]
    public IActionResult GetData()
    {
        try
        {
            var username = User.FindFirstValue(ClaimTypes.Name);

            var config = _cache.CachedCore;

            if (config == null) return StatusCode(503, "Core config not loaded");

            return Ok(new
            {
                username,
                versionMC1 = config.Version,
                forgeV1 = config.ForgeVersion,
                manifestLink1 = config.ManifestLink,
                InstallerLink = config.InstallLink,
                config.ConfLink,
                config.KubejsLink,
                config.ShadersLink
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetData");
            return StatusCode(500, new { Error = "INTERNAL_ERROR" });
        }
    }

    [HttpGet("TechnoMagic")]
    public IActionResult GetTechnoData()
    {
        try
        {
            var username = User.FindFirstValue(ClaimTypes.Name);
   
            var config = _cache.CachedTechno;

            if (config == null) return StatusCode(503, "Techno config not loaded");

            return Ok(new
            {
                username,
                version = config.Version,
                forge = config.ForgeVersion,
                manifestLink = config.ManifestLink,
                installerLink = config.InstallLink,
                config.ConfLink,
                config.KubejsLink,
                config.ShadersLink,
                config.LibraryExpectedChecksum,
                config.VersionExpectedChecksum,
                config.AssetsExpectedChecksum,
                config.UiVersion,
                config.ClientLink
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetTechnoData");
            return StatusCode(500, new { Error = "INTERNAL_ERROR" });
        }
    }

    [HttpGet("servers")]
    public IActionResult GetServers()
    {
        try
        {
            var username = User.FindFirstValue(ClaimTypes.Name);
            if (string.IsNullOrEmpty(username)) return Unauthorized();

            var techno = _cache.CachedTechno;
            var vanila = _cache.CachedVanila;

            if (techno == null || vanila == null)
            {
                return StatusCode(503, "One or more configurations are not loaded yet.");
            }

            var servers = new List<object>();

            if (_cache.IsVanilaEnabled && _cache.CachedVanila != null)
            {
                servers.Add(new
                {
                    Id = 1,
                    Name = "Vanila+",
                    Username = username,
                    Description = new List<object> {
                        new { Type = "Run", Text = "Сервер Vanila+", IsBold = true, FontSize = 26 },
                         new { Type = "LineBreak" },
                         new { Type = "LineBreak" },
                         new { Type = "Run", Text = "Сервер TechnoMagic основан на сборке Create Chronicles: Bosses and Beyond.", IsBold = true},
                         new { Type = "LineBreak" },
                         new { Type = "LineBreak" },
                         new { Type = "Run", Text = "Вас ждет проработанная система прогресса, состоящая из 10 глав. Она объединяет автоматизацию, исследование, магию и битвы с боссами. В вашем распоряжении более 800 квестов, которые постепенно помогут освоить развитие технологий и магических искусств.", IsBold = true },
                         new { Type = "LineBreak" },
                         new { Type = "LineBreak" },
                         new { Type = "Run", Text = "Изучайте мир с модом Create, стройте производственные линии и получайте доступ к новому контенту с каждой главой. Сражайтесь с боссами из L’EnderCataclysm, Mowzie’s Mobs и Bosses of Mass Destruction, развивая при этом собственное древо талантов. Посетите новые измерения – от Twilight Forest до The Aether и Blue Skies, исследуйте необычные пещеры из Alex’s Caves, приручайте драконов из Ice and Fire и отправляйтесь в космос с Ad Astra.", IsBold = true },
                         new { Type = "LineBreak" },
                         new { Type = "LineBreak" },
                         new { Type = "Run", Text = "Ждем вас на нашем сервере!", IsBold = true }
                    },
                    Config = new
                    {
                        server = "Vanila+",
                        version = vanila.Version,
                        bootloaderType = vanila.bootloaderType,
                        loader = vanila.ForgeVersion,
                        clientLink = vanila.ClientLink,
                        installerLink = vanila.InstallLink,
                        manifestLink = vanila.ManifestLink,
                        confLink = vanila.ConfLink,
                        kubejsLink = vanila.KubejsLink,
                        shadersLink = vanila.ShadersLink,
                        versionExpectedChecksum = vanila.VersionExpectedChecksum,
                        libraryExpectedChecksum = vanila.LibraryExpectedChecksum,
                        assetsExpectedChecksum = vanila.AssetsExpectedChecksum
                    }
                });
            }
            if (_cache.IsTechnoEnabled && _cache.CachedTechno != null)
            {
                servers.Add(new
                {
                    Id = 2,
                    Name = "TechnoMagic",
                    Username = username,
                    Description = new List<object> {
                         new { Type = "Run", Text = "Сервер TechnoMagic", IsBold = true, FontSize = 26 },
                         new { Type = "LineBreak" },
                         new { Type = "LineBreak" },
                         new { Type = "Run", Text = "Сервер TechnoMagic основан на сборке Create Chronicles: Bosses and Beyond.", IsBold = true},
                         new { Type = "LineBreak" },
                         new { Type = "LineBreak" },
                         new { Type = "Run", Text = "Вас ждет проработанная система прогресса, состоящая из 10 глав. Она объединяет автоматизацию, исследование, магию и битвы с боссами. В вашем распоряжении более 800 квестов, которые постепенно помогут освоить развитие технологий и магических искусств.", IsBold = true },
                         new { Type = "LineBreak" },
                         new { Type = "LineBreak" },
                         new { Type = "Run", Text = "Изучайте мир с модом Create, стройте производственные линии и получайте доступ к новому контенту с каждой главой. Сражайтесь с боссами из L’EnderCataclysm, Mowzie’s Mobs и Bosses of Mass Destruction, развивая при этом собственное древо талантов. Посетите новые измерения – от Twilight Forest до The Aether и Blue Skies, исследуйте необычные пещеры из Alex’s Caves, приручайте драконов из Ice and Fire и отправляйтесь в космос с Ad Astra.", IsBold = true },
                         new { Type = "LineBreak" },
                         new { Type = "LineBreak" },
                         new { Type = "Run", Text = "Ждем вас на нашем сервере!", IsBold = true }
                   },
                    Config = new
                    {
                        server = "TechnoMagic",
                        version = techno.Version,
                        bootloaderType = techno.bootloaderType,
                        loader = techno.ForgeVersion,
                        clientLink = techno.ClientLink,
                        installerLink = techno.InstallLink,
                        manifestLink = techno.ManifestLink,
                        confLink = techno.ConfLink,
                        kubejsLink = techno.KubejsLink,
                        shadersLink = techno.ShadersLink,
                        versionExpectedChecksum = techno.VersionExpectedChecksum,
                        libraryExpectedChecksum = techno.LibraryExpectedChecksum,
                        assetsExpectedChecksum = techno.AssetsExpectedChecksum
                    }
                });
            }

            return Ok(servers);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetServers");
            return StatusCode(500, new { Error = "INTERNAL_ERROR" });
        }
    }
    [HttpGet("get_userdata")]
    public IActionResult GetUserData()
    {
        try
        {
            // Получаем ник из токена
            var username = User.FindFirstValue(ClaimTypes.Name) ?? "Player";

            // Формируем структуру новостей точно по вашему шаблону
            var news = new List<NewsPart>
        {
            // Заголовок "Новости"
            new NewsPart { Text = "Новости", IsBold = true, FontSize = 26, Color = "AliceBlue" },
            new NewsPart { Type = "LineBreak" },
            new NewsPart { Type = "LineBreak" },

            // Название сервера
            new NewsPart { Text = "TechnoMagic | ", IsBold = true, Color = "AliceBlue" },
            new NewsPart { Text = "Открытый бета-тест", IsBold = true, Color = "AliceBlue" },
            new NewsPart { Type = "LineBreak" },
            new NewsPart { Type = "LineBreak" },

            // Основной текст
            new NewsPart { Text = "Прошло немало времени с прошлого сезона, и за это время мы усердно трудились над новым проектом. Теперь рады сообщить — новый сезон открыт!", IsBold = true },
            new NewsPart { Type = "LineBreak" },
            new NewsPart { Type = "LineBreak" },

            new NewsPart { Text = "Это пока не финальная версия, а открытый тест, доступный всем игрокам. Мы не успели реализовать всё задуманное, но теперь будем продолжать развитие вместе с вами.", IsBold = true },
            new NewsPart { Type = "LineBreak" },
            new NewsPart { Type = "LineBreak" },

            new NewsPart { Text = "Играть можно уже сейчас!", IsBold = true },
            new NewsPart { Type = "LineBreak" },

            new NewsPart { Text = "Если у вас появятся идеи по улучшению, или вы обнаружите баги, проблемы или зависания, обязательно сообщите нам через Telegram-бота: ", IsBold = true },
            
            // Ссылка на бота
            new NewsPart { Type = "Link", Text = "@minecraft_urfu_underground_bot", Color = "#747474" },

            new NewsPart { Type = "LineBreak" },
            new NewsPart { Type = "LineBreak" },

            new NewsPart { Text = "Хорошей игры! ", IsBold = true }
        };
            var profileData = new List<NewsPart>
        {
            new NewsPart { Text = "Игровой профиль", IsBold = true, FontSize = 26, Color = "AliceBlue" },
            new NewsPart { Type = "LineBreak" },
            new NewsPart { Type = "LineBreak" },

            new NewsPart { Text = "Игровой профиль еще не до конца готов. Мы работаем над полным выводом вашей статистики. ", FontSize = 14, IsBold = true },
            new NewsPart { Text = "", Color = "#3C6", IsBold = true },
            new NewsPart { Type = "LineBreak" },
            new NewsPart { Type = "LineBreak" },

            new NewsPart { Text = "Никнейм: ", FontSize = 14, IsBold = true },
            new NewsPart { Text = username, Color = "#3C6", IsBold = true }, 
            new NewsPart { Type = "LineBreak" },

            new NewsPart { Text = "Игровое время: ", FontSize = 14, IsBold = true },
            new NewsPart { Text = "нет данных", Color = "#3C6" },
            new NewsPart { Type = "LineBreak" },

            new NewsPart { Text = "Убийств: ", FontSize = 14, IsBold = true },
            new NewsPart { Text = "нет данных", Color = "#3C6" }, 
            new NewsPart { Type = "LineBreak" },

            new NewsPart { Text = "Смертей: ", FontSize = 14, IsBold = true },
            new NewsPart { Text = "нет данных", Color = "#3C6" },
            new NewsPart { Type = "LineBreak" },

            new NewsPart { Text = "Статус: ", FontSize = 14, IsBold = true },
            new NewsPart { Text = "нет данных", Color = "#3C6" }
        };

            return Ok(new
            {
                username,
                news,
                profileData
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetUserData");
            return StatusCode(500, new { Error = "INTERNAL_ERROR" });
        }
    }
}