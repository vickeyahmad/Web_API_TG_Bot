namespace WebApiTgBot.Modules.Payments.Services;

/// <summary>
/// Interface for generating standardized, collision-resistant payment reference numbers.
/// </summary>
public interface IPaymentNumberGenerator
{
    string GeneratePaymentNumber(DateTime? timestamp = null);
}
