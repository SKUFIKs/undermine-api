# Stage 1: Base image
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Stage 2: Build
FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Копируем проект и восстанавливаем зависимости
COPY ["API_UnderMine_v0_1.csproj", "./"]
RUN dotnet restore "./API_UnderMine_v0_1.csproj"

# Копируем все остальные файлы проекта
COPY . .

# Сборка проекта
RUN dotnet build "./API_UnderMine_v0_1.csproj" -c $BUILD_CONFIGURATION -o /app/build
RUN dotnet publish "./API_UnderMine_v0_1.csproj" -c $BUILD_CONFIGURATION -o /app/publish

# Stage 3: Final
FROM base AS final
WORKDIR /app

# Копируем сертификаты
COPY certs /app/certs

COPY --from=build /app/publish .

# Указываем переменные окружения для Kestrel
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_CERT_PASSWORD=MyPassword

# Порты для HTTP и HTTPS
EXPOSE 8080
EXPOSE 8081

ENTRYPOINT ["dotnet", "API_UnderMine_v0_1.dll"]

