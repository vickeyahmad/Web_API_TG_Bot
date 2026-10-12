namespace WebApiTgBot.Modules.Orders.Services;

/// <summary>
/// Contract for generating unique, human-readable order reference numbers.
/// </summary>
public interface IOrderNumberGenerator
{
    /// <summary>
    /// Generates a globally unique order number (e.g. "ORD-20261013-7F8A3B").
    /// </summary>
    string GenerateOrderNumber(DateTime? timestamp = null);
}
