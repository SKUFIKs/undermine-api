using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnderMineApi.Controllers;
using UnderMineApi.Data;
using UnderMineApi.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Linq; // Не забудь добавить для .Count()

public class AdminControllerTests
{
    private AppDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task GetAllUsers_ReturnsListOfUsers()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        // Используем long для Id (L)
        context.McAuthAccounts.Add(new McAuthAccount { Id = 1L, RealName = "User1", Role = "User" });
        context.McAuthAccounts.Add(new McAuthAccount { Id = 2L, RealName = "User2", Role = "Admin" });
        await context.SaveChangesAsync();

        var mockLogger = new Mock<ILogger<AdminController>>();
        var controller = new AdminController(context, mockLogger.Object);

        // Act
        var result = await controller.GetAllUsers();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);

        // Вместо List<object> используем IEnumerable
        var users = Assert.IsAssignableFrom<System.Collections.IEnumerable>(okResult.Value);

        // Проверка на количество элементов через LINQ
        var count = 0;
        foreach (var u in users) count++;
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task BanUser_ReturnsNotFound_WhenUserDoesNotExist()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        var mockLogger = new Mock<ILogger<AdminController>>();
        var controller = new AdminController(context, mockLogger.Object);

        // Act - передаем 999L (long)
        var result = await controller.BanUser(999L);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task BanUser_ReturnsBadRequest_WhenTryingToBanAdmin()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        context.McAuthAccounts.Add(new McAuthAccount { Id = 1L, RealName = "AdminGuy", Role = "Admin", IsBanned = false });
        await context.SaveChangesAsync();

        var mockLogger = new Mock<ILogger<AdminController>>();
        var controller = new AdminController(context, mockLogger.Object);

        // Act - 1L
        var result = await controller.BanUser(1L);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Нельзя забанить администратора!", badRequest.Value);
    }

    [Fact]
    public async Task BanUser_Success_BansUserAndResetsSession()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        context.McAuthAccounts.Add(new McAuthAccount { Id = 10L, RealName = "BadPlayer", Role = "User", IsBanned = false, HasSession = 1 });
        await context.SaveChangesAsync();

        var mockLogger = new Mock<ILogger<AdminController>>();
        var controller = new AdminController(context, mockLogger.Object);

        // Act
        var result = await controller.BanUser(10L);

        // Assert
        Assert.IsType<OkObjectResult>(result);

        // FindAsync теперь получает long (10L), что совпадает с типом в БД
        var userInDb = await context.McAuthAccounts.FindAsync(10L);
        Assert.NotNull(userInDb);
        Assert.True(userInDb.IsBanned);
        Assert.Equal(0, userInDb.HasSession);
    }

    [Fact]
    public async Task PromoteToAdmin_Success_UpdatesRole()
    {
        // Arrange
        using var context = GetInMemoryDbContext();
        context.McAuthAccounts.Add(new McAuthAccount { Id = 5L, RealName = "Player", Role = "User" });
        await context.SaveChangesAsync();

        var mockLogger = new Mock<ILogger<AdminController>>();
        var controller = new AdminController(context, mockLogger.Object);

        // Act
        var result = await controller.PromoteToAdmin(5L);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        var userInDb = await context.McAuthAccounts.FindAsync(5L);
        Assert.Equal("Admin", userInDb.Role);
    }
}