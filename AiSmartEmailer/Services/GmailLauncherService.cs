#pragma warning disable CA1416, CA1422, CS0618, CS1570, CS8600, CS8602, CS8604, SYSLIB0014, IL2026, IL3050
using System;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Android.Content;
using Android.Widget;
using AiSmartEmailer.Models;

namespace AiSmartEmailer.Services;

/// <summary>
/// Launches the Gmail Android Application with prefilled From account hint, To account(s),
/// AI/OCR extracted Subject line, prefilled Body + Complementary Close, and attached PDF/Image file.
/// </summary>
public static class GmailLauncherService
{
    public const string GmailPackageName = "com.google.android.gm";

    /// <summary>
    /// Opens the Gmail app composer prefilled with all configured fields and the shared attachment,
    /// leaving only the final "Send" button tap for the user.
    /// </summary>
    public static void LaunchGmailWithAttachment(
        Context context,
        string fromEmail,
        string toEmailsRaw,
        string ccEmailsRaw,
        string subjectLine,
        string fullBodyText,
        Android.Net.Uri shareableAttachmentUri,
        string mimeType,
        string fileName)
    {
        string[] toEmails = SplitEmails(toEmailsRaw);
        string[] ccEmails = SplitEmails(ccEmailsRaw);

        var intent = new Intent(Intent.ActionSend);
        intent.SetType(string.IsNullOrWhiteSpace(mimeType) ? "application/pdf" : mimeType);

        // 1. Prefill To & CC recipients
        if (toEmails.Length > 0)
        {
            intent.PutExtra(Intent.ExtraEmail, toEmails);
        }
        if (ccEmails.Length > 0)
        {
            intent.PutExtra(Intent.ExtraCc, ccEmails);
        }

        // 2. Prefill Configured From Account hints (supported by Gmail versions that read account extras)
        if (!string.IsNullOrWhiteSpace(fromEmail))
        {
            string trimmedFrom = fromEmail.Trim();
            intent.PutExtra("account", trimmedFrom);
            intent.PutExtra("extra_account", trimmedFrom);
            intent.PutExtra("from", trimmedFrom);
            intent.PutExtra("user_email", trimmedFrom);
        }

        // 3. Prefill AI/OCR Fetched Subject Line
        intent.PutExtra(Intent.ExtraSubject, subjectLine ?? string.Empty);

        // 4. Prefill Body + Complementary Close
        intent.PutExtra(Intent.ExtraText, fullBodyText ?? string.Empty);

        // 5. Attach the PDF / Image via our ContentProvider URI and grant Gmail read permission
        intent.PutExtra(Intent.ExtraStream, shareableAttachmentUri);
        intent.ClipData = ClipData.NewRawUri(fileName, shareableAttachmentUri);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);

        // Explicitly grant URI permission to the Gmail package
        try
        {
            context.GrantUriPermission(
                GmailPackageName,
                shareableAttachmentUri,
                ActivityFlags.GrantReadUriPermission);
        }
        catch
        {
            // Ignore if Gmail is not installed
        }

        // Try launching the official Gmail app directly first
        intent.SetPackage(GmailPackageName);
        try
        {
            context.StartActivity(intent);
            return;
        }
        catch (ActivityNotFoundException)
        {
            // Fallback: if the official Gmail app is not installed or disabled, open the system email chooser
            intent.SetPackage(null);
            var chooser = Intent.CreateChooser(intent, "Send Email via Gmail");
            chooser?.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
            if (chooser != null)
            {
                context.StartActivity(chooser);
            }
            else
            {
                Toast.MakeText(context, "Could not find Gmail or an email client on this device.", ToastLength.Long)?.Show();
            }
        }
    }

    /// <summary>
    /// Optional Direct SMTP Send using the configured From Gmail address and a 16-character Gmail App Password.
    /// Useful if you have multiple Gmail accounts on your phone and want to guarantee sending from a specific From account without switching in the Gmail app.
    /// </summary>
    public static async Task SendDirectViaGmailSmtpAsync(
        AppConfig config,
        string toEmailsRaw,
        string ccEmailsRaw,
        string subjectLine,
        string fullBodyText,
        string localFilePath)
    {
        if (string.IsNullOrWhiteSpace(config.FromEmail))
            throw new InvalidOperationException("Please configure your 'From Gmail Account' in Settings first.");

        if (string.IsNullOrWhiteSpace(config.OptionalSmtpAppPassword))
            throw new InvalidOperationException("To use Direct Send, enter a 16-character Gmail App Password in Settings (or use the 'Open in Gmail App' button).");

        string[] toList = SplitEmails(toEmailsRaw);
        if (toList.Length == 0)
            throw new InvalidOperationException("Please specify at least one 'To' recipient email address.");

        using var message = new MailMessage
        {
            From = new MailAddress(config.FromEmail.Trim()),
            Subject = subjectLine,
            Body = fullBodyText,
            IsBodyHtml = false
        };

        foreach (var to in toList)
            message.To.Add(to);

        foreach (var cc in SplitEmails(ccEmailsRaw))
            message.CC.Add(cc);

        using var attachment = new Attachment(localFilePath);
        message.Attachments.Add(attachment);

        using var smtp = new SmtpClient("smtp.gmail.com", 587)
        {
            EnableSsl = true,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(
                config.FromEmail.Trim(),
                config.OptionalSmtpAppPassword.Replace(" ", "").Trim())
        };

        await smtp.SendMailAsync(message);
    }

    private static string[] SplitEmails(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => e.Contains('@'))
            .ToArray();
    }
}
