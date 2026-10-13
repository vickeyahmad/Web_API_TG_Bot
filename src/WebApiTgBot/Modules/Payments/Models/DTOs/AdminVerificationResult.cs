namespace WebApiTgBot.Modules.Payments.Models.DTOs;

public enum AdminVerificationResultStatus
{
    Success,
    BadRequest,
    Forbidden,
    NotFound,
    Conflict
}

public class AdminVerificationResult
{
    public AdminVerificationResultStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public AdminManualPaymentDto? Payment { get; set; }
    public ApproveManualPaymentResultDto? ApprovalResult { get; set; }

    public static AdminVerificationResult Approved(ApproveManualPaymentResultDto result, string message = "Payment approved and wallet credited successfully.") =>
        new() { Status = AdminVerificationResultStatus.Success, ApprovalResult = result, Payment = result.Payment, Message = message };

    public static AdminVerificationResult Rejected(AdminManualPaymentDto payment, string message = "Payment rejected.") =>
        new() { Status = AdminVerificationResultStatus.Success, Payment = payment, Message = message };

    public static AdminVerificationResult BadRequest(string message) =>
        new() { Status = AdminVerificationResultStatus.BadRequest, Message = message };

    public static AdminVerificationResult Forbidden(string message) =>
        new() { Status = AdminVerificationResultStatus.Forbidden, Message = message };

    public static AdminVerificationResult NotFound(string message) =>
        new() { Status = AdminVerificationResultStatus.NotFound, Message = message };

    public static AdminVerificationResult Conflict(string message) =>
        new() { Status = AdminVerificationResultStatus.Conflict, Message = message };
}
