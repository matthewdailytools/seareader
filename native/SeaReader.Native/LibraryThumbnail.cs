using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SeaReader.Native;

internal static class LibraryThumbnail
{
    internal const int MaximumEpubBytes = 12 * 1024 * 1024;
    private const int MaximumJpegBytes = 512 * 1024;
    private const int MaximumWidth = 320;
    private const int MaximumHeight = 480;
    private const int MaximumSourceSide = 8192;
    private const long MaximumSourcePixels = 24_000_000;

    internal static Task<byte[]?> FromEpubAsync(byte[] cover, CancellationToken ct) => Task.Run(() =>
    {
        if (cover.Length == 0 || cover.Length > MaximumEpubBytes) return null;
        // Read dimensions before handing an embedded image to the Windows decoder.
        var input = PrepareRaster(cover, out var dimensions, ct);
        if (input == null) return null;

        try
        {
            ct.ThrowIfCancellationRequested();
            var bitmap = Decode(input, dimensions.Width, dimensions.Height);
            ct.ThrowIfCancellationRequested();
            return Encode(bitmap, ct);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }, ct);

    internal static BitmapSource? DecodeEpubIllustration(byte[] image, int maximumEdge, CancellationToken ct, out bool hasDetail)
    {
        ct.ThrowIfCancellationRequested();
        hasDetail = false;
        var input = PrepareRaster(image, out var dimensions, ct);
        if (input == null) return null;

        try
        {
            ct.ThrowIfCancellationRequested();
            var bitmap = Decode(input, dimensions.Width, dimensions.Height, maximumEdge, maximumEdge);
            ct.ThrowIfCancellationRequested();
            if (bitmap.PixelWidth <= 0 || bitmap.PixelWidth > maximumEdge ||
                bitmap.PixelHeight <= 0 || bitmap.PixelHeight > maximumEdge) return null;
            hasDetail = Math.Max(dimensions.Width, dimensions.Height) > maximumEdge;
            return bitmap;
        }
        catch (Exception ex) when (IsUnavailable(ex) || ex is OverflowException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static byte[]? PrepareRaster(byte[] bytes, out (int Width, int Height) dimensions, CancellationToken ct)
    {
        if ((ReadPngSize(bytes) ?? ReadJpegSize(bytes)) is { } source)
        {
            dimensions = source;
            return IsAllowedSourceSize(source.Width, source.Height) ? bytes : null;
        }
        return ReadGifFirstFrame(bytes, out dimensions, ct);
    }

    internal static Task<byte[]?> FromPdfAsync(string path, CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            var book = await PdfBook.OpenAsync(path, ct).ConfigureAwait(false);
            var page = await book.RenderPageAsync(0, MaximumWidth, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return Encode(page, ct);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }, ct);

    internal static Task<BitmapSource?> LoadAsync(string path, CancellationToken ct) => Task.Run<BitmapSource?>(() =>
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var length = stream.Length;
            if (length is < 4 or > MaximumJpegBytes) return null;
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            ct.ThrowIfCancellationRequested();
            if (bytes[^2] != 0xff || bytes[^1] != 0xd9 ||
                ReadJpegSize(bytes) is not (var width, var height) ||
                width is <= 0 or > MaximumWidth || height is <= 0 or > MaximumHeight)
                return null;

            var bitmap = Decode(bytes, width, height);
            ct.ThrowIfCancellationRequested();
            return bitmap.PixelWidth == width && bitmap.PixelHeight == height ? bitmap : null;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }, ct);

    private static BitmapSource Decode(byte[] bytes, int width, int height, int maximumWidth = MaximumWidth,
        int maximumHeight = MaximumHeight)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (width > maximumWidth || height > maximumHeight)
        {
            if ((long)width * maximumHeight >= (long)height * maximumWidth)
                bitmap.DecodePixelWidth = maximumWidth;
            else
                bitmap.DecodePixelHeight = maximumHeight;
        }
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[]? Encode(BitmapSource source, CancellationToken ct)
    {
        var scale = FitScale(source.PixelWidth, source.PixelHeight);
        if (scale < 1)
        {
            int width = Math.Max(1, (int)Math.Floor(source.PixelWidth * scale));
            int height = Math.Max(1, (int)Math.Floor(source.PixelHeight * scale));
            source = new TransformedBitmap(source, new ScaleTransform(
                (double)width / source.PixelWidth, (double)height / source.PixelHeight));
        }
        if (source.PixelWidth is <= 0 or > MaximumWidth || source.PixelHeight is <= 0 or > MaximumHeight)
            return null;

        using var stream = new MemoryStream();
        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
        ct.ThrowIfCancellationRequested();
        return stream.Length <= MaximumJpegBytes ? stream.ToArray() : null;
    }

    private static double FitScale(int width, int height) =>
        Math.Min(1, Math.Min((double)MaximumWidth / width, (double)MaximumHeight / height));

    private static bool IsAllowedSourceSize(int width, int height) =>
        width is > 0 and <= MaximumSourceSide && height is > 0 and <= MaximumSourceSide &&
        (long)width * height <= MaximumSourcePixels;

    private static (int Width, int Height)? ReadPngSize(ReadOnlySpan<byte> image)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (image.Length < 33 || !image.StartsWith(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(image[8..]) != 13 || !image[12..16].SequenceEqual("IHDR"u8))
            return null;
        return (BinaryPrimitives.ReadInt32BigEndian(image[16..]), BinaryPrimitives.ReadInt32BigEndian(image[20..]));
    }

    private static (int Width, int Height)? ReadJpegSize(ReadOnlySpan<byte> image)
    {
        ReadOnlySpan<byte> signature = [0xff, 0xd8];
        if (!image.StartsWith(signature)) return null;

        int position = signature.Length;
        while (position < image.Length)
        {
            if (image[position++] != 0xff) return null;
            while (position < image.Length && image[position] == 0xff) position++;
            if (position == image.Length) return null;
            var marker = image[position++];
            if (marker == 0x01) continue;
            if (marker is 0 or >= 0xd0 and <= 0xda || image.Length - position < 2) return null;

            var length = BinaryPrimitives.ReadUInt16BigEndian(image[position..]);
            if (length < 2 || length > image.Length - position) return null;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (marker is not (0xc0 or 0xc1 or 0xc2) || length < 8) return null;
                return (BinaryPrimitives.ReadUInt16BigEndian(image[(position + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(image[(position + 3)..]));
            }
            position += length;
        }
        return null;
    }

    private static byte[]? ReadGifFirstFrame(byte[] image, out (int Width, int Height) size, CancellationToken ct)
    {
        size = default;
        ReadOnlySpan<byte> bytes = image;
        if (bytes.Length < 13 || (!bytes.StartsWith("GIF87a"u8) && !bytes.StartsWith("GIF89a"u8))) return null;
        int canvasWidth = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        int canvasHeight = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        if (!IsAllowedSourceSize(canvasWidth, canvasHeight)) return null;

        int position = 13 + GifColorTableSize(bytes[10]);
        while (position < bytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            byte block = bytes[position++];
            if (block == 0x21)
            {
                if (position == bytes.Length) return null;
                position++; // Extension label.
                if (!SkipGifSubBlocks(bytes, ref position)) return null;
                continue;
            }
            if (block != 0x2c || bytes.Length - position < 9) return null;

            int left = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
            int top = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(position + 2)..]);
            int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(position + 4)..]);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(position + 6)..]);
            if (width == 0 || height == 0 || left + width > canvasWidth || top + height > canvasHeight) return null;
            position += 9 + GifColorTableSize(bytes[position + 8]);
            if (position >= bytes.Length - 1 || bytes[position++] is < 2 or > 8 || bytes[position] == 0 ||
                !SkipGifSubBlocks(bytes, ref position)) return null;

            size = (width, height);
            if (position == bytes.Length - 1 && bytes[position] == 0x3b) return image;
            // Close the stream after frame zero to keep later animation data away from the decoder.
            var firstFrame = new byte[position + 1];
            bytes[..position].CopyTo(firstFrame);
            firstFrame[position] = 0x3b;
            return firstFrame;
        }
        return null;
    }

    private static int GifColorTableSize(byte flags) => (flags & 0x80) == 0 ? 0 : 3 << ((flags & 7) + 1);

    private static bool SkipGifSubBlocks(ReadOnlySpan<byte> image, ref int position)
    {
        while (position < image.Length)
        {
            int length = image[position++];
            if (length == 0) return true;
            position += length;
        }
        return false;
    }

    private static bool IsUnavailable(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException or ExternalException
        or InvalidOperationException;
}
