using static System.Net.WebRequestMethods;

namespace API_UnderMine_v0_1.Config
{
    public class OutputSetting
    {
        public static OutputSetting Instance { get; } = new OutputSetting();
        

        /// <summary>
        ///  SERVER 1
        /// </summary>
  
        public string BASE_URL_SERVER_1 { get; set; } = "https://www.dropbox.com/scl/fi/";
        public string KEY_URL_SERVER_1 { get; set; } = "xrxzg95onmleug3aylkkp/manifest.json?rlkey=7k7cs5j3lfhn3c4bu4lwh0woc&st=rw896grm&dl=1";
        public string SERVER_ID_SERVER_1 { get; set; } = "SERVER_1";
        public string MinecraftVersionServer1 { get; set; } = "1.20.1";
        public string ForgeVersionServer1 { get; set; } = "47.4.6";

        /// <summary>
        ///  SERVER 2
        /// </summary>
        
        public string BASE_URL_SERVER_2 { get; set; } = "https://www.dropbox.com/scl/fi/";
        public string KEY_URL_SERVER_2 { get; set; } = "xrxzg95onmleug3aylkkp/manifest.json?rlkey=7k7cs5j3lfhn3c4bu4lwh0woc&st=rw896grm&dl=1";
        public string SERVER_ID_SERVER_2 { get; set; } = "SERVER_2";
        public string MinecraftVersionServer2 { get; set; } = "1.20.1";
        public string ForgeVersionServer2 { get; set; } = "47.4.6";

        /// <summary>
        ///  SERVER 3
        /// </summary>

        public string BASE_URL_SERVER_3 { get; set; } = "https://www.dropbox.com/scl/fi/";
        public string KEY_URL_SERVER_3 { get; set; } = "xrxzg95onmleug3aylkkp/manifest.json?rlkey=7k7cs5j3lfhn3c4bu4lwh0woc&st=rw896grm&dl=1";
        public string SERVER_ID_SERVER_3 { get; set; } = "SERVER_3";
        public string MinecraftVersionServer3 { get; set; } = "1.20.1";
        public string ForgeVersionServer3 { get; set; } = "47.4.6";

        private OutputSetting() { }
    }

}
