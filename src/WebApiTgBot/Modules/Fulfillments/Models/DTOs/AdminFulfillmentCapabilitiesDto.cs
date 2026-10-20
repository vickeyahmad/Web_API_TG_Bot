namespace WebApiTgBot.Modules.Fulfillments.Models.DTOs;

/// <summary>
/// Operational capabilities and feature availability for administrative fulfillment workflows.
/// Reflects server-side configuration and product eligibility policies without exposing secrets.
/// </summary>
public class AdminFulfillmentCapabilitiesDto
{
    /// <summary>
    /// Master switch status from server configuration (ManualFulfillment:Enabled).
    /// Defaults to false for production safety.
    /// </summary>
    public bool IsGloballyEnabled { get; set; }

    /// <summary>
    /// Indicates whether a restricted product allowlist policy is actively configured.
    /// In our fail-closed architecture, manual fulfillment is only permitted if this policy is configured
    /// with at least one approved product SKU.
    /// </summary>
    public bool HasProductAllowlistPolicy { get; set; }

    /// <summary>
    /// Count of approved product SKUs configured for manual fulfillment.
    /// Does not expose the internal SKUs to unapproved callers or customer surfaces.
    /// </summary>
    public int ApprovedProductCount { get; set; }

    /// <summary>
    /// Effective availability of manual fulfillment operations.
    /// True ONLY IF IsGloballyEnabled is true AND HasProductAllowlistPolicy is true (ApprovedProductCount > 0).
    /// </summary>
    public bool IsEffectivelyAvailable { get; set; }

    /// <summary>
    /// Human-readable explanation of effective operational status.
    /// Useful for administrator UI banners and diagnostic messages.
    /// </summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// If a product SKU was specified in the query, indicates whether that specific product is approved.
    /// Null when no product SKU was queried.
    /// </summary>
    public bool? IsProductApproved { get; set; }
}
