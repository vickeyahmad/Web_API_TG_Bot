namespace WebApiTgBot.Modules.Fulfillments.Options;

/// <summary>
/// Server-side configuration options for administrator manual fulfillment workflows.
/// Disabled by default for production safety.
/// </summary>
public class ManualFulfillmentOptions
{
    public const string SectionName = "ManualFulfillment";

    /// <summary>
    /// Master toggle for administrator manual fulfillment workflows.
    /// Default is false for production safety.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Fail-closed allowlist of internal product SKUs specifically approved for controlled manual fulfillment.
    /// Default is empty, ensuring manual fulfillment is blocked even if Enabled is true,
    /// until specific products are explicitly configured.
    /// </summary>
    public List<string> ApprovedProductSkus { get; set; } = new();

    /// <summary>
    /// Checks whether a product SKU is on the approved manual fulfillment allowlist.
    /// Fail-closed: returns false if the allowlist is null or empty, or if the SKU is not found.
    /// Case-insensitive comparison.
    /// </summary>
    public bool IsProductApproved(string? productSku)
    {
        if (string.IsNullOrWhiteSpace(productSku) || ApprovedProductSkus == null || ApprovedProductSkus.Count == 0)
        {
            return false;
        }

        var normalized = productSku.Trim();
        return ApprovedProductSkus.Any(approved =>
            string.Equals(approved.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks whether manual fulfillment is effectively available.
    /// True ONLY IF the master switch is enabled AND the allowlist contains at least one approved SKU.
    /// </summary>
    public bool IsEffectivelyAvailable => Enabled && ApprovedProductSkus != null && ApprovedProductSkus.Count > 0;
}
