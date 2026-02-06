using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Threading.Tasks;
using UnderMineApi.Controllers;
using UnderMineApi.Data;
using UnderMineApi.DTOs;
using UnderMineApi.Models;
using UnderMineApi.Services;
using Xunit;

public class AuthControllerTests
{
    private AppDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    // Вспомогательный метод для настройки контроллера с HttpContext
    private AuthController CreateController(AppDbContext context, IJwtService jwtService = null)
    {
        var mockConfig = new Mock<IConfiguration>();
        var mockLogger = new Mock<ILogger<AuthController>>();
        var mockJwt = jwtService != null ? Mock.Get(jwtService) : new Mock<IJwtService>();

        var controller = new AuthController(context, mockConfig.Object, mockLogger.Object, mockJwt.Object);

        // Mock HttpContext для IP адреса
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    [Fact]
    public async Task Register_ReturnsConflict_IfUserExists()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        context.McAuthAccounts.Add(new McAuthAccount { Username = "existinguser" });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var request = new AuthRequestDto { Login = "ExistingUser", Password = "123", DeviceId = "d1" };

        // Act
        var result = await controller.Register(request);

        // Assert
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        // Проверка через рефлексию анонимного типа или dynamic
        dynamic val = conflict.Value;
        Assert.Equal(409, (int)val.GetType().GetProperty("status").GetValue(val, null));
    }

    [Fact]
    public async Task Register_Returns429_IfTooManyAccountsOnDevice()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        // Добавляем 2 аккаунта с тем же DeviceId
        context.McAuthAccounts.Add(new McAuthAccount { Username = "u1", DeviceId = "device_limit" });
        context.McAuthAccounts.Add(new McAuthAccount { Username = "u2", DeviceId = "device_limit" });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var request = new AuthRequestDto { Login = "NewUser", Password = "123", DeviceId = "device_limit" };

        // Act
        var result = await controller.Register(request);

        // Assert
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(429, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task Register_Success_CreatesUser()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var controller = CreateController(context);
        var request = new AuthRequestDto { Login = "NewUser", Password = "password123", DeviceId = "dev1" };

        // Act
        var result = await controller.Register(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        var user = await context.McAuthAccounts.FirstOrDefaultAsync(u => u.Username == "newuser");
        Assert.NotNull(user);
        Assert.True(BCrypt.Net.BCrypt.Verify("password123", user.Password)); // Проверяем хеш
    }

    [Fact]
    public async Task Login_ReturnsUnauthorized_IfUserNotFound()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var controller = CreateController(context);
        var request = new AuthRequestDto { Login = "NonExistent", Password = "123", DeviceId = "d" };

        // Act
        var result = await controller.Login(request);

        // Assert
        var unauth = Assert.IsType<UnauthorizedObjectResult>(result);
        dynamic val = unauth.Value;
        Assert.Equal("Пользователь не найден", (string)val.GetType().GetProperty("message").GetValue(val, null));
    }

    [Fact]
    public async Task Login_ReturnsUnauthorized_IfPasswordWrong()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        string hash = BCrypt.Net.BCrypt.HashPassword("correct_pass");
        context.McAuthAccounts.Add(new McAuthAccount { Username = "user", Password = hash });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var request = new AuthRequestDto { Login = "user", Password = "wrong_pass", DeviceId = "d" };

        // Act
        var result = await controller.Login(request);

        // Assert
        var unauth = Assert.IsType<UnauthorizedObjectResult>(result);
        dynamic val = unauth.Value;
        Assert.Equal("Неверный логин или пароль!", (string)val.GetType().GetProperty("message").GetValue(val, null));
    }

    [Fact]
    public async Task Login_Success_ReturnsToken()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        string hash = BCrypt.Net.BCrypt.HashPassword("pass");
        context.McAuthAccounts.Add(new McAuthAccount { Username = "user", RealName = "User", Password = hash, IsBanned = false });
        await context.SaveChangesAsync();

        var mockJwt = new Mock<IJwtService>();
        mockJwt.Setup(s => s.GenerateToken(It.IsAny<McAuthAccount>())).Returns("fake_token");

        var controller = CreateController(context, mockJwt.Object);
        var request = new AuthRequestDto { Login = "User", Password = "pass", DeviceId = "d" };

        // Act
        var result = await controller.Login(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<LoginResponseDto>(okResult.Value);
        Assert.Equal("fake_token", response.Token);
    }
}