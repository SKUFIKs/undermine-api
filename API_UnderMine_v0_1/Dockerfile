FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble AS base
WORKDIR /app
EXPOSE 8080    # только HTTP

FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["API_UnderMine_v0_1.csproj", "./"]
RUN dotnet restore "./API_UnderMine_v0_1.csproj"

COPY . .
RUN dotnet publish "./API_UnderMine_v0_1.csproj" -c $BUILD_CONFIGURATION -o /app/publish

FROM base AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV DOTNET_USE_POLLING_FILE_WATCHER=1
ENV DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["dotnet", "API_UnderMine_v0_1.dll"]
