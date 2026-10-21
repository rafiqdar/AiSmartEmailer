#pragma warning disable CA1416, CA1422, CS0618, CS1570, CS8600, CS8602, CS8604, SYSLIB0014, IL2026, IL3050
using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AiSmartEmailer.Models;
using AiSmartEmailer.Services;

namespace AiSmartEmailer;

/// <summary>
/// Receives shared PDFs and Images from any Android app via the Android Share Sheet (ACTION_SEND).
/// Automatically:
///   1. Asynchronously stages the shared PDF/Image for attachment forwarding (non-blocking UI)
///   2. Runs the Multi-Engine AI / Hedged Parallel OCR Subject Extractor with live progress &amp; Skip/Retry controls
///   3. Prefills From, To, Subject, Body + Complementary Close, and Attachment
///   4. Automatically launches the Gmail app so you only have to click "Send"!
/// </summary>
[Activity(
    Label = "AI Smart Emailer",
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = SoftInput.AdjustResize)]
[IntentFilter(
    new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeTypes = new[] { "application/pdf", "image/*" })]
public class ShareTargetActivity : Android.App.Activity
{
    private AppSettingsService _settingsService = null!;
    private AiOcrSubjectExtractorService _ocrExtractor = null!;
    private AppConfig _config = null!;

    private LinearLayout _loadingPanel = null!;
    private TextView _txtStatusMessage = null!;
    private TextView _txtFileBadge = null!;
    private TextView _txtEngineBadge = null!;
    private EditText _editShareFrom = null!;
    private EditText _editShareTo = null!;
    private EditText _editShareSubject = null!;
    private EditText _editShareBody = null!;
    private Button _btnSkipWait = null!;
    private Button _btnRetryOcr = null!;
    private Button _btnOpenGmail = null!;
    private Button _btnDirectSend = null!;
    private Button _btnOpenSettings = null!;

    private string _stagedLocalFilePath = string.Empty;
    private Android.Net.Uri? _shareableUri;
    private string _stagedFileName = string.Empty;
    private string _stagedMimeType = "application/pdf";

