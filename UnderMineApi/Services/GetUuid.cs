using System.Security.Cryptography;
using System.Text;

namespace UnderMineApi.Services
{
    public interface GetUuid
    {
        public static Guid GetOfflineUuid(string username)
        {
            string input = "OfflinePlayer:" + username;

            byte[] byteArray = System.Text.Encoding.UTF8.GetBytes(input);

            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(byteArray);
                
                hash[6]  = 0x0f;
                hash[6] |= 0x30;

                hash[8] &= 0x3f;
                hash[8] |= 0x80;

                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(hash, 0, 4);
                    Array.Reverse(hash, 4, 2);
                    Array.Reverse(hash, 6, 2);
                }

                return new Guid(hash);
            }
        }
    }
}
