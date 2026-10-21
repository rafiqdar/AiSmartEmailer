#pragma warning disable CA1416, CA1422, CS0618, CS1570, CS8600, CS8602, CS8604, SYSLIB0014, IL2026, IL3050
using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AiSmartEmailer.Models;
using AiSmartEmailer.Services;

namespace AiSmartEmailer;

/// <summary>
/// Main Configuration Activity where the user configures:
/// - From Gmail Account
/// - Default To Gmail Account(s) &amp; CC
/// - Default Email Body &amp; Complementary Close lines
/// - AI Agent OCR Settings (Gemini / OpenAI API Key, Model, Timeout, Image Contrast Enhancement, Subject Cache)
/// - Auto-open Gmail toggle
/// </summary>
[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = Android.Views.SoftInput.AdjustResize)]
public class MainActivity : Android.App.Activity
{
    private AppSettingsService _settingsService = null!;

    private EditText _editFromEmail = null!;
    private EditText _editToEmail = null!;
    private EditText _editCcEmail = null!;
    private EditText _editEmailBody = null!;
    private EditText _editComplementaryClose = null!;
    private Spinner _spinnerAiProvider = null!;
    private EditText _editApiKey = null!;
    private EditText _editAiModel = null!;
    private EditText _editOcrTimeout = null!;
    private EditText _editCustomPrompt = null!;
    private Switch _switchImageEnhancement = null!;
    private Switch _switchSubjectCache = null!;
    private Switch _switchAutoOpenGmail = null!;
    private EditText _editSmtpAppPassword = null!;
    private Button _btnSaveSettings = null!;
    private Button _btnClearCache = null!;

    private readonly string[] _providers = new[] { "Gemini", "OpenAI", "LocalOnly" };

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _settingsService = new AppSettingsService(this);

        _editFromEmail = FindViewById<EditText>(Resource.Id.editFromEmail)!;
        _editToEmail = FindViewById<EditText>(Resource.Id.editToEmail)!;
        _editCcEmail = FindViewById<EditText>(Resource.Id.editCcEmail)!;
        _editEmailBody = FindViewById<EditText>(Resource.Id.editEmailBody)!;
        _editComplementaryClose = FindViewById<EditText>(Resource.Id.editComplementaryClose)!;
        _spinnerAiProvider = FindViewById<Spinner>(Resource.Id.spinnerAiProvider)!;
        _editApiKey = FindViewById<EditText>(Resource.Id.editApiKey)!;
        _editAiModel = FindViewById<EditText>(Resource.Id.editAiModel)!;
        _editOcrTimeout = FindViewById<EditText>(Resource.Id.editOcrTimeout)!;
        _editCustomPrompt = FindViewById<EditText>(Resource.Id.editCustomPrompt)!;
        _switchImageEnhancement = FindViewById<Switch>(Resource.Id.switchImageEnhancement)!;
        _switchSubjectCache = FindViewById<Switch>(Resource.Id.switchSubjectCache)!;
        _switchAutoOpenGmail = FindViewById<Switch>(Resource.Id.switchAutoOpenGmail)!;
        _editSmtpAppPassword = FindViewById<EditText>(Resource.Id.editSmtpAppPassword)!;
        _btnSaveSettings = FindViewById<Button>(Resource.Id.btnSaveSettings)!;
        _btnClearCache = FindViewById<Button>(Resource.Id.btnClearCache)!;

        var adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerDropDownItem, _providers);
        _spinnerAiProvider.Adapter = adapter;

        LoadSettingsIntoUi();

        _btnSaveSettings.Click += (_, _) => SaveSettingsFromUi();
        _btnClearCache.Click += (_, _) =>
        {
            _settingsService.ClearSubjectCache();
            Toast.MakeText(this, "Cached subjects cleared!", ToastLength.Short)?.Show();
        };
    }

    private void LoadSettingsIntoUi()
    {
        AppConfig config = _settingsService.LoadConfig();

        _editFromEmail.Text = config.FromEmail;
        _editToEmail.Text = config.ToEmail;
        _editCcEmail.Text = config.CcEmail;
        _editEmailBody.Text = config.EmailBody;
        _editComplementaryClose.Text = config.ComplementaryClose;

        int providerIdx = Array.FindIndex(_providers, p => p.Equals(config.AiProvider, StringComparison.OrdinalIgnoreCase));
        _spinnerAiProvider.SetSelection(providerIdx >= 0 ? providerIdx : 0);

        _editApiKey.Text = config.ApiKey;
        _editAiModel.Text = config.AiModel;
        _editOcrTimeout.Text = config.OcrTimeoutSeconds.ToString();
        _editCustomPrompt.Text = config.CustomOcrPrompt;
        _switchImageEnhancement.Checked = config.EnableImageEnhancement;
        _switchSubjectCache.Checked = config.EnableSubjectCache;
        _switchAutoOpenGmail.Checked = config.AutoOpenGmailAfterOcr;
        _editSmtpAppPassword.Text = config.OptionalSmtpAppPassword;
    }

    private void SaveSettingsFromUi()
    {
        int timeoutSec = 12;
        if (int.TryParse(_editOcrTimeout.Text?.Trim(), out int parsedTimeout))
        {
            timeoutSec = Math.Clamp(parsedTimeout, 5, 30);
        }

        var config = new AppConfig
        {
            FromEmail = _editFromEmail.Text?.Trim() ?? string.Empty,
            ToEmail = _editToEmail.Text?.Trim() ?? string.Empty,
            CcEmail = _editCcEmail.Text?.Trim() ?? string.Empty,
            EmailBody = _editEmailBody.Text ?? string.Empty,
            ComplementaryClose = _editComplementaryClose.Text ?? string.Empty,
            AiProvider = _providers[_spinnerAiProvider.SelectedItemPosition],
            ApiKey = _editApiKey.Text?.Trim() ?? string.Empty,
            AiModel = _editAiModel.Text?.Trim() ?? "gemini-2.5-flash",
            OcrTimeoutSeconds = timeoutSec,
            CustomOcrPrompt = _editCustomPrompt.Text ?? string.Empty,
            EnableImageEnhancement = _switchImageEnhancement.Checked,
            EnableSubjectCache = _switchSubjectCache.Checked,
            AutoOpenGmailAfterOcr = _switchAutoOpenGmail.Checked,
            OptionalSmtpAppPassword = _editSmtpAppPassword.Text?.Trim() ?? string.Empty
        };

        _settingsService.SaveConfig(config);
        Toast.MakeText(this, "Configuration saved! Share any PDF or Image to AI Smart Emailer.", ToastLength.Long)?.Show();
    }
}
