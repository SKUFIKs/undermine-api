using API_UnderMine_v0_1.Config;
using API_UnderMine_v0_1.Controllers;

namespace API_UnderMine_v0_1.Migrations
{
    public class ServerManager
    {
        private readonly Dictionary<string, ServerInfo> _servers = new();

        public ServerManager()
        {
            InitializeServers();
        }

        private void InitializeServers()
        {
            var output = OutputSetting.Instance;

            // Сервер 1
            _servers[output.SERVER_ID_SERVER_1] = new ServerInfo
            {
                ServerId = output.SERVER_ID_SERVER_1,
                MinecraftVersion = output.MinecraftVersionServer1,
                ForgeVersion = output.ForgeVersionServer1,
                ManifestUrl = output.BASE_URL_SERVER_1 + output.KEY_URL_SERVER_1,
                Status = "online",
                LastUpdated = DateTime.UtcNow
            };

            _servers[output.SERVER_ID_SERVER_2] = new ServerInfo
            {
                ServerId = output.SERVER_ID_SERVER_2,
                MinecraftVersion = output.MinecraftVersionServer2,
                ForgeVersion = output.ForgeVersionServer2,
                ManifestUrl = output.BASE_URL_SERVER_2 + output.KEY_URL_SERVER_2,

            };

            _servers[output.SERVER_ID_SERVER_3] = new ServerInfo
            {
                ServerId = output.SERVER_ID_SERVER_3,
                MinecraftVersion = output.MinecraftVersionServer3,
                ForgeVersion = output.ForgeVersionServer3,
                ManifestUrl = output.BASE_URL_SERVER_3 + output.KEY_URL_SERVER_3,
                Status = "online",
            };
        }

        public ServerInfo[] GetAllServers() => _servers.Values.ToArray();

        public ServerInfo? GetServerById(string serverId) =>
            _servers.ContainsKey(serverId) ? _servers[serverId] : null;

        public bool ServerExists(string serverId) => _servers.ContainsKey(serverId);

        public string[] GetAvailableServerIds() => _servers.Keys.ToArray();
    }
}
