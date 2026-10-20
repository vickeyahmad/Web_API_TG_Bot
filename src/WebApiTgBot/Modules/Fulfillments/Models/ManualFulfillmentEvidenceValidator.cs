using System.Text.RegularExpressions;

namespace WebApiTgBot.Modules.Fulfillments.Models;

/// <summary>
/// Operational safety validator for administrative manual fulfillment delivery evidence.
/// Enforces approved channels, structured non-sensitive reference formats, and strictly blocks
/// plaintext credentials, passwords, tokens, or URLs from being persisted into notes.
/// </summary>
public static class ManualFulfillmentEvidenceValidator
{
    /// <summary>
    /// Documented, approved delivery channels for controlled manual fulfillment.
    /// Restricts evidence to authorized business contact mechanisms.
    /// </summary>
    public static readonly HashSet<string> ApprovedChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "EmailSupport",
        "WhatsAppDirect",
        "TelegramManual",
        "InternalPortal"
    };

    /// <summary>
    /// Approved reference prefixes for controlled manual fulfillment.
    /// External delivery references must start with one of these controlled prefixes.
    /// </summary>
    public static readonly string[] ApprovedReferencePrefixes =
    {
        "TICKET-",
        "DISPATCH-",
        "MSG-",
        "PORTAL-",
        "REF-"
    };

    /// <summary>
    /// Explicit curated set of safe operational notes permitted during controlled pilot.
    /// Free-text entry is strictly prohibited to guarantee credentials or secrets cannot be stored.
    /// </summary>
    public static readonly HashSet<string> ApprovedOperationalNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Standard delivery completed.",
        "Delivered via authorized customer channel.",
        "Delivered via authorized customer care agent.",
        "Customer confirmed receipt.",
        "Account access transferred directly to customer.",
        "Manual verification completed.",
        "Replacement delivery issued.",
        "Audit verification complete.",
        "Standard authorized dispatch procedure completed."
    };

    /// <summary>
    /// Pattern enforcing structured reference identifiers (e.g. TICKET-12345, EXT_REF_99, DISPATCH-2026-10).
    /// Disallows spaces, quotation marks, and arbitrary special symbols.
    /// </summary>
    private static readonly Regex ReferenceFormatRegex = new(
        @"^[A-Za-z0-9][A-Za-z0-9_.:#-]{2,99}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Ambiguous, generic, or dummy placeholders that do not represent valid external delivery evidence.
    /// </summary>
    private static readonly HashSet<string> AmbiguousPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "N/A", "NA", "NONE", "NULL", "0000", "1111", "1234", "TEST", "DEMO", "SAMPLE", "DELIVERED", "DONE", "OK", "COMPLETED"
    };

    /// <summary>
    /// Regex scanning for common URLs that might leak magic links, reset links, or invitation secrets.
    /// </summary>
    private static readonly Regex UrlRegex = new(
        @"(https?:\/\/|ftp:\/\/|www\.)\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Secret keywords and credential parameter assignments strictly prohibited from operational notes.
    /// </summary>
    private static readonly string[] ForbiddenSecretKeywords =
    {
        "password", "passwd", "pwd", "secret", "token", "bearer",
        "api_key", "apikey", "otp", "license_key", "licensekey",
        "private_key", "credentials", "auth:"
    };

    /// <summary>
    /// Validates and normalizes an external delivery reference.
    /// </summary>
    public static (bool IsValid, string? ErrorMessage, string Normalized) ValidateReference(string? reference)
    {
        var trimmed = reference?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return (false, "A valid external delivery reference or support ticket identifier between 3 and 100 characters is required.", string.Empty);
        }

        if (trimmed.Contains('<') || trimmed.Contains('>'))
        {
            return (false, "DeliveryReference contains invalid characters.", string.Empty);
        }

        if (AmbiguousPlaceholders.Contains(trimmed))
        {
            return (false, "Ambiguous placeholders (e.g. 'N/A', 'TEST', '0000', 'NONE') are not permitted as delivery references.", string.Empty);
        }

        var hasValidPrefix = ApprovedReferencePrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (!hasValidPrefix)
        {
            return (false, $"DeliveryReference must begin with an approved controlled prefix ({string.Join(", ", ApprovedReferencePrefixes)}).", string.Empty);
        }

        if (trimmed.Length < 5 || trimmed.Length > 100)
        {
            return (false, "A valid external delivery reference between 5 and 100 characters is required.", string.Empty);
        }

        if (!ReferenceFormatRegex.IsMatch(trimmed))
        {
            return (false, "DeliveryReference must be a structured identifier (3-100 alphanumeric characters, hyphens, colons, dots, or underscores without spaces).", string.Empty);
        }

        return (true, null, trimmed);
    }

    /// <summary>
    /// Validates that the delivery channel belongs to the approved pilot channel set.
    /// </summary>
    public static (bool IsValid, string? ErrorMessage, string Normalized) ValidateChannel(string? channel)
    {
        var trimmed = channel?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < 2 || trimmed.Length > 50)
        {
            return (false, "A valid non-sensitive delivery channel identifier between 2 and 50 characters is required.", string.Empty);
        }

        if (trimmed.Contains('<') || trimmed.Contains('>'))
        {
            return (false, "DeliveryChannel contains invalid characters.", string.Empty);
        }

        // Match against approved channels
        var approved = ApprovedChannels.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
        if (approved == null)
        {
            return (false, $"Invalid delivery channel. Approved channels are: {string.Join(", ", ApprovedChannels)}.", string.Empty);
        }

        return (true, null, approved);
    }

    /// <summary>
    /// Validates operational notes to guarantee they are strictly administrative comments
    /// and do not contain plaintext credentials, passwords, tokens, API keys, or URLs.
    /// </summary>
    public static (bool IsValid, string? ErrorMessage, string? Normalized) ValidateNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return (true, null, null);
        }

        var trimmed = notes.Trim();

        if (trimmed.Length > 200)
        {
            return (false, "Notes cannot exceed 200 characters.", null);
        }

        if (trimmed.Contains('<') || trimmed.Contains('>'))
        {
            return (false, "Notes contain invalid characters. Providing HTML tags or markup is strictly prohibited.", null);
        }

        if (UrlRegex.IsMatch(trimmed))
        {
            return (false, "Notes must contain only safe, non-sensitive operational comments. Providing URLs is strictly prohibited.", null);
        }

        // Check for forbidden secret keywords
        foreach (var keyword in ForbiddenSecretKeywords)
        {
            if (Regex.IsMatch(trimmed, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || (keyword.EndsWith(':') && trimmed.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                || trimmed.IndexOf("key=", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("secret=", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("token=", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("pwd=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (false, "Notes must contain only safe, non-sensitive operational comments. Providing credentials, passwords, tokens, API keys, or URLs is strictly prohibited.", null);
            }
        }

        // Match against approved operational notes set for pilot safety
        var approvedNote = ApprovedOperationalNotes.FirstOrDefault(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase));
        if (approvedNote == null)
        {
            return (false, "Arbitrary free-text notes are prohibited during controlled pilot. Notes must be omitted or selected from approved operational options.", null);
        }

        return (true, null, approvedNote);
    }
}
