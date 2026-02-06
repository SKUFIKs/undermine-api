using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using UnderMineApi.Controllers;
using UnderMineApi.Services;
using UnderMineApi.Models; // Для классов Config

public class ReceivingServerDataTests
{
    // Хелпер для создания контроллера с авторизованным пользователем
    private ReceivingServerData CreateControllerWithUser(ConfigCacheService cacheService, string username)
    {
        var mockLogger = new Mock<ILogger<ReceivingServerData>>();
        var controller = new ReceivingServerData(mockLogger.Object, cacheService);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, username)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return controller;
    }

    [Fact]
    public void GetData_Returns503_WhenConfigNotLoaded()
    {
        // Arrange
        var mockCache = new Mock<ConfigCacheService>();
        mockCache.Setup(c => c.CachedCore).Returns((CoreData)null);

        var controller = CreateControllerWithUser(mockCache.Object, "TestPlayer");

        // Act
        var result = controller.GetData();

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, statusCodeResult.StatusCode);
    }

    [Fact]
    public void GetData_ReturnsOk_WhenConfigLoaded()
    {
        // Arrange
        var mockCache = new Mock<ConfigCacheService>();
        var testConfig = new CoreData { Version = "1.16.5", ForgeVersion = "36.2.0" };
        mockCache.Setup(c => c.CachedCore).Returns(testConfig);

        var controller = CreateControllerWithUser(mockCache.Object, "TestPlayer");

        // Act
        var result = controller.GetData();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        // Можно проверить поля через dynamic или reflection
        dynamic val = okResult.Value;
        Assert.Equal("1.16.5", (string)val.GetType().GetProperty("versionMC1").GetValue(val, null));
        Assert.Equal("TestPlayer", (string)val.GetType().GetProperty("username").GetValue(val, null));
    }

    [Fact]
    public void GetServers_ReturnsList_BasedOnFlags()
    {
        // Arrange
        var mockCache = new Mock<ConfigCacheService>();

        // Настраиваем, что конфиги загружены
        mockCache.Setup(c => c.CachedVanila).Returns(new VanilaConfig { Version = "1.0" });
        mockCache.Setup(c => c.CachedTechno).Returns(new TechnoConfig { Version = "2.0" });

        // Включаем только Ванилу
        mockCache.Setup(c => c.IsVanilaEnabled).Returns(true);
        mockCache.Setup(c => c.IsTechnoEnabled).Returns(false);

        var controller = CreateControllerWithUser(mockCache.Object, "Player1");

        // Act
        var result = controller.GetServers();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsType<List<object>>(okResult.Value);
        Assert.Single(list); // Должен быть только 1 сервер (Ванила)
    }

    [Fact]
    public void GetUserData_ReturnsProfileInfo()
    {
        // Arrange
        var mockCache = new Mock<ConfigCacheService>(); // Кеш тут не используется, но нужен для конструктора
        var controller = CreateControllerWithUser(mockCache.Object, "SuperPlayer");

        // Act
        var result = controller.GetUserData();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic val = okResult.Value;
        Assert.Equal("SuperPlayer", (string)val.GetType().GetProperty("username").GetValue(val, null));
    }
}