using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;

namespace SeaReader.Native;

internal static class TextPaginationCache
{
    // Bump when the reader changes text decoding, row splitting, or page break layout.
    private const int FormatVersion = 1;
    private const int MagicSize = 8;
    private const int HashSize = 32;
    private const int KeyOffset = MagicSize + sizeof(int);
    private const int ContentOffset = KeyOffset + HashSize;
    private const int CountOffset = ContentOffset + HashSize;
    private const int HeaderSize = CountOffset + sizeof(int);
    private const int PageSize = sizeof(int) + sizeof(double) + sizeof(int);
    private const int MaximumPages = 1_000_000;
    private static ReadOnlySpan<byte> Magic => "SRPAGES\0"u8;
    private static readonly string WpfVersion = typeof(RichTextBox).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    internal static Task<TextPagination?> TryReadAsync(string cacheDirectory, string bookId,
        TextDocument document, TextPaginationLayout layout, CancellationToken token) =>
        Task.Run(() => Read(cacheDirectory, bookId, document, layout, token), token);

    internal static Task WriteAsync(string cacheDirectory, string bookId, TextDocument document,
        TextPaginationLayout layout, TextPagination pages, CancellationToken token) =>
        Task.Run(() => Write(cacheDirectory, bookId, document, layout, pages, token), token);

    private static TextPagination? Read(string cacheDirectory, string bookId, TextDocument document,
        TextPaginationLayout layout, CancellationToken token)
    {
        if (CachePath(cacheDirectory, bookId) is not { } path) return null;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length < HeaderSize + PageSize + HashSize || input.Length > HeaderSize + MaximumPages * PageSize + HashSize)
                return null;

            Span<byte> header = stackalloc byte[HeaderSize];
            input.ReadExactly(header);
            var count = BinaryPrimitives.ReadInt32LittleEndian(header[CountOffset..]);
            if (!header[..MagicSize].SequenceEqual(Magic) ||
                BinaryPrimitives.ReadInt32LittleEndian(header[MagicSize..]) != FormatVersion ||
                !header.Slice(KeyOffset, HashSize).SequenceEqual(LayoutKey(bookId, document, layout)) ||
                count is < 1 or > MaximumPages || input.Length != HeaderSize + count * PageSize + HashSize)
                return null;

            var payload = new byte[count * PageSize];
            input.ReadExactly(payload);
            Span<byte> digest = stackalloc byte[HashSize];
            input.ReadExactly(digest);
            token.ThrowIfCancellationRequested();
            if (!digest.SequenceEqual(Checksum(header, payload)) ||
                !header.Slice(ContentOffset, HashSize).SequenceEqual(ContentHash(document.Text, token))) return null;

            var starts = new TextPageStart[count];
            for (var index = 0; index < starts.Length; index++)
            {
                if ((index & 255) == 0) token.ThrowIfCancellationRequested();
                var bytes = payload.AsSpan(index * PageSize, PageSize);
                var start = new TextPageStart(BinaryPrimitives.ReadInt32LittleEndian(bytes),
                    BinaryPrimitives.ReadDoubleLittleEndian(bytes[sizeof(int)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[(sizeof(int) + sizeof(double))..]));
                if (!IsValidStart(document, start) || (index == 0 ? start != new TextPageStart(0, 0, 0) :
                    !Follows(starts[index - 1], start))) return null;
                starts[index] = start;
            }
            token.ThrowIfCancellationRequested();
            return TextPagination.FromTextStarts(starts);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsValidStart(TextDocument document, TextPageStart start)
    {
        if (start.Offset < 0 || start.SourceOffset < start.Offset || start.SourceOffset > document.Text.Length ||
            !double.IsFinite(start.Fraction) || start.Fraction is < 0 or > 1) return false;
        var row = document.Lines[document.FindLine(start.Offset)];
        return row.Offset == start.Offset && start.SourceOffset <= row.Offset + row.Length;
    }

    private static bool Follows(TextPageStart previous, TextPageStart next) => next != previous &&
        next.SourceOffset >= previous.SourceOffset && (next.Offset > previous.Offset ||
            next.Offset == previous.Offset && next.Fraction >= previous.Fraction);

    private static void Write(string cacheDirectory, string bookId, TextDocument document,
        TextPaginationLayout layout, TextPagination pages, CancellationToken token)
    {
        if (pages.Count is < 1 or > MaximumPages || CachePath(cacheDirectory, bookId) is not { } path) return;
        var payload = new byte[pages.Count * PageSize];
        for (var index = 0; index < pages.Count; index++)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            var start = pages.GetPage(index, out var chapter);
            if (chapter != 0 || start.Offset < 0) return;
            var bytes = payload.AsSpan(index * PageSize, PageSize);
            BinaryPrimitives.WriteInt32LittleEndian(bytes, start.Offset);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes[sizeof(int)..], start.Fraction);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(sizeof(int) + sizeof(double))..], start.SourceOffset);
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[MagicSize..], FormatVersion);
        LayoutKey(bookId, document, layout).CopyTo(header[KeyOffset..]);
        ContentHash(document.Text, token).CopyTo(header[ContentOffset..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[CountOffset..], pages.Count);
        var digest = Checksum(header, payload);

        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(cacheDirectory);
            var candidate = Path.Combine(cacheDirectory, $"{Guid.NewGuid():N}.tmp");
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                temporary = candidate;
                output.Write(header);
                output.Write(payload);
                output.Write(digest);
            }
            token.ThrowIfCancellationRequested();
            File.Move(candidate, path, overwrite: true);
            temporary = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static string? CachePath(string cacheDirectory, string bookId) =>
        bookId.Length == 64 && bookId.All(Uri.IsHexDigit)
            ? Path.Combine(cacheDirectory, bookId.ToLowerInvariant() + ".pages") : null;

    private static byte[] LayoutKey(string bookId, TextDocument document, TextPaginationLayout layout)
    {
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FormatVersion);
            writer.Write(WpfVersion);
            writer.Write(Environment.Version.ToString());
            writer.Write(Environment.OSVersion.VersionString);
            writer.Write(bookId.ToLowerInvariant());
            writer.Write(document.EncodingName);
            writer.Write(document.Text.Length);
            writer.Write(document.Lines.Count);
            writer.Write(layout.Width);
            writer.Write(layout.Height);
            writer.Write(layout.FontFamily);
            writer.Write(layout.Size);
            writer.Write(layout.LineHeight);
            writer.Write(layout.DpiX);
            writer.Write(layout.DpiY);
            writer.Write((int)layout.FormattingMode);
            writer.Write(layout.Bold);
            writer.Write((int)layout.Alignment);
            writer.Write(layout.Hyphenation);
        }
        return SHA256.HashData(data.GetBuffer().AsSpan(0, (int)data.Length));
    }

    private static byte[] ContentHash(string text, CancellationToken token)
    {
        const int chunkLength = 256 * 1024;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var index = 0; index < text.Length;)
        {
            token.ThrowIfCancellationRequested();
            var length = Math.Min(chunkLength, text.Length - index);
            hash.AppendData(MemoryMarshal.AsBytes(text.AsSpan(index, length)));
            index += length;
        }
        token.ThrowIfCancellationRequested();
        return hash.GetHashAndReset();
    }

    private static byte[] Checksum(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(payload);
        return hash.GetHashAndReset();
    }
}
