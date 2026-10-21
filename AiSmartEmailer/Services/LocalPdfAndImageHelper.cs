#pragma warning disable CA1416, CA1422, CS0618, CS1570, CS8600, CS8602, CS8604, SYSLIB0014, IL2026, IL3050
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;

namespace AiSmartEmailer.Services;

/// <summary>
/// High-performance Pure-C# + Native Android PDF &amp; Image Helper (v4.0):
///   1. Renders PDF pages into crisp Android Bitmaps and compact JPEGs (&lt; 300 KB) for fast OCR upload.
///   2. Decodes shared images with OOM-safe bounds downsampling AND pure-C# EXIF orientation rotation.
///   3. Fast non-backtracking Digital PDF stream parser (skips image/font streams, decodes both literal (...) and &lt;hex&gt;
///      strings with 1-byte &amp; 2-byte /ToUnicode CMaps, and preserves horizontal baselines via Td/TD/Tm/TJ operators).
///   4. 4-Tier Subject Extractor (Explicit Subject block -&gt; Positional Pre-Salutation block -&gt; Official Order/Circular/Notice
///      block -&gt; Letterhead-filtered Semantic Heading).
/// </summary>
public static class LocalPdfAndImageHelper
{
    public sealed class DetailedSubjectMatch
    {
        public string SubjectLine { get; init; } = string.Empty;
        public bool FoundInDocument { get; init; }
        public bool FoundExplicitSubject { get; init; }
        public int ConfidenceScore { get; init; }
        public string MatchTier { get; init; } = string.Empty;

        public DetailedSubjectMatch() { }

        public DetailedSubjectMatch(
            string subjectLine,
            bool foundInDocument,
            bool foundExplicitSubject,
            int confidenceScore,
            string matchTier)
        {
            SubjectLine = subjectLine;
            FoundInDocument = foundInDocument;
            FoundExplicitSubject = foundExplicitSubject;
            ConfidenceScore = confidenceScore;
            MatchTier = matchTier;
        }
    }

    private static readonly string[] KnownScannerWatermarks = new[]
    {
        "AnyScanner",
        "CamScanner",
        "Scanned with CamScanner",
        "Scanned by CamScanner",
        "Adobe Scan",
        "TapScanner",
        "Scanned with TapScanner",
        "ClearScanner",
        "Clear Scanner",
        "vFlat",
        "FastScanner",
        "Microsoft Lens",
        "Office Lens",
        "DocScanner",
        "Genius Scan",
        "Notebloc",
        "Kaagaz",
        "Oken Scanner",
        "Scanner App"
    };

    private static readonly string[] LetterheadAndAddressNoisePrefixes = new[]
    {
        "Government of",
        "Union Territory of",
        "U.T. of",
        "UT of",
        "Jammu and Kashmir",
        "Jammu & Kashmir",
        "Office of the",
        "Office of",
        "Directorate of",
        "Department of",
        "Ministry of",
        "Chief Education Officer",
        "Zonal Education Officer",
        "Deputy Commissioner",
        "District Development",
        "The Principal",
        "The Headmaster",
        "The Director",
        "The Secretary",
        "Samagra Shiksha",
        "(Address",
        "Address:",
        "Email:",
        "E-mail:",
        "Web:",
        "Website:",
        "Phone:",
        "Tele:",
        "Tel:",
        "Fax:",
        "Contact:",
        "Pin:",
        "PIN-",
        "No.",
        "No:",
        "No :",
        "Ref No",
        "Dated",
        "Date:",
        "Copy to",
        "Endst"
    };

    /// <summary>
    /// Renders the first page(s) of a PDF file into high-resolution Android Bitmaps using Android's native PdfRenderer.
    /// </summary>
    public static List<Bitmap> RenderPdfPagesToBitmaps(string pdfFilePath, int maxPages = 1, bool enhanceContrast = true)
    {
        var bitmaps = new List<Bitmap>();
        try
        {
            using var jFile = new Java.IO.File(pdfFilePath);
            using var pfd = ParcelFileDescriptor.Open(jFile, ParcelFileMode.ReadOnly);
            if (pfd == null) return bitmaps;

            using var renderer = new PdfRenderer(pfd);
            int pagesToRender = Math.Min(renderer.PageCount, Math.Max(1, maxPages));

            for (int i = 0; i < pagesToRender; i++)
            {
                using var page = renderer.OpenPage(i);
                if (page == null) continue;

                // Optimal ~1550px width balances sharp OCR character edges with fast rendering & compact JPEG size
                int width = Math.Clamp(page.Width * 3, 1250, 1650);
                int height = Math.Max(1, (int)((double)page.Height / Math.Max(1, page.Width) * width));

                var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
                using var canvas = new Canvas(bitmap);
                canvas.DrawColor(Color.White);

                page.Render(bitmap, null, null, PdfRenderMode.ForDisplay);
                bitmaps.Add(bitmap);
            }
        }
        catch
        {
            // Ignore rendering errors
        }

        return bitmaps;
    }

