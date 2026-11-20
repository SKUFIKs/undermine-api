namespace UnderMineApi.Services.PasswordHasher
{
    public class BcryptPasswordHasher : IPasswordHasher
    {
        public string HashPassword(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password);
        }

        public bool VerifyPassword(string password, string hash)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash))
                return false;

            try
            {
                return BCrypt.Net.BCrypt.Verify(password, hash);
            }
            catch (Exception ex) when (ex is ArgumentException or BCrypt.Net.SaltParseException)
            {
                // Malformed hash or a password outside BCrypt's limits: treat as "does not match"
                // instead of turning a login attempt into a 500.
                return false;
            }
        }
    }
}
