#pragma warning disable CA1416, CA1422, CS0618, CS1570, CS8600, CS8602, CS8604, SYSLIB0014, IL2026, IL3050
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using AiSmartEmailer.Models;

namespace AiSmartEmailer.Services;

/// <summary>
/// High-Performance Multi-Engine OCR &amp; AI Subject Extraction Pipeline (v4.0):
///   - Stage 0: Instant Local Subject Cache (&lt; 2ms on repeat shares).
///   - Stage 1: Fast Non-Backtracking Digital PDF Stream Parser (&lt; 80ms, works 100% offline).
///   - Stage 2: OOM-safe Image Decode + Pure-C# EXIF Auto-Rotation.
///   - Stage 3: Optional Gemini / OpenAI Vision Agent with fast per-request timeout &amp; automatic model fallback.
///   - Stage 4: Hedged Parallel Cloud Vision OCR (OCR.space Engine 2 + Engine 1 concurrent race with confidence scoring).
/// </summary>
public class AiOcrSubjectExtractorService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(35)
    };

    private class OverlaySegment
    {
        public string Text { get; set; } = string.Empty;
        public double Left { get; set; }
        public double Top { get; set; }
        public double Height { get; set; }
        public double CenterY => Top + (Height / 2.0);
    }

    public async Task<SubjectExtractionResult> ExtractSubjectAsync(
        Context context,
        string localFilePath,
        string mimeType,
        string fileName,
        AppConfig config,
        IProgress<string>? progress = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        long startTick = System.Environment.TickCount64;
        long ElapsedMs() => Math.Max(1L, System.Environment.TickCount64 - startTick);

        var settingsService = new AppSettingsService(context);
        string fileFingerprint = AppSettingsService.ComputeFastFileFingerprint(localFilePath);

        // =========================================================================
        // STAGE 0: Instant Local Subject Cache (< 2 ms)
        // =========================================================================
        if (config.EnableSubjectCache && !forceRefresh && !string.IsNullOrWhiteSpace(fileFingerprint))
        {
            if (settingsService.TryGetCachedSubject(fileFingerprint, out string cachedSubj, out string cachedEngine))
            {
                long elapsed = ElapsedMs();
                return new SubjectExtractionResult
                {
                    Success = true,
                    FoundDocumentSubject = true,
                    FoundExplicitSubject = true,
                    ConfidenceScore = 100,
                    ElapsedMilliseconds = elapsed,
                    SubjectLine = cachedSubj,
                    EngineUsed = $"{cachedEngine} ({elapsed} ms)"
                };
            }
        }

        bool isPdf = mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                     || localFilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

        var bitmaps = new List<Bitmap>();
        var bestCandidate = new LocalPdfAndImageHelper.DetailedSubjectMatch();
        string bestCandidateEngine = string.Empty;
        string bestRawSnippet = string.Empty;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // =========================================================================
            // STAGE 1: Fast Local Digital PDF Stream Parser (< 80 ms, 100% Offline)
            // =========================================================================
            string genuineDigitalText = string.Empty;
            if (isPdf)
            {
                progress?.Report("Stage 1/3: Scanning digital PDF text streams...");
                genuineDigitalText = LocalPdfAndImageHelper.ExtractGenuineDigitalTextFromPdf(localFilePath);

                if (!string.IsNullOrWhiteSpace(genuineDigitalText))
                {
                    var digitalMatch = LocalPdfAndImageHelper.ExtractSubjectDetailed(genuineDigitalText, fileName);
                    if (digitalMatch.FoundExplicitSubject && LocalPdfAndImageHelper.IsValidHumanReadableSubject(digitalMatch.SubjectLine))
                    {
                        long elapsed = ElapsedMs();
                        settingsService.SaveCachedSubject(fileFingerprint, digitalMatch.SubjectLine, "Digital PDF Text Parser");
                        return new SubjectExtractionResult
                        {
                            Success = true,
                            FoundDocumentSubject = true,
                            FoundExplicitSubject = true,
                            ConfidenceScore = digitalMatch.ConfidenceScore,
                            ElapsedMilliseconds = elapsed,
                            SubjectLine = digitalMatch.SubjectLine,
                            EngineUsed = $"Digital PDF Parser ({digitalMatch.MatchTier}, {elapsed} ms)",
                            RawExtractedSnippet = genuineDigitalText
                        };
                    }

                    if (digitalMatch.FoundInDocument && digitalMatch.ConfidenceScore > bestCandidate.ConfidenceScore)
                    {
                        bestCandidate = digitalMatch;
                        bestCandidateEngine = "Digital PDF Heading Parser";
                        bestRawSnippet = genuineDigitalText;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // =========================================================================
            // STAGE 2: High-Resolution Bitmap Rendering & EXIF Orientation Fix
            // =========================================================================
            progress?.Report("Stage 2/3: Preparing document image for AI / OCR...");
            if (isPdf)
            {
                bitmaps = LocalPdfAndImageHelper.RenderPdfPagesToBitmaps(
                    localFilePath,
                    maxPages: 2,
                    enhanceContrast: config.EnableImageEnhancement);
            }
            else
            {
                var imgBitmap = LocalPdfAndImageHelper.LoadBitmapForOcr(
                    localFilePath,
                    enhanceContrast: config.EnableImageEnhancement);
                if (imgBitmap != null)
                {
                    bitmaps.Add(imgBitmap);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            int timeoutSeconds = Math.Clamp(config.OcrTimeoutSeconds, 5, 30);
            string apiKey = config.ApiKey?.Trim() ?? string.Empty;
            string provider = config.AiProvider?.Trim() ?? "Gemini";
            string? aiErrorNotice = null;

            // =========================================================================
            // STAGE 3: Optional Configured AI Vision Agent (Gemini / OpenAI)
            // =========================================================================
            if (!string.IsNullOrWhiteSpace(apiKey)
                && !provider.Equals("LocalOnly", StringComparison.OrdinalIgnoreCase)
                && bitmaps.Count > 0)
            {
                progress?.Report($"Stage 3/3: Extracting subject via {provider} AI Vision Agent...");
                using var aiCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                aiCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                try
                {
                    SubjectExtractionResult aiRes = provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                        ? await ExtractWithOpenAiVisionAsync(bitmaps.Take(1).ToList(), genuineDigitalText, fileName, config, aiCts.Token)
                        : await ExtractWithGeminiAgentWithFallbackAsync(bitmaps.Take(1).ToList(), genuineDigitalText, fileName, config, aiCts.Token);

                    if (aiRes.Success && LocalPdfAndImageHelper.IsValidHumanReadableSubject(aiRes.SubjectLine))
                    {
                        long elapsed = ElapsedMs();
                        aiRes.ElapsedMilliseconds = elapsed;
                        aiRes.EngineUsed = $"{aiRes.EngineUsed} ({elapsed} ms)";
                        settingsService.SaveCachedSubject(fileFingerprint, aiRes.SubjectLine, aiRes.EngineUsed);
                        return aiRes;
                    }
                }
                catch (System.OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    aiErrorNotice = $"{provider} API timed out after {timeoutSeconds}s; switched to Built-in OCR.";
                }
                catch (Exception ex)
                {
                    aiErrorNotice = $"{provider} API notice: {ex.Message}; switched to Built-in OCR.";
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // =========================================================================
            // STAGE 4: Hedged Parallel Built-In Cloud OCR (Engine 2 + Engine 1 Race)
            // =========================================================================
            string lastOcrError = string.Empty;
            if (bitmaps.Count > 0)
            {
                progress?.Report("Stage 3/3: Running parallel AI Document OCR (Engine 2 + Engine 1)...");

                byte[] top78Jpeg = LocalPdfAndImageHelper.CropTopPortionToJpegBytes(bitmaps[0], heightFraction: 0.78, maxBytes: 250_000);
                byte[] fullPageJpeg = LocalPdfAndImageHelper.CompressBitmapUnderLimit(bitmaps[0], maxBytes: 340_000);

                using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                raceCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                Task<(string Text, string? Error, string EngineLabel)> taskE2 =
                    RunBuiltInVisionOcrWithRetryAsync(top78Jpeg, "2", "Built-in Neural OCR (Engine 2)", raceCts.Token);

                Task<(string Text, string? Error, string EngineLabel)> taskE1 = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1500, raceCts.Token);
                    }
                    catch (System.OperationCanceledException)
                    {
                        return (string.Empty, "Cancelled", "Built-in Fast OCR (Engine 1)");
                    }
                    return await RunBuiltInVisionOcrWithRetryAsync(fullPageJpeg, "1", "Built-in Fast OCR (Engine 1)", raceCts.Token);
                }, raceCts.Token);

                var pendingTasks = new List<Task<(string Text, string? Error, string EngineLabel)>> { taskE2, taskE1 };

                while (pendingTasks.Count > 0)
                {
                    var finishedTask = await Task.WhenAny(pendingTasks);
                    pendingTasks.Remove(finishedTask);

                    var (ocrText, ocrErr, engineLabel) = await finishedTask;
                    if (!string.IsNullOrWhiteSpace(ocrErr) && ocrErr != "Cancelled")
                    {
                        lastOcrError = ocrErr;
                    }

                    if (!string.IsNullOrWhiteSpace(ocrText))
                    {
                        var match = LocalPdfAndImageHelper.ExtractSubjectDetailed(ocrText, fileName);

                        if (match.FoundExplicitSubject && LocalPdfAndImageHelper.IsValidHumanReadableSubject(match.SubjectLine))
                        {
                            raceCts.Cancel();
                            long elapsed = ElapsedMs();
                            settingsService.SaveCachedSubject(fileFingerprint, match.SubjectLine, engineLabel);
                            return new SubjectExtractionResult
                            {
                                Success = true,
                                FoundDocumentSubject = true,
                                FoundExplicitSubject = true,
                                ConfidenceScore = match.ConfidenceScore,
                                ElapsedMilliseconds = elapsed,
                                SubjectLine = match.SubjectLine,
                                EngineUsed = $"{engineLabel} ({match.MatchTier}, {elapsed} ms)",
                                RawExtractedSnippet = ocrText,
                                ErrorMessage = aiErrorNotice
                            };
                        }

                        if (match.FoundInDocument && match.ConfidenceScore > bestCandidate.ConfidenceScore)
                        {
                            bestCandidate = match;
                            bestCandidateEngine = engineLabel;
                            bestRawSnippet = ocrText;
                        }
                    }
                }

                // If Page 1 did not contain an explicit "Subject:" and this is a multi-page PDF, check Page 2!
                if (!bestCandidate.FoundExplicitSubject && bitmaps.Count > 1 && !cancellationToken.IsCancellationRequested)
                {
                    progress?.Report("Checking Page 2 for Subject line...");
                    using var page2Cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    page2Cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(6, timeoutSeconds - 3)));

                    byte[] page2Jpeg = LocalPdfAndImageHelper.CropTopPortionToJpegBytes(bitmaps[1], heightFraction: 0.70, maxBytes: 250_000);
                    var (p2Text, _, p2Label) = await RunBuiltInVisionOcrWithRetryAsync(page2Jpeg, "2", "Built-in Neural OCR (Page 2)", page2Cts.Token);
                    if (!string.IsNullOrWhiteSpace(p2Text))
                    {
                        var p2Match = LocalPdfAndImageHelper.ExtractSubjectDetailed(p2Text, fileName);
                        if (p2Match.FoundExplicitSubject && LocalPdfAndImageHelper.IsValidHumanReadableSubject(p2Match.SubjectLine))
                        {
                            long elapsed = ElapsedMs();
                            settingsService.SaveCachedSubject(fileFingerprint, p2Match.SubjectLine, p2Label);
                            return new SubjectExtractionResult
                            {
                                Success = true,
                                FoundDocumentSubject = true,
                                FoundExplicitSubject = true,
                                ConfidenceScore = p2Match.ConfidenceScore,
                                ElapsedMilliseconds = elapsed,
                                SubjectLine = p2Match.SubjectLine,
                                EngineUsed = $"{p2Label} ({elapsed} ms)",
                                RawExtractedSnippet = p2Text
                            };
                        }
                    }
                }
            }

            // =========================================================================
            // STAGE 5: Return Best Document Heading Candidate or Clean Filename Fallback
            // =========================================================================
            long totalElapsed = ElapsedMs();
            if (bestCandidate.FoundInDocument && LocalPdfAndImageHelper.IsValidHumanReadableSubject(bestCandidate.SubjectLine))
            {
                settingsService.SaveCachedSubject(fileFingerprint, bestCandidate.SubjectLine, bestCandidateEngine);
                return new SubjectExtractionResult
                {
                    Success = true,
                    FoundDocumentSubject = true,
                    FoundExplicitSubject = false,
                    ConfidenceScore = bestCandidate.ConfidenceScore,
                    ElapsedMilliseconds = totalElapsed,
                    SubjectLine = bestCandidate.SubjectLine,
                    EngineUsed = $"{bestCandidateEngine} ({bestCandidate.MatchTier}, {totalElapsed} ms)",
                    RawExtractedSnippet = bestRawSnippet,
                    ErrorMessage = aiErrorNotice
                };
            }

            var fallback = LocalPdfAndImageHelper.ExtractSubjectDetailed(string.Empty, fileName);
            return new SubjectExtractionResult
            {
                Success = false,
                FoundDocumentSubject = false,
                FoundExplicitSubject = false,
                ConfidenceScore = fallback.ConfidenceScore,
                ElapsedMilliseconds = totalElapsed,
                SubjectLine = fallback.SubjectLine,
                EngineUsed = $"Fallback ({totalElapsed} ms) — Tap 'RE-SCAN AI / OCR' to retry",
                ErrorMessage = !string.IsNullOrWhiteSpace(lastOcrError)
                    ? $"OCR notice: {lastOcrError}. You can tap 'RE-SCAN AI / OCR' or edit the Subject directly."
                    : (aiErrorNotice ?? "Could not detect an explicit Subject line. You can edit the Subject below or tap 'RE-SCAN AI / OCR'.")
            };
        }
        finally
        {
            foreach (var bmp in bitmaps)
            {
                try { bmp?.Dispose(); } catch { }
            }
        }
    }

    private async Task<(string ExtractedText, string? Error, string EngineLabel)> RunBuiltInVisionOcrWithRetryAsync(
        byte[] jpegBytes,
        string ocrEngine,
        string engineLabel,
        CancellationToken ct)
    {
        var (text1, err1) = await RunBuiltInVisionOcrOnceAsync(jpegBytes, ocrEngine, ct);
        if (!string.IsNullOrWhiteSpace(text1) || ct.IsCancellationRequested)
        {
            return (text1, err1, engineLabel);
        }

        try
        {
            await Task.Delay(400, ct);
            var (text2, err2) = await RunBuiltInVisionOcrOnceAsync(jpegBytes, ocrEngine, ct);
            return (text2, err2, engineLabel);
        }
        catch (System.OperationCanceledException)
        {
            return (string.Empty, "Timed out", engineLabel);
        }
    }

    private async Task<(string ExtractedText, string? Error)> RunBuiltInVisionOcrOnceAsync(
        byte[] jpegBytes,
        string ocrEngine,
        CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("helloworld"), "apikey");
            form.Add(new StringContent("eng"), "language");
            form.Add(new StringContent("true"), "isOverlayRequired");
            form.Add(new StringContent("true"), "isTable");
            form.Add(new StringContent("true"), "scale");
            form.Add(new StringContent("true"), "detectOrientation");
            form.Add(new StringContent(ocrEngine), "OCREngine");

            var imageContent = new ByteArrayContent(jpegBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(imageContent, "file", "document_page.jpg");

            using var response = await SharedHttpClient.PostAsync("https://api.ocr.space/parse/image", form, ct);
            string json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return (string.Empty, $"HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("IsErroredOnProcessing", out JsonElement isErr) && isErr.ValueKind == JsonValueKind.True)
            {
                string errMsg = "OCR processing error";
                if (root.TryGetProperty("ErrorMessage", out JsonElement errArr) && errArr.ValueKind == JsonValueKind.Array && errArr.GetArrayLength() > 0)
                {
                    errMsg = errArr[0].GetString() ?? errMsg;
                }
                return (string.Empty, errMsg);
            }

            if (!root.TryGetProperty("ParsedResults", out JsonElement results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                return (string.Empty, "No OCR results returned");
            }

            var firstResult = results[0];

            string geometricText = ReconstructLinesFromJsonOverlay(firstResult);

            string parsedText = string.Empty;
            if (firstResult.TryGetProperty("ParsedText", out JsonElement ptEl))
            {
                parsedText = LocalPdfAndImageHelper.SanitizeReadableString(ptEl.GetString());
            }

            var geoMatch = !string.IsNullOrWhiteSpace(geometricText)
                ? LocalPdfAndImageHelper.ExtractSubjectDetailed(geometricText)
                : new LocalPdfAndImageHelper.DetailedSubjectMatch();

            var parsedMatch = !string.IsNullOrWhiteSpace(parsedText)
                ? LocalPdfAndImageHelper.ExtractSubjectDetailed(parsedText)
                : new LocalPdfAndImageHelper.DetailedSubjectMatch();

            if (geoMatch.ConfidenceScore >= parsedMatch.ConfidenceScore && !string.IsNullOrWhiteSpace(geometricText))
            {
                return (geometricText, null);
            }

            return (!string.IsNullOrWhiteSpace(parsedText) ? parsedText : geometricText, null);
        }
        catch (System.OperationCanceledException)
        {
            return (string.Empty, "Request timed out");
        }
        catch (Exception ex)
        {
            return (string.Empty, ex.Message);
        }
    }

    private static string ReconstructLinesFromJsonOverlay(JsonElement firstResult)
    {
        try
        {
            if (!firstResult.TryGetProperty("TextOverlay", out JsonElement overlay)
                || !overlay.TryGetProperty("Lines", out JsonElement linesArr)
                || linesArr.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var segments = new List<OverlaySegment>();

            foreach (JsonElement lineEl in linesArr.EnumerateArray())
            {
                string lineText = lineEl.TryGetProperty("LineText", out JsonElement lt) ? (lt.GetString() ?? "") : "";
                lineText = LocalPdfAndImageHelper.SanitizeReadableString(lineText);
                if (string.IsNullOrWhiteSpace(lineText)) continue;

                double minTop = lineEl.TryGetProperty("MinTop", out JsonElement mt) && mt.TryGetDouble(out double topVal) ? topVal : 0;
                double maxHeight = lineEl.TryGetProperty("MaxHeight", out JsonElement mh) && mh.TryGetDouble(out double hVal) ? Math.Max(1.0, hVal) : 20.0;
                double left = 0;

                if (lineEl.TryGetProperty("Words", out JsonElement wordsArr)
                    && wordsArr.ValueKind == JsonValueKind.Array
                    && wordsArr.GetArrayLength() > 0)
                {
                    var firstWord = wordsArr[0];
                    if (firstWord.TryGetProperty("Left", out JsonElement leftEl) && leftEl.TryGetDouble(out double lVal))
                    {
                        left = lVal;
                    }
                }

                segments.Add(new OverlaySegment
                {
                    Text = lineText,
                    Left = left,
                    Top = minTop,
                    Height = maxHeight
                });
            }

            if (segments.Count == 0) return string.Empty;

            var sorted = segments.OrderBy(s => s.CenterY).ThenBy(s => s.Left).ToList();
            var rows = new List<List<OverlaySegment>>();

            foreach (var seg in sorted)
            {
                if (rows.Count == 0)
                {
                    rows.Add(new List<OverlaySegment> { seg });
                    continue;
                }

                var currentRow = rows[^1];
                double avgCenterY = currentRow.Average(r => r.CenterY);
                double avgHeight = currentRow.Average(r => r.Height);

                if (Math.Abs(seg.CenterY - avgCenterY) <= Math.Max(12.0, avgHeight * 0.62))
                {
                    currentRow.Add(seg);
                }
                else
                {
                    rows.Add(new List<OverlaySegment> { seg });
                }
            }

            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                var orderedTexts = row.OrderBy(r => r.Left).Select(r => r.Text.Trim()).Where(t => t.Length > 0);
                string mergedRow = Regex.Replace(string.Join(" ", orderedTexts), @"\s+", " ").Trim();
                if (!string.IsNullOrWhiteSpace(mergedRow))
                {
                    sb.AppendLine(mergedRow);
                }
            }

            return sb.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<SubjectExtractionResult> ExtractWithGeminiAgentWithFallbackAsync(
        List<Bitmap> bitmaps,
        string digitalText,
        string fileName,
        AppConfig config,
        CancellationToken ct)
    {
        string preferredModel = string.IsNullOrWhiteSpace(config.AiModel) ? "gemini-2.5-flash" : config.AiModel.Trim();
        var candidateModels = new List<string> { preferredModel };
        if (!candidateModels.Contains("gemini-2.5-flash", StringComparer.OrdinalIgnoreCase))
            candidateModels.Add("gemini-2.5-flash");
        if (!candidateModels.Contains("gemini-2.0-flash", StringComparer.OrdinalIgnoreCase))
            candidateModels.Add("gemini-2.0-flash");

        Exception? lastEx = null;
        foreach (string model in candidateModels)
        {
            try
            {
                return await ExtractWithGeminiModelAsync(bitmaps, digitalText, fileName, config, model, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastEx = ex;
            }
        }

        throw lastEx ?? new InvalidOperationException("Gemini API extraction failed.");
    }

    private async Task<SubjectExtractionResult> ExtractWithGeminiModelAsync(
        List<Bitmap> bitmaps,
        string digitalText,
        string fileName,
        AppConfig config,
        string model,
        CancellationToken ct)
    {
        string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(config.ApiKey.Trim())}";

        string prompt = string.IsNullOrWhiteSpace(config.CustomOcrPrompt)
            ? new AppConfig().CustomOcrPrompt
            : config.CustomOcrPrompt;

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine(prompt);
        promptBuilder.AppendLine($"File Name: {fileName}");
        if (!string.IsNullOrWhiteSpace(digitalText))
        {
            promptBuilder.AppendLine("Digital Text from PDF:");
            promptBuilder.AppendLine(digitalText);
        }

        // Build JSON with Utf8JsonWriter (100% Trimmer & AOT safe in Release APK builds)
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("contents");
            writer.WriteStartObject();
            writer.WriteStartArray("parts");

            writer.WriteStartObject();
            writer.WriteString("text", promptBuilder.ToString());
            writer.WriteEndObject();

            foreach (var bmp in bitmaps)
            {
                byte[] jpegBytes = LocalPdfAndImageHelper.CompressBitmapUnderLimit(bmp, 360_000);
                writer.WriteStartObject();
                writer.WriteStartObject("inline_data");
                writer.WriteString("mime_type", "image/jpeg");
                writer.WriteString("data", Convert.ToBase64String(jpegBytes));
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartObject("generationConfig");
            writer.WriteNumber("temperature", 0.05);
            writer.WriteNumber("maxOutputTokens", 200);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        string jsonPayload = Encoding.UTF8.GetString(ms.ToArray());

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };

        using var response = await SharedHttpClient.SendAsync(request, ct);
        string responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gemini ({model}) HTTP {(int)response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        string? rawSubject = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        string cleanedSubject = CleanExtractedSubject(rawSubject);
        bool valid = LocalPdfAndImageHelper.IsValidHumanReadableSubject(cleanedSubject);
        return new SubjectExtractionResult
        {
            Success = valid,
            FoundDocumentSubject = valid,
            FoundExplicitSubject = valid,
            ConfidenceScore = valid ? 99 : 0,
            SubjectLine = cleanedSubject,
            EngineUsed = $"Gemini AI Vision ({model})",
            RawExtractedSnippet = digitalText
        };
    }

    private async Task<SubjectExtractionResult> ExtractWithOpenAiVisionAsync(
        List<Bitmap> bitmaps,
        string digitalText,
        string fileName,
        AppConfig config,
        CancellationToken ct)
    {
        string model = string.IsNullOrWhiteSpace(config.AiModel) || config.AiModel.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)
            ? "gpt-4o-mini"
            : config.AiModel.Trim();

        string extra = string.IsNullOrWhiteSpace(digitalText) ? string.Empty : $"\nText:\n{digitalText}";

        // Build JSON with Utf8JsonWriter (100% Trimmer & AOT safe in Release APK builds)
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");

            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", $"{config.CustomOcrPrompt}\nFile Name: {fileName}{extra}");
            writer.WriteEndObject();

            foreach (var bmp in bitmaps)
            {
                byte[] jpegBytes = LocalPdfAndImageHelper.CompressBitmapUnderLimit(bmp, 360_000);
                string dataUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(jpegBytes)}";
                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WriteStartObject("image_url");
                writer.WriteString("url", dataUrl);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteNumber("temperature", 0.05);
            writer.WriteNumber("max_tokens", 180);
            writer.WriteEndObject();
        }

        string jsonPayload = Encoding.UTF8.GetString(ms.ToArray());

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());

        using var response = await SharedHttpClient.SendAsync(request, ct);
        string responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI HTTP {(int)response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        string? rawSubject = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        string cleanedSubject = CleanExtractedSubject(rawSubject);
        bool valid = LocalPdfAndImageHelper.IsValidHumanReadableSubject(cleanedSubject);
        return new SubjectExtractionResult
        {
            Success = valid,
            FoundDocumentSubject = valid,
            FoundExplicitSubject = valid,
            ConfidenceScore = valid ? 99 : 0,
            SubjectLine = cleanedSubject,
            EngineUsed = $"OpenAI Vision ({model})",
            RawExtractedSnippet = digitalText
        };
    }

    private static string CleanExtractedSubject(string? raw)
    {
        string sanitized = LocalPdfAndImageHelper.SanitizeReadableString(raw);
        if (string.IsNullOrWhiteSpace(sanitized)) return string.Empty;

        string cleaned = sanitized
            .Trim('"', '\'', '`', '*')
            .Replace("\r", " ")
            .Replace("\n", " ");

        cleaned = Regex.Replace(
            cleaned,
            @"^(?:Subject|Subj|Sub|Regarding|Ref|Re|विषय)\s*[:.;=\-–—~|/]+\s*",
            string.Empty,
            RegexOptions.IgnoreCase);

        cleaned = cleaned.TrimStart('-', '–', '—', ':', ';', '.', ' ');

        while (cleaned.Contains("  "))
            cleaned = cleaned.Replace("  ", " ");

        return cleaned.Trim();
    }
}
