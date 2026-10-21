#pragma warning disable CA1416, CA1422, CS0618, CS1570, CS8600, CS8602, CS8604, SYSLIB0014, IL2026, IL3050
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Webkit;

namespace AiSmartEmailer.Services;

/// <summary>
/// Custom ContentProvider registered as "com.aismartemailer.app.fileprovider".
/// Safely exposes shared PDF/Image files from the app's cache directory to the Gmail app
/// via content:// URIs with temporary read permissions (required on Android 7.0+ / API 24+).
/// </summary>
[ContentProvider(
    new[] { Authority },
    Name = "com.aismartemailer.app.AttachmentFileProvider",
    Exported = false,
    GrantUriPermissions = true)]
public class AttachmentFileProvider : ContentProvider
{
    public const string Authority = "com.aismartemailer.app.fileprovider";
    public const string SharedFolderName = "shared_attachments";

    public override bool OnCreate() => true;

    /// <summary>
    /// Asynchronously copies an incoming shared content:// URI into our app's private cache directory
    /// on a background thread (with 64 KB buffered I/O) so the UI thread never stutters on large PDFs.
    /// </summary>
    public static Task<(string LocalFilePath, Android.Net.Uri ShareableUri, string FileName, string MimeType)> StageIncomingUriAsync(
        Context context,
        Android.Net.Uri incomingUri,
        string? incomingMimeType,
        CancellationToken ct = default)
    {
        return Task.Run(() => StageIncomingUri(context, incomingUri, incomingMimeType, ct), ct);
    }

    /// <summary>
    /// Copies an incoming shared content:// URI into our app's private cache directory
    /// and returns a local file path + shareable content:// URI for Gmail.
    /// </summary>
    public static (string LocalFilePath, Android.Net.Uri ShareableUri, string FileName, string MimeType) StageIncomingUri(
        Context context,
        Android.Net.Uri incomingUri,
        string? incomingMimeType,
        CancellationToken ct = default)
    {
        string fileName = QueryDisplayName(context, incomingUri);
        string mimeType = ResolveMimeType(context, incomingUri, incomingMimeType, fileName);

        // Ensure file has a valid extension matching its MIME type
        if (!Path.HasExtension(fileName))
        {
            string ext = mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase) ? ".pdf"
                       : mimeType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png"
                       : mimeType.Contains("webp", StringComparison.OrdinalIgnoreCase) ? ".webp"
                       : ".jpg";
            fileName += ext;
        }

        string sharedDir = Path.Combine(context.CacheDir!.AbsolutePath, SharedFolderName);
        Directory.CreateDirectory(sharedDir);

        string safeFileName = SanitizeFileName(fileName);
        string localPath = Path.Combine(sharedDir, safeFileName);

