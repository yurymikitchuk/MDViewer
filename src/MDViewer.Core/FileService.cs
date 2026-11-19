using System.Text;

namespace MDViewer.Services;

public static class FileService
{
    public static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdown"];

    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8Lenient = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static bool IsMarkdownPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return MarkdownExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizePath(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));

    /// <summary>
    /// Reads text with BOM detection, strict UTF-8, then Windows-1251 / system ANSI fallback.
    /// </summary>
    public static async Task<string> ReadUtf8Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var bytes = new byte[stream.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = await stream.ReadAsync(bytes.AsMemory(read, bytes.Length - read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (read != bytes.Length)
        {
            Array.Resize(ref bytes, read);
        }

        return DecodeBytes(bytes);
    }

    internal static string DecodeBytes(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Utf8Lenient.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return Utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Common for Cyrillic docs saved as ANSI in older Windows editors.
            try
            {
                return Encoding.GetEncoding(1251).GetString(bytes);
            }
            catch
            {
                return Encoding.Default.GetString(bytes);
            }
        }
    }
}
