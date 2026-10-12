using System.Security.Cryptography;

namespace WebApiTgBot.Modules.Orders.Services;

/// <summary>
/// Generates standardized, collision-resistant order numbers formatted as "ORD-yyyyMMdd-XXXXXX".
/// Uses cryptographically secure random alphanumeric characters.
/// </summary>
public class OrderNumberGenerator : IOrderNumberGenerator
{
    private const string AllowedChars = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"; // excludes ambiguous characters like 0, O, 1, I

    public string GenerateOrderNumber(DateTime? timestamp = null)
    {
        var datePart = (timestamp ?? DateTime.UtcNow).ToString("yyyyMMdd");
        Span<byte> randomBytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(randomBytes);

        Span<char> suffix = stackalloc char[6];
        for (int i = 0; i < 6; i++)
        {
            suffix[i] = AllowedChars[randomBytes[i] % AllowedChars.Length];
        }

        return $"ORD-{datePart}-{suffix}";
    }
}