    private System.Threading.CancellationTokenSource? _activeCts;

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_share_target);

        _settingsService = new AppSettingsService(this);
        _ocrExtractor = new AiOcrSubjectExtractorService();
        _config = _settingsService.LoadConfig();

        BindViews();
        await ProcessIncomingShareIntentAsync(Intent);
    }

    protected override async void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        _config = _settingsService.LoadConfig();
        await ProcessIncomingShareIntentAsync(intent);
    }

    protected override void OnDestroy()
    {
        CancelActiveExtraction();
        base.OnDestroy();
    }

    private void CancelActiveExtraction()
    {
        try
        {
            _activeCts?.Cancel();
            _activeCts?.Dispose();
        }
        catch { }
        finally
        {
            _activeCts = null;
        }
    }

    private void BindViews()
    {
        _loadingPanel = FindViewById<LinearLayout>(Resource.Id.loadingPanel)!;
        _txtStatusMessage = FindViewById<TextView>(Resource.Id.txtStatusMessage)!;
        _txtFileBadge = FindViewById<TextView>(Resource.Id.txtFileBadge)!;
        _txtEngineBadge = FindViewById<TextView>(Resource.Id.txtEngineBadge)!;
        _editShareFrom = FindViewById<EditText>(Resource.Id.editShareFrom)!;
        _editShareTo = FindViewById<EditText>(Resource.Id.editShareTo)!;
        _editShareSubject = FindViewById<EditText>(Resource.Id.editShareSubject)!;
        _editShareBody = FindViewById<EditText>(Resource.Id.editShareBody)!;
        _btnSkipWait = FindViewById<Button>(Resource.Id.btnSkipWait)!;
        _btnRetryOcr = FindViewById<Button>(Resource.Id.btnRetryOcr)!;
        _btnOpenGmail = FindViewById<Button>(Resource.Id.btnOpenGmail)!;
        _btnDirectSend = FindViewById<Button>(Resource.Id.btnDirectSend)!;
        _btnOpenSettings = FindViewById<Button>(Resource.Id.btnOpenSettings)!;

        // Keep Email Body's "{Subject}" placeholder synced if user manually edits the Subject box
        _editShareSubject.AfterTextChanged += (_, _) =>
        {
            string currentSubj = _editShareSubject.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(currentSubj))
            {
                _editShareBody.Text = AppSettingsService.BuildFullEmailBody(_config, currentSubj, _stagedFileName);
            }
        };

        _btnSkipWait.Click += (_, _) =>
        {
            CancelActiveExtraction();
            _loadingPanel.Visibility = ViewStates.Gone;
            _btnOpenGmail.Enabled = true;
            _btnRetryOcr.Enabled = true;
            _txtEngineBadge.Text = "OCR Engine: Skipped by user (Ready to edit or open Gmail)";
        };

        _btnRetryOcr.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_stagedLocalFilePath))
            {
                Toast.MakeText(this, "No staged attachment available to re-scan.", ToastLength.Short)?.Show();
                return;
            }
            await RunSubjectExtractionPipelineAsync(forceRefresh: true, autoLaunchGmailWhenDone: false);
        };

        _btnOpenGmail.Click += (_, _) => LaunchGmailNow();
        _btnDirectSend.Click += async (_, _) => await SendDirectlyViaSmtpAsync();
        _btnOpenSettings.Click += (_, _) =>
        {
            var settingsIntent = new Intent(this, typeof(MainActivity));
            StartActivity(settingsIntent);
        };
    }

    private async Task ProcessIncomingShareIntentAsync(Intent? intent)
    {
        CancelActiveExtraction();

        if (intent == null || intent.Action != Intent.ActionSend)
        {
            Toast.MakeText(this, "No shared file received.", ToastLength.Short)?.Show();
            return;
        }

        Android.Net.Uri? incomingUri = GetIncomingStreamUri(intent);
        if (incomingUri == null)
        {
            Toast.MakeText(this, "Could not read shared file URI.", ToastLength.Long)?.Show();
            return;
        }

        _activeCts = new System.Threading.CancellationTokenSource();
        var ct = _activeCts.Token;

        try
        {
            _loadingPanel.Visibility = ViewStates.Visible;
            _btnOpenGmail.Enabled = false;
            _btnRetryOcr.Enabled = false;
            _txtStatusMessage.Text = "Staging shared attachment...";

            _editShareFrom.Text = _config.FromEmail;
            _editShareTo.Text = _config.ToEmail;

            // 1. Asynchronously copy incoming content:// URI to our FileProvider cache off the UI thread
            var staged = await AttachmentFileProvider.StageIncomingUriAsync(this, incomingUri, intent.Type, ct);
            _stagedLocalFilePath = staged.LocalFilePath;
            _shareableUri = staged.ShareableUri;
            _stagedFileName = staged.FileName;
            _stagedMimeType = staged.MimeType;

            _txtFileBadge.Text = $"Attached: {_stagedFileName} ({_stagedMimeType})";

            // Prefill an immediate clean filename subject while OCR runs so the user never sees blank fields
            var (initialSubj, _) = LocalPdfAndImageHelper.ExtractSubjectFromTextOrLines(string.Empty, _stagedFileName);
            _editShareSubject.Text = initialSubj;
            _editShareBody.Text = AppSettingsService.BuildFullEmailBody(_config, initialSubj, _stagedFileName);
            _btnOpenGmail.Enabled = true;

            // 2. Run Multi-Engine AI + Parallel OCR Subject Extraction
            await RunSubjectExtractionPipelineAsync(forceRefresh: false, autoLaunchGmailWhenDone: _config.AutoOpenGmailAfterOcr);
        }
        catch (System.OperationCanceledException)
        {
            _loadingPanel.Visibility = ViewStates.Gone;
            _btnOpenGmail.Enabled = true;
            _btnRetryOcr.Enabled = true;
        }
        catch (Exception ex)
        {
            _loadingPanel.Visibility = ViewStates.Gone;
            _btnOpenGmail.Enabled = true;
            _btnRetryOcr.Enabled = true;
            _txtEngineBadge.Text = $"Error: {ex.Message}";
            Toast.MakeText(this, $"Error processing shared file: {ex.Message}", ToastLength.Long)?.Show();
        }
    }

    private async Task RunSubjectExtractionPipelineAsync(bool forceRefresh, bool autoLaunchGmailWhenDone)
    {
        CancelActiveExtraction();
        _activeCts = new System.Threading.CancellationTokenSource();
        var ct = _activeCts.Token;

        _loadingPanel.Visibility = ViewStates.Visible;
        _btnRetryOcr.Enabled = false;

        string currentStageText = "Analyzing document to extract Subject line...";
        long startTick = System.Environment.TickCount64;

        var progress = new Progress<string>(msg =>
        {
            currentStageText = msg;
            double elapsedSec = (System.Environment.TickCount64 - startTick) / 1000.0;
            _txtStatusMessage.Text = $"{currentStageText} ({elapsedSec:0.0}s)";
        });

        // Update elapsed timer smoothly every 300ms while waiting
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(300, ct);
                    RunOnUiThread(() =>
                    {
                        if (!ct.IsCancellationRequested && _loadingPanel.Visibility == ViewStates.Visible)
                        {
                            double elapsedSec = (System.Environment.TickCount64 - startTick) / 1000.0;
                            _txtStatusMessage.Text = $"{currentStageText} ({elapsedSec:0.0}s)";
                        }
                    });
                }
                catch (System.OperationCanceledException)
                {
                    break;
                }
            }
        }, ct);

        try
        {
            SubjectExtractionResult extraction = await Task.Run(() =>
                _ocrExtractor.ExtractSubjectAsync(
                    this,
                    _stagedLocalFilePath,
                    _stagedMimeType,
                    _stagedFileName,
                    _config,
                    progress,
                    forceRefresh,
                    ct), ct);

            if (ct.IsCancellationRequested) return;

            string extractedSubject = LocalPdfAndImageHelper.SanitizeReadableString(extraction.SubjectLine);
            if (string.IsNullOrWhiteSpace(extractedSubject))
            {
                extractedSubject = "Official Document for Review";
            }

            _editShareSubject.Text = extractedSubject;
            _editShareBody.Text = AppSettingsService.BuildFullEmailBody(_config, extractedSubject, _stagedFileName);
            _txtEngineBadge.Text = $"OCR Engine: {extraction.EngineUsed}";

            if (!string.IsNullOrWhiteSpace(extraction.ErrorMessage))
            {
                Toast.MakeText(this, extraction.ErrorMessage, ToastLength.Long)?.Show();
            }

            _loadingPanel.Visibility = ViewStates.Gone;
            _btnOpenGmail.Enabled = true;
            _btnRetryOcr.Enabled = true;

            if (autoLaunchGmailWhenDone && extraction.FoundDocumentSubject)
            {
                LaunchGmailNow();
            }
        }
        catch (System.OperationCanceledException)
        {
            _loadingPanel.Visibility = ViewStates.Gone;
            _btnOpenGmail.Enabled = true;
            _btnRetryOcr.Enabled = true;
        }
        catch (Exception ex)
        {
            _loadingPanel.Visibility = ViewStates.Gone;
            _btnOpenGmail.Enabled = true;
            _btnRetryOcr.Enabled = true;
            _txtEngineBadge.Text = $"OCR Notice: {ex.Message}";
            Toast.MakeText(this, $"Subject extraction notice: {ex.Message}", ToastLength.Long)?.Show();
        }
    }

    private static Android.Net.Uri? GetIncomingStreamUri(Intent intent)
    {
        Android.Net.Uri? uri = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            uri = intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Android.Net.Uri))) as Android.Net.Uri;
        }
        else
        {
#pragma warning disable CS0618
            uri = intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri;
#pragma warning restore CS0618
        }

        if (uri == null && intent.ClipData != null && intent.ClipData.ItemCount > 0)
        {
            uri = intent.ClipData.GetItemAt(0)?.Uri;
        }

        return uri;
    }

    private void LaunchGmailNow()
    {
        if (_shareableUri == null)
        {
            Toast.MakeText(this, "No attachment staged to send.", ToastLength.Short)?.Show();
            return;
        }

        GmailLauncherService.LaunchGmailWithAttachment(
            context: this,
            fromEmail: _editShareFrom.Text?.Trim() ?? _config.FromEmail,
            toEmailsRaw: _editShareTo.Text?.Trim() ?? _config.ToEmail,
            ccEmailsRaw: _config.CcEmail,
            subjectLine: LocalPdfAndImageHelper.SanitizeReadableString(_editShareSubject.Text),
            fullBodyText: LocalPdfAndImageHelper.SanitizeReadableString(_editShareBody.Text),
            shareableAttachmentUri: _shareableUri,
            mimeType: _stagedMimeType,
            fileName: _stagedFileName);
    }

    private async Task SendDirectlyViaSmtpAsync()
    {
        try
        {
            _btnDirectSend.Enabled = false;
            _loadingPanel.Visibility = ViewStates.Visible;
            _txtStatusMessage.Text = "Sending email directly from your configured Gmail account...";

            await GmailLauncherService.SendDirectViaGmailSmtpAsync(
                config: _config,
                toEmailsRaw: _editShareTo.Text?.Trim() ?? _config.ToEmail,
                ccEmailsRaw: _config.CcEmail,
                subjectLine: LocalPdfAndImageHelper.SanitizeReadableString(_editShareSubject.Text),
                fullBodyText: LocalPdfAndImageHelper.SanitizeReadableString(_editShareBody.Text),
                localFilePath: _stagedLocalFilePath);

            _loadingPanel.Visibility = ViewStates.Gone;
            _btnDirectSend.Enabled = true;
            Toast.MakeText(this, "Email sent successfully!", ToastLength.Long)?.Show();
            Finish();
        }
        catch (Exception ex)
        {
            _loadingPanel.Visibility = ViewStates.Gone;
            _btnDirectSend.Enabled = true;
            Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
        }
    }
}
