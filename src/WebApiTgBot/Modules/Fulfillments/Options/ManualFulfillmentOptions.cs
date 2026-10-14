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
}
