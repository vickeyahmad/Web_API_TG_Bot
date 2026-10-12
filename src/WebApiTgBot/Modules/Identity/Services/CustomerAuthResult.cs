using WebApiTgBot.Modules.Identity.Models.DTOs;

namespace WebApiTgBot.Modules.Identity.Services;

/// <summary>
/// Status codes representing customer authentication and registration outcomes.
/// </summary>
public enum CustomerAuthStatus
{
    Success,
    InvalidCredentials,
    InactiveAccount,
    LockedOut,
    DuplicateEmail,
    ValidationError,
    NotACustomer
}

/// <summary>
/// Internal result object returned by ICustomerAuthService operations.
/// </summary>
public class CustomerAuthResult
{
    public CustomerAuthStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public CustomerUserDto? User { get; init; }
    public IEnumerable<string>? Errors { get; init; }

    public static CustomerAuthResult Ok(CustomerUserDto user, string message = "Success.") =>
        new() { Status = CustomerAuthStatus.Success, User = user, Message = message };

    public static CustomerAuthResult InvalidCredentials(string message = "Invalid email or password.") =>
        new() { Status = CustomerAuthStatus.InvalidCredentials, Message = message };

    public static CustomerAuthResult Inactive(string message = "Account is disabled. Please contact customer support.") =>
        new() { Status = CustomerAuthStatus.InactiveAccount, Message = message };

    public static CustomerAuthResult LockedOut(string message = "Account is temporarily locked out due to multiple failed login attempts. Please try again later.") =>
        new() { Status = CustomerAuthStatus.LockedOut, Message = message };

    public static CustomerAuthResult DuplicateEmail(string message = "An account with this email address already exists.") =>
        new() { Status = CustomerAuthStatus.DuplicateEmail, Message = message };

    public static CustomerAuthResult ValidationError(IEnumerable<string> errors, string message = "Validation failed.") =>
        new() { Status = CustomerAuthStatus.ValidationError, Errors = errors, Message = message };

    public static CustomerAuthResult NotACustomer(string message = "Invalid email or password.") =>
        new() { Status = CustomerAuthStatus.NotACustomer, Message = message };
}
