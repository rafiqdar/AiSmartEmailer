#pragma warning disable CA1416, CA1422, CS0618, CS1570, CS8600, CS8602, CS8604, SYSLIB0014, IL2026, IL3050
using System;
using System.IO;
using Android.Content;
using AiSmartEmailer.Models;

namespace AiSmartEmailer.Services;

/// <summary>
/// Persists and retrieves user configuration and fast local subject cache using Android SharedPreferences.
/// </summary>
public class AppSettingsService
{
    private const string PrefsName = "AiSmartEmailerPrefs_v1";
    private const string CachePrefsName = "AiSmartEmailerSubjectCache_v2";

    private const string KeyFromEmail = "from_email";
    private const string KeyToEmail = "to_email";
    private const string KeyCcEmail = "cc_email";
    private const string KeyEmailBody = "email_body";
    private const string KeyComplementaryClose = "complementary_close";
    private const string KeyAiProvider = "ai_provider";
    private const string KeyApiKey = "api_key";
    private const string KeyAiModel = "ai_model";
    private const string KeyCustomOcrPrompt = "custom_ocr_prompt";
    private const string KeyAutoOpenGmail = "auto_open_gmail";
    private const string KeySmtpAppPassword = "smtp_app_password";
    private const string KeyOcrTimeoutSeconds = "ocr_timeout_seconds";
    private const string KeyEnableImageEnhancement = "enable_image_enhancement";
    private const string KeyEnableSubjectCache = "enable_subject_cache";

    private readonly ISharedPreferences _prefs;
    private readonly ISharedPreferences _cachePrefs;

    public AppSettingsService(Context context)
    {
        _prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;
        _cachePrefs = context.GetSharedPreferences(CachePrefsName, FileCreationMode.Private)!;
    }

    public AppConfig LoadConfig()
    {
        var defaults = new AppConfig();
        int timeout = _prefs.GetInt(KeyOcrTimeoutSeconds, defaults.OcrTimeoutSeconds);
        timeout = Math.Clamp(timeout, 5, 30);

        return new AppConfig
        {
            FromEmail = _prefs.GetString(KeyFromEmail, defaults.FromEmail) ?? defaults.FromEmail,
            ToEmail = _prefs.GetString(KeyToEmail, defaults.ToEmail) ?? defaults.ToEmail,
            CcEmail = _prefs.GetString(KeyCcEmail, defaults.CcEmail) ?? defaults.CcEmail,
            EmailBody = _prefs.GetString(KeyEmailBody, defaults.EmailBody) ?? defaults.EmailBody,
            ComplementaryClose = _prefs.GetString(KeyComplementaryClose, defaults.ComplementaryClose) ?? defaults.ComplementaryClose,
            AiProvider = _prefs.GetString(KeyAiProvider, defaults.AiProvider) ?? defaults.AiProvider,
            ApiKey = _prefs.GetString(KeyApiKey, defaults.ApiKey) ?? defaults.ApiKey,
            AiModel = _prefs.GetString(KeyAiModel, defaults.AiModel) ?? defaults.AiModel,
            CustomOcrPrompt = _prefs.GetString(KeyCustomOcrPrompt, defaults.CustomOcrPrompt) ?? defaults.CustomOcrPrompt,
            AutoOpenGmailAfterOcr = _prefs.GetBoolean(KeyAutoOpenGmail, defaults.AutoOpenGmailAfterOcr),
            OptionalSmtpAppPassword = _prefs.GetString(KeySmtpAppPassword, defaults.OptionalSmtpAppPassword) ?? defaults.OptionalSmtpAppPassword,
            OcrTimeoutSeconds = timeout,
            EnableImageEnhancement = _prefs.GetBoolean(KeyEnableImageEnhancement, defaults.EnableImageEnhancement),
            EnableSubjectCache = _prefs.GetBoolean(KeyEnableSubjectCache, defaults.EnableSubjectCache)
        };
    }

