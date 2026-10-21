#pragma warning disable CA1416, CA1422, CS0618, CS1570, CS8600, CS8602, CS8604, SYSLIB0014, IL2026, IL3050
using System;

namespace AiSmartEmailer.Models;

/// <summary>
/// Holds all user-configurable settings for the AI Smart Emailer application.
/// </summary>
public class AppConfig
{
    public string FromEmail { get; set; } = string.Empty;
    public string ToEmail { get; set; } = string.Empty;
    public string CcEmail { get; set; } = string.Empty;

    public string EmailBody { get; set; } =
        "Please find attached the document regarding \"{Subject}\" for your review and necessary action.";

    public string ComplementaryClose { get; set; } =
        "Thanks and Regards\nCEO OFFICE GANDERBAL";

    public string AiProvider { get; set; } = "Gemini";
    public string ApiKey { get; set; } = string.Empty;
    public string AiModel { get; set; } = "gemini-2.5-flash";

    public string CustomOcrPrompt { get; set; } =
        "Examine this document/image carefully using OCR. " +
        "1. Look for an explicit subject line (e.g., starting with 'Subject:', 'Sub:', 'Ref:', 'Re:', 'Regarding:', or 'विषय:'). " +
        "If found, return the COMPLETE, exact multi-line subject text (without the 'Subject:' label itself, and stopping before 'Sir,'/'Madam,' or the body text). " +
        "2. If the 'Subject:' label is missing or cut off, look at the subject block positioned between the 'To' recipient block/Date and the salutation ('Sir,'/'Madam,'), or read the primary order/circular/notice heading. " +
        "Return ONLY the final subject line as a single line of plain text with no quotes or markdown.";

    public bool AutoOpenGmailAfterOcr { get; set; } = true;
    public string OptionalSmtpAppPassword { get; set; } = string.Empty;

    /// <summary>
    /// Per-engine network timeout in seconds (prevents long loading hangs on weak mobile networks).
    /// Default: 12 seconds.
    /// </summary>
    public int OcrTimeoutSeconds { get; set; } = 12;

    /// <summary>
    /// Enables automatic contrast/sharpness preprocessing &amp; EXIF rotation correction before OCR.
    /// </summary>
    public bool EnableImageEnhancement { get; set; } = true;

    /// <summary>
    /// Caches extracted subject lines by SHA-256 file fingerprint for instant (&lt; 5ms) repeat shares.
    /// </summary>
    public bool EnableSubjectCache { get; set; } = true;
}

public class SubjectExtractionResult
{
    public bool Success { get; set; }
    public bool FoundDocumentSubject { get; set; }
    public bool FoundExplicitSubject { get; set; }
    public int ConfidenceScore { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string SubjectLine { get; set; } = string.Empty;
    public string EngineUsed { get; set; } = string.Empty;
    public string RawExtractedSnippet { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
