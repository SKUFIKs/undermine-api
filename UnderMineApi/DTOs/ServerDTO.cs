namespace UnderMineApi.DTOs;

public record ServerConfig(
    string LoaderVersion);

public record ServerList(
    string Id,
    string ServerName,
    string GameVersion,
    string LoaderType,
    string IPAddress,
    ServerConfig? Config);