    /// <summary>
    /// Loads a shared image file into an Android Bitmap using OOM-safe bounds downsampling
    /// and pure-C# EXIF orientation correction (fixing sideways camera photos).
    /// </summary>
    public static Bitmap? LoadBitmapForOcr(string imagePath, bool enhanceContrast = true)
    {
        try
        {
            var boundsOptions = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(imagePath, boundsOptions);

            int srcW = boundsOptions.OutWidth;
            int srcH = boundsOptions.OutHeight;
            if (srcW <= 0 || srcH <= 0)
            {
                return BitmapFactory.DecodeFile(imagePath);
            }

            int maxDim = 1750;
            int sampleSize = 1;
            while ((srcW / (sampleSize * 2)) >= maxDim || (srcH / (sampleSize * 2)) >= maxDim)
            {
                sampleSize *= 2;
            }

            var decodeOptions = new BitmapFactory.Options
            {
                InSampleSize = sampleSize
            };

            var decoded = BitmapFactory.DecodeFile(imagePath, decodeOptions);
            if (decoded == null) return null;

            // Apply EXIF rotation if the camera stored the JPEG rotated (90 / 180 / 270 degrees)
            int rotationDegrees = ReadJpegExifRotationDegrees(imagePath);
            if (rotationDegrees != 0)
            {
                using var matrix = new Matrix();
                matrix.PostRotate(rotationDegrees);
                var rotated = Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, matrix, true);
                if (rotated != null && !ReferenceEquals(rotated, decoded))
                {
                    decoded.Dispose();
                    decoded = rotated;
                }
            }

            // Final exact scale if still larger than maxDim
            if (decoded.Width > maxDim || decoded.Height > maxDim)
            {
                float ratio = Math.Min((float)maxDim / decoded.Width, (float)maxDim / decoded.Height);
                int newW = Math.Max(1, (int)(decoded.Width * ratio));
                int newH = Math.Max(1, (int)(decoded.Height * ratio));
                var scaled = Bitmap.CreateScaledBitmap(decoded, newW, newH, true);
                if (scaled != null && !ReferenceEquals(scaled, decoded))
                {
                    decoded.Dispose();
                    decoded = scaled;
                }
            }

            return decoded;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pure-C# JPEG EXIF Orientation reader (parses APP1 TIFF tag 0x0112 with zero Android.Media dependencies).
    /// Returns 90, 180, 270, or 0.
    /// </summary>
    private static int ReadJpegExifRotationDegrees(string imagePath)
    {
        try
        {
            using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 32) return 0;

            byte[] buf = new byte[(int)Math.Min(65536L, fs.Length)];
            int read = fs.Read(buf, 0, buf.Length);
            if (read < 14 || buf[0] != 0xFF || buf[1] != 0xD8) return 0;

            int pos = 2;
            while (pos + 4 < read && buf[pos] == 0xFF)
            {
                byte marker = buf[pos + 1];
                if (marker == 0xDA || marker == 0xD9) break; // SOS or EOI

                int segLen = (buf[pos + 2] << 8) | buf[pos + 3];
                if (segLen < 2 || pos + 2 + segLen > read) break;

                // APP1 marker (0xFFE1) starting with "Exif\0\0"
                if (marker == 0xE1 && segLen >= 14
                    && buf[pos + 4] == (byte)'E' && buf[pos + 5] == (byte)'x'
                    && buf[pos + 6] == (byte)'i' && buf[pos + 7] == (byte)'f'
                    && buf[pos + 8] == 0 && buf[pos + 9] == 0)
                {
                    int tiffStart = pos + 10;
                    bool littleEndian = buf[tiffStart] == (byte)'I' && buf[tiffStart + 1] == (byte)'I';
                    int ifdOffset = ReadInt32(buf, tiffStart + 4, littleEndian);
                    int dirStart = tiffStart + ifdOffset;
                    if (dirStart + 2 > read) return 0;

                    int numEntries = ReadUInt16(buf, dirStart, littleEndian);
                    for (int i = 0; i < numEntries; i++)
                    {
                        int entryPos = dirStart + 2 + (i * 12);
                        if (entryPos + 12 > read) break;

                        int tag = ReadUInt16(buf, entryPos, littleEndian);
                        if (tag == 0x0112) // Orientation tag
                        {
                            int orientation = ReadUInt16(buf, entryPos + 8, littleEndian);
                            return orientation switch
                            {
                                6 => 90,
                                3 => 180,
                                8 => 270,
                                _ => 0
                            };
                        }
                    }
                    return 0;
                }

                pos += 2 + segLen;
            }
        }
        catch
        {
            // Ignore EXIF parse errors
        }
        return 0;
    }

    private static int ReadUInt16(byte[] buf, int offset, bool littleEndian)
    {
        return littleEndian
            ? (buf[offset] | (buf[offset + 1] << 8))
            : ((buf[offset] << 8) | buf[offset + 1]);
    }

    private static int ReadInt32(byte[] buf, int offset, bool littleEndian)
    {
        return littleEndian
            ? (buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24))
            : ((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);
    }

