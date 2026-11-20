namespace UnderMineApi.Services.PasswordHasher
{
    public interface IPasswordHasher
    {
        string HashPassword(string password);

        /// <summary>
        /// Verifies a password against a BCrypt hash. Never throws for malformed input/hashes;
        /// returns false instead.
        /// </summary>
        bool VerifyPassword(string password, string hash);
    }
}