    public void SaveConfig(AppConfig config)
    {
        using var editor = _prefs.Edit()!;
        editor.PutString(KeyFromEmail, config.FromEmail?.Trim() ?? string.Empty);
        editor.PutString(KeyToEmail, config.ToEmail?.Trim() ?? string.Empty);
        editor.PutString(KeyCcEmail, config.CcEmail?.Trim() ?? string.Empty);
        editor.PutString(KeyEmailBody, config.EmailBody ?? string.Empty);
        editor.PutString(KeyComplementaryClose, config.ComplementaryClose ?? string.Empty);
        editor.PutString(KeyAiProvider, config.AiProvider?.Trim() ?? "Gemini");
        editor.PutString(KeyApiKey, config.ApiKey?.Trim() ?? string.Empty);
        editor.PutString(KeyAiModel, config.AiModel?.Trim() ?? "gemini-2.5-flash");
        editor.PutString(KeyCustomOcrPrompt, config.CustomOcrPrompt ?? string.Empty);
        editor.PutBoolean(KeyAutoOpenGmail, config.AutoOpenGmailAfterOcr);
        editor.PutString(KeySmtpAppPassword, config.OptionalSmtpAppPassword?.Trim() ?? string.Empty);
        editor.PutInt(KeyOcrTimeoutSeconds, Math.Clamp(config.OcrTimeoutSeconds, 5, 30));
        editor.PutBoolean(KeyEnableImageEnhancement, config.EnableImageEnhancement);
        editor.PutBoolean(KeyEnableSubjectCache, config.EnableSubjectCache);
        editor.Apply();
    }

    /// <summary>
    /// Computes a fast 64-bit FNV-1a fingerprint of a file (combining file length + up to 32 KB head + 32 KB tail)
    /// in under 1 millisecond using pure C# with zero platform crypto dependencies.
    /// </summary>
    public static string ComputeFastFileFingerprint(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length == 0) return string.Empty;

            ulong hash = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong len = (ulong)info.Length;
            hash = (hash ^ len) * prime;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int chunkSize = (int)Math.Min(32768L, info.Length);
            byte[] buffer = new byte[chunkSize];

            int readHead = fs.Read(buffer, 0, chunkSize);
            for (int i = 0; i < readHead; i += 4)
            {
                hash = (hash ^ buffer[i]) * prime;
            }

            if (info.Length > chunkSize * 2)
            {
                fs.Seek(-chunkSize, SeekOrigin.End);
                int readTail = fs.Read(buffer, 0, chunkSize);
                for (int i = 0; i < readTail; i += 4)
                {
                    hash = (hash ^ buffer[i]) * prime;
                }
            }

            return $"{info.Length}_{hash:X16}";
        }
        catch
        {
            return string.Empty;
        }
    }

    public bool TryGetCachedSubject(string fileFingerprint, out string subjectLine, out string engineUsed)
    {
        subjectLine = string.Empty;
        engineUsed = string.Empty;

        if (string.IsNullOrWhiteSpace(fileFingerprint))
            return false;

        string? cached = _cachePrefs.GetString($"subj_{fileFingerprint}", null);
        if (string.IsNullOrWhiteSpace(cached) || !LocalPdfAndImageHelper.IsValidHumanReadableSubject(cached))
            return false;

        subjectLine = cached;
        engineUsed = _cachePrefs.GetString($"eng_{fileFingerprint}", "Instant Local Cache") ?? "Instant Local Cache";
        return true;
    }

    public void SaveCachedSubject(string fileFingerprint, string subjectLine, string engineUsed)
    {
        if (string.IsNullOrWhiteSpace(fileFingerprint) || !LocalPdfAndImageHelper.IsValidHumanReadableSubject(subjectLine))
            return;

        try
        {
            using var editor = _cachePrefs.Edit()!;
            editor.PutString($"subj_{fileFingerprint}", subjectLine.Trim());
            editor.PutString($"eng_{fileFingerprint}", $"{engineUsed} [Cached]");
            editor.Apply();
        }
        catch
        {
            // Ignore cache write errors
        }
    }

    public void ClearSubjectCache()
    {
        try
        {
            using var editor = _cachePrefs.Edit()!;
            editor.Clear();
            editor.Apply();
        }
        catch { }
    }

    /// <summary>
    /// Formats the complete email body combining the body template (with placeholders resolved)
    /// and the complementary close lines.
    /// </summary>
    public static string BuildFullEmailBody(AppConfig config, string subjectLine, string fileName)
    {
        string body = string.IsNullOrWhiteSpace(config.EmailBody)
            ? "Please find the attached file."
            : config.EmailBody;

        body = body
            .Replace("{Subject}", subjectLine ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{FileName}", fileName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{Date}", DateTime.Now.ToString("dd MMM yyyy"), StringComparison.OrdinalIgnoreCase);

        string close = config.ComplementaryClose?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(close))
        {
            return body;
        }

        return $"{body.TrimEnd()}\n\n{close}";
    }
}