    /// <summary>
    /// Crops the top portion of a document page (default top 78%, covering deep headers + Subject blocks)
    /// and compresses it to a compact JPEG guaranteed to be under maxBytes (default 260 KB).
    /// </summary>
    public static byte[] CropTopPortionToJpegBytes(Bitmap bitmap, double heightFraction = 0.78, int maxBytes = 260_000)
    {
        int cropH = Math.Clamp((int)(bitmap.Height * heightFraction), 120, bitmap.Height);
        using var cropped = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, cropH);
        return CompressBitmapUnderLimit(cropped, maxBytes);
    }

    /// <summary>
    /// Compresses a full Bitmap to JPEG bytes guaranteed to stay below maxBytes (default 340 KB for fast mobile upload).
    /// </summary>
    public static byte[] CompressBitmapUnderLimit(Bitmap bitmap, int maxBytes = 340_000)
    {
        int quality = 82;
        while (quality >= 42)
        {
            using var ms = new MemoryStream();
            bitmap.Compress(Bitmap.CompressFormat.Jpeg!, quality, ms);
            byte[] data = ms.ToArray();
            if (data.Length <= maxBytes || quality <= 42)
            {
                return data;
            }
            quality -= 10;
        }

        using var fallbackMs = new MemoryStream();
        bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 45, fallbackMs);
        return fallbackMs.ToArray();
    }

    /// <summary>
    /// Strips null bytes (\0), Unicode replacement chars (\uFFFD), and control characters.
    /// </summary>
    public static string SanitizeReadableString(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (c == '\0' || c == '\uFFFD' || c == '\uFEFF')
                continue;

            if (c == '\r' || c == '\n' || c == '\t' || !char.IsControl(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Validates that a candidate subject line is genuine human-readable words and NOT
    /// a CID-shifted glyph sequence ("$Q\\6FDQQHU") or a scanner watermark ("AnyScanner").
    /// </summary>
    public static bool IsValidHumanReadableSubject(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        string cleaned = SanitizeReadableString(candidate);
        if (cleaned.Length < 4) return false;

        foreach (var wm in KnownScannerWatermarks)
        {
            if (cleaned.Equals(wm, StringComparison.OrdinalIgnoreCase)
                || cleaned.Equals($"Document ({wm})", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (Regex.IsMatch(cleaned, @"\\[0-9A-Za-z]|[\$\\]{2,}"))
            return false;

        int letters = 0;
        int vowels = 0;
        int weirdSymbols = 0;

        foreach (char c in cleaned)
        {
            if (char.IsLetter(c))
            {
                letters++;
                if ("AEIOUaeiou".IndexOf(c) >= 0 || c > 127)
                    vowels++;
            }
            else if (!char.IsDigit(c) && !char.IsWhiteSpace(c) && ".,;:-–—/()'\"&%#No[]".IndexOf(c) < 0)
            {
                weirdSymbols++;
            }
        }

        if (letters < 4 || vowels == 0) return false;
        if (weirdSymbols > letters / 3) return false;

        return true;
    }

    /// <summary>
    /// Fast, non-backtracking Digital PDF text extractor:
    ///   - Scans PDF streams using fast index lookups (no multi-MB catastrophic Regex backtracking).
    ///   - Skips /Subtype /Image and /FontFile binary streams so scanned PDFs finish in &lt; 15ms.
    ///   - Decodes 1-byte and 2-byte /ToUnicode CMaps, literal (...) strings, and &lt;hex&gt; strings.
    ///   - Tracks Td/TD/Tm/T* vertical positioning so words on the same horizontal line stay on the same line!
    /// </summary>
    public static string ExtractGenuineDigitalTextFromPdf(string pdfFilePath)
    {
        try
        {
            var fileInfo = new FileInfo(pdfFilePath);
            if (!fileInfo.Exists || fileInfo.Length == 0) return string.Empty;

            // Read at most the first 8 MB so huge multi-page scanned PDFs never cause memory spikes
            int bytesToRead = (int)Math.Min(fileInfo.Length, 8_000_000L);
            byte[] rawBytes = new byte[bytesToRead];
            using (var fs = new FileStream(pdfFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int totalRead = 0;
                while (totalRead < bytesToRead)
                {
                    int r = fs.Read(rawBytes, totalRead, bytesToRead - totalRead);
                    if (r <= 0) break;
                    totalRead += r;
                }
            }

            string latin1 = Encoding.Latin1.GetString(rawBytes);
            var deflatedStreams = new List<string>();

            int searchPos = 0;
            int maxStreamsToInspect = 45;
            int inspectedStreams = 0;

            while (searchPos < latin1.Length && inspectedStreams < maxStreamsToInspect)
            {
                int streamIdx = latin1.IndexOf("stream", searchPos, StringComparison.Ordinal);
                if (streamIdx < 0) break;

                // Ensure "stream" is not "endstream"
                if (streamIdx >= 3 && string.CompareOrdinal(latin1, streamIdx - 3, "end", 0, 3) == 0)
                {
                    searchPos = streamIdx + 6;
                    continue;
                }

                // Check the dictionary preceding "stream" (up to 350 chars back) to skip Image and Font binary streams!
                int dictLookbackStart = Math.Max(0, streamIdx - 350);
                string dictHeader = latin1.Substring(dictLookbackStart, streamIdx - dictLookbackStart);

                int dataStart = streamIdx + 6;
                if (dataStart < latin1.Length && latin1[dataStart] == '\r') dataStart++;
                if (dataStart < latin1.Length && latin1[dataStart] == '\n') dataStart++;

                int endStreamIdx = latin1.IndexOf("endstream", dataStart, StringComparison.Ordinal);
                if (endStreamIdx < 0) break;

                searchPos = endStreamIdx + 9;

                if (dictHeader.Contains("/Subtype /Image", StringComparison.OrdinalIgnoreCase)
                    || dictHeader.Contains("/Subtype/Image", StringComparison.OrdinalIgnoreCase)
                    || dictHeader.Contains("/FontFile", StringComparison.OrdinalIgnoreCase)
                    || dictHeader.Contains("/DCTDecode", StringComparison.OrdinalIgnoreCase)
                    || dictHeader.Contains("/JPXDecode", StringComparison.OrdinalIgnoreCase)
                    || dictHeader.Contains("/CCITTFaxDecode", StringComparison.OrdinalIgnoreCase)
                    || dictHeader.Contains("/JBIG2Decode", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int streamLen = endStreamIdx - dataStart;
                if (streamLen > 0 && latin1[endStreamIdx - 1] == '\n') streamLen--;
                if (streamLen > 0 && latin1[dataStart + streamLen - 1] == '\r') streamLen--;

                if (streamLen <= 0 || streamLen > 1_500_000) continue;
                inspectedStreams++;

                string decodedChunk = TryInflateOrReadPdfStream(rawBytes, dataStart, streamLen, dictHeader);
                if (!string.IsNullOrWhiteSpace(decodedChunk))
                {
                    deflatedStreams.Add(decodedChunk);
                }
            }

            if (deflatedStreams.Count == 0) return string.Empty;

            var cmapDict = ParseToUnicodeCMaps(deflatedStreams);
            var btEtRegex = new Regex(@"\bBT\b(.*?)\bET\b", RegexOptions.Singleline);

            var sb = new StringBuilder();
            foreach (var chunk in deflatedStreams)
            {
                if (chunk.Contains("begincmap", StringComparison.Ordinal)) continue;

                foreach (Match btMatch in btEtRegex.Matches(chunk))
                {
                    ExtractTextOperatorsWithPositioning(btMatch.Groups[1].Value, cmapDict, sb);
                    sb.AppendLine();
                }

                if (sb.Length > 12_000) break; // First 1-2 pages are more than enough for the Subject line
            }

            string cleaned = CleanAndFilterDigitalLines(sb.ToString());

            // If digital text has fewer than 45 characters and no Subject keyword, treat as scanned PDF
            if (cleaned.Length < 45 && !Regex.IsMatch(cleaned, @"\b(?:Subject|Subj|Sub|Regarding|Ref)\b", RegexOptions.IgnoreCase))
            {
                return string.Empty;
            }

            return cleaned;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryInflateOrReadPdfStream(byte[] fullPdf, int offset, int count, string dictHeader)
    {
        bool isFlate = dictHeader.Contains("/FlateDecode", StringComparison.OrdinalIgnoreCase)
                       || dictHeader.Contains("/Fl", StringComparison.Ordinal);

        if (!isFlate)
        {
            string raw = Encoding.Latin1.GetString(fullPdf, offset, Math.Min(count, 250_000));
            if (raw.Contains("BT", StringComparison.Ordinal) || raw.Contains("begincmap", StringComparison.Ordinal))
            {
                return raw;
            }
        }

        try
        {
            using var ms = new MemoryStream(fullPdf, offset, count);
            using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.Latin1);
            return reader.ReadToEnd();
        }
        catch
        {
            try
            {
                if (count > 2)
                {
                    using var ms = new MemoryStream(fullPdf, offset + 2, count - 2);
                    using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                    using var reader = new StreamReader(deflate, Encoding.Latin1);
                    return reader.ReadToEnd();
                }
            }
            catch { }
        }
        return string.Empty;
    }

    private static Dictionary<int, string> ParseToUnicodeCMaps(List<string> streams)
    {
        var map = new Dictionary<int, string>();
        var rangeRegex = new Regex(@"<([0-9A-Fa-f]{2,4})>\s*<([0-9A-Fa-f]{2,4})>\s*<([0-9A-Fa-f]{2,8})>");
        var charRegex = new Regex(@"<([0-9A-Fa-f]{2,4})>\s*<([0-9A-Fa-f]{2,8})>");

        foreach (var s in streams)
        {
            if (!s.Contains("begincmap", StringComparison.Ordinal)) continue;

            foreach (Match m in rangeRegex.Matches(s))
            {
                int startCode = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber);
                int endCode = int.Parse(m.Groups[2].Value, NumberStyles.HexNumber);
                string targetHex = m.Groups[3].Value;
                if (targetHex.Length <= 4)
                {
                    int targetStart = int.Parse(targetHex, NumberStyles.HexNumber);
                    for (int code = startCode; code <= endCode && (code - startCode) < 512; code++)
                    {
                        map[code] = ((char)(targetStart + (code - startCode))).ToString();
                    }
                }
            }

            foreach (Match m in charRegex.Matches(s))
            {
                int code = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber);
                string targetHex = m.Groups[2].Value;
                if (!map.ContainsKey(code))
                {
                    map[code] = DecodeHexUnicodeTarget(targetHex);
                }
            }
        }

        return map;
    }

    private static string DecodeHexUnicodeTarget(string hex)
    {
        try
        {
            if (hex.Length <= 4)
            {
                int val = int.Parse(hex, NumberStyles.HexNumber);
                return ((char)val).ToString();
            }

            var sb = new StringBuilder();
            for (int i = 0; i + 3 < hex.Length; i += 4)
            {
                int val = int.Parse(hex.Substring(i, 4), NumberStyles.HexNumber);
                sb.Append((char)val);
            }
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static readonly Regex PdfBtOperatorRegex = new(
        @"(?<tm>-?\d+(?:\.\d+)?\s+-?\d+(?:\.\d+)?\s+-?\d+(?:\.\d+)?\s+-?\d+(?:\.\d+)?\s+-?\d+(?:\.\d+)?\s+(?<tmY>-?\d+(?:\.\d+)?)\s+Tm)|" +
        @"(?<td>-?\d+(?:\.\d+)?\s+(?<tdY>-?\d+(?:\.\d+)?)\s+T[dD])|" +
        @"(?<tstar>T\*)|" +
        @"(?<tj>(?:\((?:\\.|[^\\()])*\)|<[0-9A-Fa-f\s]*>)\s*(?:Tj|'|""|\s))|" +
        @"(?<tjarr>\[(?:\((?:\\.|[^\\()])*\)|<[0-9A-Fa-f\s]*>|[-\d.\s])*\]\s*TJ)",
        RegexOptions.Singleline);

    private static readonly Regex InnerTokenRegex = new(
        @"(?<lit>\((?:\\.|[^\\()])*\))|(?<hex><[0-9A-Fa-f\s]+>)|(?<kern>-?\d+(?:\.\d+)?)",
        RegexOptions.Singleline);

    private static void ExtractTextOperatorsWithPositioning(string btContent, Dictionary<int, string> cmap, StringBuilder sb)
    {
        double? lastTmY = null;

        foreach (Match m in PdfBtOperatorRegex.Matches(btContent))
        {
            if (m.Groups["tstar"].Success)
            {
                sb.AppendLine();
                continue;
            }

            if (m.Groups["tdY"].Success)
            {
                if (double.TryParse(m.Groups["tdY"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double dy))
                {
                    if (Math.Abs(dy) > 1.5)
                        sb.AppendLine();
                    else
                        sb.Append(' ');
                }
                continue;
            }

            if (m.Groups["tmY"].Success)
            {
                if (double.TryParse(m.Groups["tmY"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                {
                    if (lastTmY.HasValue && Math.Abs(y - lastTmY.Value) > 2.5)
                        sb.AppendLine();
                    else if (lastTmY.HasValue)
                        sb.Append(' ');

                    lastTmY = y;
                }
                continue;
            }

            string opChunk = m.Groups["tj"].Success ? m.Groups["tj"].Value : m.Groups["tjarr"].Value;
            foreach (Match tm in InnerTokenRegex.Matches(opChunk))
            {
                if (tm.Groups["lit"].Success)
                {
                    string lit = tm.Groups["lit"].Value;
                    if (lit.Length >= 2)
                    {
                        string inner = UnescapePdfLiteralString(lit.Substring(1, lit.Length - 2));
                        sb.Append(DecodePdfLiteralString(inner, cmap));
                    }
                }
                else if (tm.Groups["hex"].Success)
                {
                    string hex = tm.Groups["hex"].Value;
                    if (hex.Length >= 2)
                    {
                        sb.Append(DecodePdfHexString(hex.Substring(1, hex.Length - 2), cmap));
                    }
                }
                else if (tm.Groups["kern"].Success)
                {
                    if (double.TryParse(tm.Groups["kern"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double k) && k < -140)
                    {
                        sb.Append(' ');
                    }
                }
            }
        }
    }

    private static string UnescapePdfLiteralString(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }

            char next = s[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append(' '); break;
                case 't': sb.Append(' '); break;
                case 'b': break;
                case 'f': sb.Append(' '); break;
                case '(': sb.Append('('); break;
                case ')': sb.Append(')'); break;
                case '\\': sb.Append('\\'); break;
                default:
                    if (next >= '0' && next <= '7')
                    {
                        int oct = next - '0';
                        if (i + 1 < s.Length && s[i + 1] >= '0' && s[i + 1] <= '7')
                        {
                            oct = (oct * 8) + (s[++i] - '0');
                            if (i + 1 < s.Length && s[i + 1] >= '0' && s[i + 1] <= '7')
                            {
                                oct = (oct * 8) + (s[++i] - '0');
                            }
                        }
                        sb.Append((char)oct);
                    }
                    else
                    {
                        sb.Append(next);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static string DecodePdfLiteralString(string raw, Dictionary<int, string> cmap)
    {
        if (raw.Length >= 2 && raw[0] == '\xFE' && raw[1] == '\xFF')
        {
            byte[] bytes = Encoding.Latin1.GetBytes(raw, 2, raw.Length - 2);
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        var outSb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (c == '\0') continue;
            if (cmap.Count > 0 && cmap.TryGetValue((int)c, out string? mapped))
            {
                outSb.Append(mapped);
            }
            else if (!char.IsControl(c) || c == '\n')
            {
                outSb.Append(c);
            }
        }
        return outSb.ToString();
    }

    private static string DecodePdfHexString(string rawHex, Dictionary<int, string> cmap)
    {
        var cleanHex = new StringBuilder(rawHex.Length);
        foreach (char c in rawHex)
        {
            if (!char.IsWhiteSpace(c)) cleanHex.Append(c);
        }
        if (cleanHex.Length % 2 != 0) cleanHex.Append('0');
        string hex = cleanHex.ToString();

        var outSb = new StringBuilder();

        bool preferTwoByte = hex.Length % 4 == 0 && (cmap.Keys.Any(k => k > 255) || hex.StartsWith("00", StringComparison.Ordinal));
        if (preferTwoByte)
        {
            for (int i = 0; i + 3 < hex.Length; i += 4)
            {
                int code16 = int.Parse(hex.Substring(i, 4), NumberStyles.HexNumber);
                if (cmap.TryGetValue(code16, out string? mapped16))
                {
                    outSb.Append(mapped16);
                }
                else if (code16 >= 32 && code16 < 0xFFFE)
                {
                    outSb.Append((char)code16);
                }
            }
            if (outSb.Length > 0) return outSb.ToString();
        }

        for (int i = 0; i + 1 < hex.Length; i += 2)
        {
            int code8 = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber);
            if (cmap.TryGetValue(code8, out string? mapped8))
            {
                outSb.Append(mapped8);
            }
            else if (code8 >= 32)
            {
                outSb.Append((char)code8);
            }
        }

        return outSb.ToString();
    }

    private static string CleanAndFilterDigitalLines(string input)
    {
        string sanitized = SanitizeReadableString(input);
        if (string.IsNullOrWhiteSpace(sanitized)) return string.Empty;

        var validLines = new List<string>();
        foreach (var rawLine in sanitized.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = Regex.Replace(rawLine, @"\s+", " ").Trim();
            if (line.Length < 3) continue;

            if (KnownScannerWatermarks.Any(w => line.Equals(w, StringComparison.OrdinalIgnoreCase)
                                                || line.Contains("Scanned with ", StringComparison.OrdinalIgnoreCase)
                                                || line.Contains("Scanned by ", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (IsValidHumanReadableSubject(line) || Regex.IsMatch(line, @"^(?:Sub|Subject|Sir|Madam|To)\b", RegexOptions.IgnoreCase))
            {
                validLines.Add(line);
            }
        }

        return string.Join("\n", validLines);
    }

    /// <summary>
    /// Backward-compatible wrapper around ExtractSubjectDetailed.
    /// </summary>
    public static (string SubjectLine, bool FoundInDocument) ExtractSubjectFromTextOrLines(string fullText, string? fallbackFileName = null)
    {
        var detailed = ExtractSubjectDetailed(fullText, fallbackFileName);
        return (detailed.SubjectLine, detailed.FoundInDocument);
    }

    /// <summary>
    /// 4-Tier Intelligent Subject Line Extractor:
    ///   Tier 1 (Confidence 98): Explicit Subject/Sub/Ref/Regarding/विषय prefix (tolerant of OCR typos &amp; multi-line layouts).
    ///   Tier 2 (Confidence 86): Positional Pre-Salutation Subject Block (lines directly above "Sir," / "Madam," after "To" / "Dated" when the "Subject:" label was cut off or blurred).
    ///   Tier 3 (Confidence 80): Official Government Order / Circular / Notice / Notification / Memorandum descriptive block.
    ///   Tier 4 (Confidence 55): Scored Semantic Document Heading (strictly filtering out letterhead, department, address, and contact noise).
    /// </summary>
    public static DetailedSubjectMatch ExtractSubjectDetailed(string fullText, string? fallbackFileName = null)
    {
        string sanitizedText = SanitizeReadableString(fullText);
        if (!string.IsNullOrWhiteSpace(sanitizedText))
        {
            var rawLines = sanitizedText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => Regex.Replace(l, @"\s+", " ").Trim())
                .Where(l => l.Length > 0 && !KnownScannerWatermarks.Any(w => l.Equals(w, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var subjectStartLineRegex = new Regex(
                @"^(?:.*?)\b(?:Subject|Subj|Sub|SUB|SUBJECT|S\s*u\s*b\s*j\s*e\s*c\s*t|5ubject|Subjeet|Subjcct|Subjeci|Subjoct|Subect|Subjct|\$ubject|Suoject|Regarding|विषय)\s*(?:[:.;=\-–—~|/]+|\s+[\-–—:]\s*|\s+(?=(?:Reg|Release|Submission|Preparation|Forwarding|Sanction|Grant|Appointment|Transfer|Conduct|Verification|Adjustment|Provision|Deputation|Issuance|Implementation|Extension|Engagement|Nomination|Selection|Fixation|Confirmation|Regularization|Allotment|Payment|Recovery|Supply|Purchase|Approval|Permission|Clarification|Information|Compliance|Meeting|Training|Inspection|Posting|Promotion|Dismissal|Suspension|Leave|Joining|Relieving|Distribution|Enrollment|Admission|Examination|Result|Scholarship|Budget|Audit|Utilization|Pending|Action|Complaint|Enquiry|Inquiry|Report|Proposal|Request|Application)\b))\s*(.*)$",
                RegexOptions.IgnoreCase);

            var standaloneSubjectLabelRegex = new Regex(
                @"^(?:Subject|Subj|Sub|5ubject|Subjeet|Subjcct|Regarding|विषय)\s*[:.;=\-–—~|/]*\s*$",
                RegexOptions.IgnoreCase);

            var salutationLineRegex = new Regex(
                @"^(?:(?:Dear|Respected|Worthy|Hon'ble|Honourable)\s+)?(?:Sir|S1r|5ir|Slr|Madam|Ma'am|Sir\s*/\s*Madam|Madam\s*/\s*Sir|Gentlemen|Mahoday|महोदय)\s*[,.:;\-'""]?\s*$",
                RegexOptions.IgnoreCase);

            var stopMarkerRegex = new Regex(
                @"^(?:(?:Dear|Respected|Worthy|Hon'ble|Honourable)\s+)?(?:Sir|S1r|5ir|Slr|Madam|Ma'am|Sir\s*/\s*Madam|Madam\s*/\s*Sir|Gentlemen|Mahoday|महोदय)\s*[,.:;\-'""]?\s*$|" +
                @"^(?:Ref|Reference|Memo\s*No|Endst|Copy\s+to)\s*[:.\-–—]|" +
                @"^(?:Kindly\s+refer|With\s+reference|In\s+reference|Please\s+refer|It\s+is\s+submitted|In\s+view\s+of|In\s+continuation|I\s+am\s+directed|Apropos|Whereas|Sanction\s+is\s+hereby|In\s+pursuance|Pursuant\s+to|As\s+desired|Enclosed\s+please|Please\s+find\s+enclosed|With\s+profound|Most\s+respectfully|Undersigned\s+is)\b",
                RegexOptions.IgnoreCase);

            var standaloneDateOrNoRegex = new Regex(
                @"^(?:Dated?\s*[:.\-–—]*\s*\d{1,2}\s*[/.\-]\s*\d{1,2}\s*[/.\-]\s*\d{2,4}|No\.?\s*[:.\-–—]?\s*[A-Za-z0-9/\-_]{3,}(?:\s+Dated.*)?)\s*$",
                RegexOptions.IgnoreCase);

            // =========================================================================
            // TIER 1: Explicit "Subject:" / "Sub:" Prefix Block Extraction (Confidence 98)
            // =========================================================================
            for (int i = 0; i < rawLines.Count; i++)
            {
                var match = subjectStartLineRegex.Match(rawLines[i]);
                bool isStandaloneLabel = !match.Success && standaloneSubjectLabelRegex.IsMatch(rawLines[i]);
                if (!match.Success && !isStandaloneLabel) continue;

                var collectedParts = new List<string>();
                if (match.Success)
                {
                    string firstRemainder = CleanSubjectFragment(match.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(firstRemainder) && !standaloneDateOrNoRegex.IsMatch(firstRemainder))
                    {
                        collectedParts.Add(firstRemainder);
                    }
                }

                for (int j = i + 1; j < Math.Min(rawLines.Count, i + 12); j++)
                {
                    string nextLine = rawLines[j].Trim();

                    if (standaloneDateOrNoRegex.IsMatch(nextLine))
                        continue;

                    if (stopMarkerRegex.IsMatch(nextLine))
                        break;

                    string cleanedNext = CleanSubjectFragment(nextLine);
                    if (!string.IsNullOrWhiteSpace(cleanedNext))
                    {
                        collectedParts.Add(cleanedNext);

                        string joinedSoFar = string.Join(" ", collectedParts);
                        if (joinedSoFar.Length >= 30
                            && (cleanedNext.EndsWith('.') || cleanedNext.EndsWith("-reg", StringComparison.OrdinalIgnoreCase) || cleanedNext.EndsWith("reg.", StringComparison.OrdinalIgnoreCase) || cleanedNext.EndsWith("thereof.", StringComparison.OrdinalIgnoreCase))
                            && !Regex.IsMatch(cleanedNext, @"\b(?:No|Dt|Dr|Mr|Mrs|Ms|St|Govt|Dept|Deptt|Dist|Distt|Sec|Sr|Jr|Ltd|Co|viz|vide|Ref|Smt|Sh)\.$", RegexOptions.IgnoreCase))
                        {
                            if (j + 1 < rawLines.Count)
                            {
                                string peek = rawLines[j + 1].Trim();
                                if (stopMarkerRegex.IsMatch(peek)
                                    || peek.StartsWith("Sir", StringComparison.OrdinalIgnoreCase)
                                    || peek.StartsWith("Madam", StringComparison.OrdinalIgnoreCase)
                                    || peek.StartsWith("Kindly", StringComparison.OrdinalIgnoreCase)
                                    || peek.StartsWith("In view", StringComparison.OrdinalIgnoreCase)
                                    || peek.StartsWith("With reference", StringComparison.OrdinalIgnoreCase))
                                {
                                    break;
                                }
                            }
                        }
                    }
                }

                string finalSubject = Regex.Replace(string.Join(" ", collectedParts), @"\s+", " ").Trim();
                if (IsValidHumanReadableSubject(finalSubject))
                {
                    return new DetailedSubjectMatch(finalSubject, true, true, 98, "Explicit Subject Line");
                }
            }

            // =========================================================================
            // TIER 2: Positional Pre-Salutation Subject Extractor (Confidence 86)
            // =========================================================================
            int salutationIdx = -1;
            for (int i = 2; i < Math.Min(rawLines.Count, 32); i++)
            {
                if (salutationLineRegex.IsMatch(rawLines[i]))
                {
                    salutationIdx = i;
                    break;
                }
            }

            if (salutationIdx >= 2)
            {
                var preSalutationLines = new List<string>();
                for (int k = salutationIdx - 1; k >= Math.Max(0, salutationIdx - 6); k--)
                {
                    string line = rawLines[k].Trim();
                    if (standaloneDateOrNoRegex.IsMatch(line))
                        break;

                    if (IsRecipientOrLetterheadLine(line))
                        break;

                    if (line.StartsWith("Ref", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith("Reference", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string cleaned = CleanSubjectFragment(line);
                    if (IsValidHumanReadableSubject(cleaned))
                    {
                        preSalutationLines.Insert(0, cleaned);
                    }
                }

                if (preSalutationLines.Count > 0)
                {
                    string positionalSubject = Regex.Replace(string.Join(" ", preSalutationLines), @"\s+", " ").Trim();
                    if (positionalSubject.Length >= 12 && IsValidHumanReadableSubject(positionalSubject))
                    {
                        return new DetailedSubjectMatch(positionalSubject, true, true, 86, "Pre-Salutation Subject Block");
                    }
                }
            }

            // =========================================================================
            // TIER 3: Official Document Type Heading Block (ORDER / CIRCULAR / NOTICE / NOTIFICATION) (Confidence 80)
            // =========================================================================
            var docTypeHeaderRegex = new Regex(
                @"^(?:GOVERNMENT\s+ORDER|OFFICE\s+ORDER|ORDER|CIRCULAR|NOTICE|NOTIFICATION|OFFICE\s+MEMORANDUM|MEMORANDUM|PRESS\s+RELEASE|TENDER\s+NOTICE|CORRIGENDUM|ADDENDUM)\b(.*)$",
                RegexOptions.IgnoreCase);

            for (int i = 0; i < Math.Min(rawLines.Count, 22); i++)
            {
                var docMatch = docTypeHeaderRegex.Match(rawLines[i]);
                if (!docMatch.Success) continue;

                var parts = new List<string> { rawLines[i] };
                for (int j = i + 1; j < Math.Min(rawLines.Count, i + 4); j++)
                {
                    string next = rawLines[j].Trim();
                    if (standaloneDateOrNoRegex.IsMatch(next)) continue;
                    if (stopMarkerRegex.IsMatch(next)) break;
                    if (IsValidHumanReadableSubject(next))
                    {
                        parts.Add(CleanSubjectFragment(next));
                        if (string.Join(" ", parts).Length >= 55) break;
                    }
                }

                string combinedOrder = Regex.Replace(string.Join(" — ", parts), @"\s+", " ").Trim();
                if (combinedOrder.Length >= 15 && IsValidHumanReadableSubject(combinedOrder))
                {
                    return new DetailedSubjectMatch(combinedOrder, true, true, 80, "Official Order/Circular Block");
                }
            }

            // =========================================================================
            // TIER 4: Scored Semantic Document Heading Fallback (Confidence 55)
            // =========================================================================
            string? bestHeading = null;
            int bestScore = -1;

            for (int i = 0; i < Math.Min(rawLines.Count, 25); i++)
            {
                string line = rawLines[i];
                if (line.Length < 16 || line.Length > 190) continue;
                if (IsRecipientOrLetterheadLine(line)) continue;
                if (standaloneDateOrNoRegex.IsMatch(line)) continue;
                if (!IsValidHumanReadableSubject(line)) continue;

                int score = 50;
                if (Regex.IsMatch(line, @"\b(?:regarding|submission|release|sanction|preparation|list|report|statement|information|application|appointment|transfer|approval|funds|budget|schedule|notice|circular|order|verification)\b", RegexOptions.IgnoreCase))
                    score += 25;
                if (line.All(c => !char.IsLetter(c) || char.IsUpper(c)))
                    score -= 10;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestHeading = line;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestHeading))
            {
                return new DetailedSubjectMatch(bestHeading, true, false, 55, "Document Heading");
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackFileName))
        {
            string cleanName = System.IO.Path.GetFileNameWithoutExtension(fallbackFileName)
                .Replace('_', ' ')
                .Replace('-', ' ');
            cleanName = Regex.Replace(cleanName, @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(cleanName))
                return new DetailedSubjectMatch($"Document ({cleanName})", false, false, 15, "Filename Fallback");
        }

        return new DetailedSubjectMatch("Official Document for Review", false, false, 10, "Default Fallback");
    }

    private static bool IsRecipientOrLetterheadLine(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length <= 4) return true;

        foreach (var prefix in LetterheadAndAddressNoisePrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (Regex.IsMatch(trimmed, @"^(?:To|From)\s*[,:.\-]?\s*$", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(trimmed, @"^(?:The\s+)?(?:Principal|Headmaster|ZEO|CEO|Director|Secretary|Manager|Officer|Executive|Engineer|Tehsildar|Patwari|BDO|DDO|HOD)\b.*[,.:]?$", RegexOptions.IgnoreCase)
            && trimmed.Length < 65)
            return true;

        if (Regex.IsMatch(trimmed, @"^(?:Distt|District|Tehsil|Zone|Block|Ganderbal|Srinagar|Jammu|Kashmir|Anantnag|Baramulla|Budgam|Pulwama|Kupwara|Bandipora|Shopian|Kulgam)\b.*$", RegexOptions.IgnoreCase)
            && trimmed.Length < 45)
            return true;

        return false;
    }

    private static string CleanSubjectFragment(string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return string.Empty;

        string cleaned = Regex.Replace(
            fragment,
            @"\s+Dated?\s*[:.\-–—]*\s*\d{1,2}\s*[/.\-]\s*\d{1,2}\s*[/.\-]\s*\d{2,4}\s*$",
            string.Empty,
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(
            cleaned,
            @"^(?:Subject|Subj|Sub|5ubject|Subjeet|Subjcct|Regarding|विषय)\s*[:.;=\-–—~|/]+\s*",
            string.Empty,
            RegexOptions.IgnoreCase);

        cleaned = cleaned.Trim().TrimStart(':', '-', '–', '—', '.', '~', ';', '=', '|', ' ');
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }
}
