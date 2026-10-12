namespace WebApiTgBot.Modules.Catalog.Services;

/// <summary>
/// Status codes representing the outcome of an administrative catalog operation.
/// </summary>
public enum AdminCatalogResultStatus
{
    Success,
    NotFound,
    Conflict,
    BadRequest
}

/// <summary>
/// Result envelope for administrative catalog operations, conveying outcome status and data/error messages.
/// </summary>
/// <typeparam name="T">Type of data returned on success.</typeparam>
public class AdminCatalogResult<T>
{
    public AdminCatalogResultStatus Status { get; init; }
    public T? Data { get; init; }
    public string Message { get; init; } = string.Empty;

    public bool IsSuccess => Status == AdminCatalogResultStatus.Success;

    public static AdminCatalogResult<T> Ok(T data, string message = "Operation completed successfully.") =>
        new() { Status = AdminCatalogResultStatus.Success, Data = data, Message = message };

    public static AdminCatalogResult<T> NotFound(string message) =>
        new() { Status = AdminCatalogResultStatus.NotFound, Message = message };

    public static AdminCatalogResult<T> Conflict(string message) =>
        new() { Status = AdminCatalogResultStatus.Conflict, Message = message };

    public static AdminCatalogResult<T> BadRequest(string message) =>
        new() { Status = AdminCatalogResultStatus.BadRequest, Message = message };
}
