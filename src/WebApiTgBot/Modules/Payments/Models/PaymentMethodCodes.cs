namespace WebApiTgBot.Modules.Payments.Models;

/// <summary>
/// Standardized identifiers for payment methods supported or planned on Gogaabyte's reseller platform.
/// Decouples payment attempt records from specific vendor gateway implementations.
/// </summary>
public static class PaymentMethodCodes
{
    public const string JazzCash = "JAZZCASH";
    public const string Easypaisa = "EASYPAISA";
    public const string NayaPay = "NAYAPAY";
    public const string SadaPay = "SADAPAY";
    public const string BankTransfer = "BANK_TRANSFER";
    public const string Wallet = "WALLET";
    public const string CryptoBinance = "CRYPTO_BINANCE";
    public const string CryptoUsdt = "CRYPTO_USDT";

    private static readonly HashSet<string> AllCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        JazzCash,
        Easypaisa,
        NayaPay,
        SadaPay,
        BankTransfer,
        Wallet,
        CryptoBinance,
        CryptoUsdt
    };

    public static bool IsValid(string? code) =>
        !string.IsNullOrWhiteSpace(code) && AllCodes.Contains(code.Trim());

    public static string Normalize(string code) =>
        code.Trim().ToUpperInvariant();
}