        using (var inputStream = context.ContentResolver!.OpenInputStream(incomingUri)
               ?? throw new InvalidOperationException("Unable to open shared file stream."))
        using (var outputStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 65536))
        {
            byte[] buffer = new byte[65536];
            int bytesRead;
            while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                outputStream.Write(buffer, 0, bytesRead);
            }
        }

        // Fire-and-forget cleanup of old staged files older than 24 hours so we don't block the current share
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var oldFile in Directory.GetFiles(sharedDir))
                {
                    if (!string.Equals(oldFile, localPath, StringComparison.OrdinalIgnoreCase)
                        && File.GetLastWriteTimeUtc(oldFile) < DateTime.UtcNow.AddHours(-24))
                    {
                        File.Delete(oldFile);
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        });

        var shareableUri = new Android.Net.Uri.Builder()
            .Scheme("content")
            .Authority(Authority)
            .AppendPath(SharedFolderName)
            .AppendPath(safeFileName)
            .Build()!;

        return (localPath, shareableUri, safeFileName, mimeType);
    }

    private static string QueryDisplayName(Context context, Android.Net.Uri uri)
    {
        try
        {
            if (uri.Scheme == "content")
            {
                using ICursor? cursor = context.ContentResolver?.Query(uri, new[] { IOpenableColumns.DisplayName }, null, null, null);
                if (cursor != null && cursor.MoveToFirst())
                {
                    int nameIndex = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                    if (nameIndex >= 0)
                    {
                        string? name = cursor.GetString(nameIndex);
                        if (!string.IsNullOrWhiteSpace(name))
                            return name;
                    }
                }
            }
        }
        catch
        {
            // Fallback to path segment below
        }

        string? lastSegment = uri.LastPathSegment;
        if (!string.IsNullOrWhiteSpace(lastSegment))
        {
            return Path.GetFileName(lastSegment);
        }

        return $"Attachment_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    private static string ResolveMimeType(Context context, Android.Net.Uri uri, string? hintMimeType, string fileName)
    {
        string? resolverMime = null;
        try
        {
            resolverMime = context.ContentResolver?.GetType(uri);
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(resolverMime) && resolverMime != "*/*" && resolverMime != "application/octet-stream")
            return resolverMime;

        if (!string.IsNullOrWhiteSpace(hintMimeType) && hintMimeType != "*/*")
            return hintMimeType;

        string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (!string.IsNullOrEmpty(ext))
        {
            string? guessed = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext);
            if (!string.IsNullOrWhiteSpace(guessed))
                return guessed;
        }

        if (ext == "pdf") return "application/pdf";
        if (ext == "png") return "image/png";
        if (ext is "jpg" or "jpeg") return "image/jpeg";
        return "application/pdf";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(name) ? "Document.pdf" : name;
    }

    private FileInfo GetLocalFileForUri(Android.Net.Uri uri)
    {
        string fileName = uri.LastPathSegment ?? throw new FileNotFoundException("Missing filename in URI");
        string sharedDir = Path.Combine(Context!.CacheDir!.AbsolutePath, SharedFolderName);
        string fullPath = Path.GetFullPath(Path.Combine(sharedDir, fileName));

        // Prevent path traversal
        if (!fullPath.StartsWith(Path.GetFullPath(sharedDir), StringComparison.Ordinal))
        {
            throw new Java.Lang.SecurityException("Path traversal attempt blocked.");
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Shared attachment not found.", fullPath);
        }
        return info;
    }

    public override ICursor Query(
        Android.Net.Uri uri,
        string[]? projection,
        string? selection,
        string[]? selectionArgs,
        string? sortOrder)
    {
        var fileInfo = GetLocalFileForUri(uri);
        projection ??= new[] { IOpenableColumns.DisplayName, IOpenableColumns.Size };

        var cols = new string[projection.Length];
        var values = new Java.Lang.Object[projection.Length];
        int count = 0;

        foreach (string col in projection)
        {
            if (col == IOpenableColumns.DisplayName)
            {
                cols[count] = IOpenableColumns.DisplayName;
                values[count++] = new Java.Lang.String(fileInfo.Name);
            }
            else if (col == IOpenableColumns.Size)
            {
                cols[count] = IOpenableColumns.Size;
                values[count++] = Java.Lang.Long.ValueOf(fileInfo.Length);
            }
        }

        var finalCols = new string[count];
        var finalValues = new Java.Lang.Object[count];
        Array.Copy(cols, finalCols, count);
        Array.Copy(values, finalValues, count);

        var cursor = new MatrixCursor(finalCols, 1);
        cursor.AddRow(finalValues);
        return cursor;
    }

    public override string? GetType(Android.Net.Uri uri)
    {
        var fileInfo = GetLocalFileForUri(uri);
        string ext = fileInfo.Extension.TrimStart('.').ToLowerInvariant();
        if (ext == "pdf") return "application/pdf";
        string? mime = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext);
        return mime ?? "application/octet-stream";
    }

    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        var fileInfo = GetLocalFileForUri(uri);
        var jFile = new Java.IO.File(fileInfo.FullName);
        return ParcelFileDescriptor.Open(jFile, ParcelFileMode.ReadOnly);
    }

    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) =>
        throw new NotSupportedException("Insert is not supported.");

    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) =>
        throw new NotSupportedException("Delete is not supported.");

    public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) =>
        throw new NotSupportedException("Update is not supported.");
}
