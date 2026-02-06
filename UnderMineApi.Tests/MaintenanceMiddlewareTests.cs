using Xunit;
using Microsoft.AspNetCore.Http;
using Moq;
using UnderMineApi.Controllers;
using UnderMineApi.Services; // Предполагается namespace сервиса
using System.Threading.Tasks;
using System.IO;

public class MaintenanceMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_Returns503_WhenMaintenanceIsOn()
    {
        // Arrange
        var mockNext = new Mock<RequestDelegate>();
        var middleware = new MaintenanceMiddleware(mockNext.Object);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream(); // Чтобы можно было записать ответ

        // Мокаем сервис. Если класс не позволяет мокать (нет virtual), 
        // создайте реальный объект и установите свойство IsMaintenance = true
        var mockCache = new Mock<ConfigCacheService>();
        mockCache.Setup(c => c.IsMaintenance).Returns(true);

        // Act
        await middleware.InvokeAsync(context, mockCache.Object);

        // Assert
        Assert.Equal(503, context.Response.StatusCode);
        mockNext.Verify(next => next(context), Times.Never); // Следующий middleware не должен вызываться
    }

    [Fact]
    public async Task InvokeAsync_CallsNext_WhenMaintenanceIsOff()
    {
        // Arrange
        var mockNext = new Mock<RequestDelegate>();
        var middleware = new MaintenanceMiddleware(mockNext.Object);
        var context = new DefaultHttpContext();

        var mockCache = new Mock<ConfigCacheService>();
        mockCache.Setup(c => c.IsMaintenance).Returns(false);

        // Act
        await middleware.InvokeAsync(context, mockCache.Object);

        // Assert
        Assert.Equal(200, context.Response.StatusCode); // По умолчанию 200
        mockNext.Verify(next => next(context), Times.Once);
    }
}