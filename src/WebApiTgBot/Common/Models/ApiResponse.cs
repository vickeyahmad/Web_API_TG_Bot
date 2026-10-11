namespace WebApiTgBot.Common.Models;

/// <summary>
/// Standard response wrapper used by all API endpoints.
/// Provides a consistent shape for every response the API returns.
/// </summary>
/// <typeparam name="T">The type of the response data payload.</typeparam>
public class ApiResponse<T>
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>A human-readable message describing the result.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The data returned by the operation. Null on failure.</summary>
    public T? Data { get; set; }

    public static ApiResponse<T> Ok(T data, string message = "Success") =>
        new() { Success = true, Message = message, Data = data };

    public static ApiResponse<T> Fail(string message) =>
        new() { Success = false, Message = message, Data = default };
}
