using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SeaReader.Native;

public static class OcrEngine
{
    private const int MaxFileBytes = 25 * 1024 * 1024;
    private const int MaxImageEdge = 4096;
    private const string CjkCharacter = "[\u3001-\u30ff\u3400-\u9fff\uf900-\ufaff\uff01-\uff60]";
    private static readonly Regex CjkWordGap = new($"(?<={CjkCharacter}) (?={CjkCharacter})");
    private static readonly string[] Languages = ["eng", "chi_sim", "jpn"];

    public static Task<BitmapSource> LoadImageAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run<BitmapSource>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            if (stream.Length is <= 0 or > MaxFileBytes)
                throw new InvalidDataException("请选择小于 25 MB 的图片。");

            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            int width = frame.PixelWidth;
            int height = frame.PixelHeight;
            if (width == 0 || height == 0) throw new InvalidDataException("该图片没有可识别的像素。");
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            if (width >= height && width > MaxImageEdge) image.DecodePixelWidth = MaxImageEdge;
            if (height > width && height > MaxImageEdge) image.DecodePixelHeight = MaxImageEdge;
            image.EndInit();
            image.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            return image;
        }, cancellationToken);

    public static Task<string> RecognizeAsync(BitmapSource image, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (language != "auto" && !Languages.Contains(language)) throw new ArgumentOutOfRangeException(nameof(language));
        if (!image.IsFrozen)
        {
            image = image.Clone();
            image.Freeze();
        }
        return Task.Run(() => Recognize(image, language, cancellationToken), cancellationToken);
    }

    private static string Recognize(BitmapSource image, string language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int edge = Math.Max(image.PixelWidth, image.PixelHeight);
        if (edge > MaxImageEdge)
        {
            double scale = (double)MaxImageEdge / edge;
            image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        }

        byte[] pixels = Grayscale(image, cancellationToken);
        string[] codes = language == "auto" ? Languages : [language];
        var data = Directory.CreateTempSubdirectory("SeaReader.Ocr-");
        IntPtr engine = IntPtr.Zero;
        IntPtr monitor = IntPtr.Zero;
        NativeOcr.CancelCallback cancel = (_, _) => cancellationToken.IsCancellationRequested;
        try
        {
            foreach (string code in codes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string model = Path.Combine(AppContext.BaseDirectory, "ocr-models", code + ".traineddata.gz");
                if (!File.Exists(model)) throw new InvalidOperationException("缺少离线 OCR 语言包，请重新安装应用。");
                using var input = new GZipStream(File.OpenRead(model), CompressionMode.Decompress);
                using var output = File.Create(Path.Combine(data.FullName, code + ".traineddata"));
                input.CopyTo(output);
            }

            cancellationToken.ThrowIfCancellationRequested();
            engine = NativeOcr.TessBaseAPICreate();
            string modelList = language switch
            {
                "auto" => "eng+chi_sim+jpn+~chi_sim_vert+~jpn_vert",
                "chi_sim" or "jpn" => $"{language}+~{language}_vert",
                _ => language
            };
            if (engine == IntPtr.Zero || InitEngine(engine, data.FullName, modelList) != 0)
                throw new InvalidOperationException("无法加载离线 OCR 模型。");

            const int automaticPageSegmentation = 3;
            NativeOcr.TessBaseAPISetPageSegMode(engine, automaticPageSegmentation);
            NativeOcr.TessBaseAPISetImage(engine, pixels, image.PixelWidth, image.PixelHeight, 1, image.PixelWidth);
            NativeOcr.TessBaseAPISetSourceResolution(engine, (int)Math.Clamp(image.DpiY, 96, 600));
            monitor = NativeOcr.TessMonitorCreate();
            if (monitor == IntPtr.Zero) throw new InvalidOperationException("无法启动离线 OCR。");
            NativeOcr.TessMonitorSetCancelFunc(monitor, cancel);
            int result = NativeOcr.TessBaseAPIRecognize(engine, monitor);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != 0) throw new InvalidOperationException("无法识别该图片。");

            IntPtr text = NativeOcr.TessBaseAPIGetUTF8Text(engine);
            if (text == IntPtr.Zero) return "";
            try
            {
                string recognized = Marshal.PtrToStringUTF8(text)?.Trim() ?? "";
                return CjkWordGap.Replace(recognized, "");
            }
            finally { NativeOcr.TessDeleteText(text); }
        }
        catch (DllNotFoundException error)
        {
            throw new InvalidOperationException("无法加载 OCR 引擎，请重新安装应用。", error);
        }
        finally
        {
            if (monitor != IntPtr.Zero) NativeOcr.TessMonitorDelete(monitor);
            if (engine != IntPtr.Zero) NativeOcr.TessBaseAPIDelete(engine);
            GC.KeepAlive(cancel);
            foreach (string code in codes) File.Delete(Path.Combine(data.FullName, code + ".traineddata"));
            data.Delete();
        }
    }

    private static int InitEngine(IntPtr engine, string directory, string languages)
    {
        const int lstmOnly = 1;
        if (directory.All(char.IsAscii))
            return NativeOcr.TessBaseAPIInit2(engine, directory, languages, lstmOnly);

        // Tesseract opens traineddata using the CRT's narrow path API. UTF-8 also handles
        // characters outside the Windows system code page when the temporary directory is localized.
        const int enableThreadLocale = 1;
        const int lcCtype = 2;
        int previousMode = NativeOcr.ConfigureThreadLocale(enableThreadLocale);
        if (previousMode == -1) throw new InvalidOperationException("无法读取当前用户目录中的离线 OCR 模型。");
        string? previousLocale = Marshal.PtrToStringUni(NativeOcr.SetLocale(lcCtype, null));
        try
        {
            if (NativeOcr.SetLocale(lcCtype, ".UTF-8") == IntPtr.Zero)
                throw new InvalidOperationException("无法读取当前用户目录中的离线 OCR 模型。");
            return NativeOcr.TessBaseAPIInit2(engine, directory, languages, lstmOnly);
        }
        finally
        {
            if (previousLocale != null) NativeOcr.SetLocale(lcCtype, previousLocale);
            NativeOcr.ConfigureThreadLocale(previousMode);
        }
    }

    private static byte[] Grayscale(BitmapSource image, CancellationToken cancellationToken)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth;
        int stride = width * 4;
        var row = new byte[stride];
        var converted = new byte[width * source.PixelHeight];
        for (int y = 0; y < source.PixelHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.CopyPixels(new System.Windows.Int32Rect(0, y, width, 1), row, stride, 0);
            int destination = y * width;
            for (int x = 0; x < width; x++)
            {
                int sourceIndex = x * 4;
                int gray = (29 * row[sourceIndex] + 150 * row[sourceIndex + 1] + 77 * row[sourceIndex + 2]) >> 8;
                int alpha = row[sourceIndex + 3];
                converted[destination + x] = (byte)((gray * alpha + 255 * (255 - alpha)) / 255);
            }
        }
        return converted;
    }

    private static class NativeOcr
    {
        private const string Library = "tesseract55.dll";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal delegate bool CancelCallback(IntPtr context, int words);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr TessBaseAPICreate();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TessBaseAPIDelete(IntPtr engine);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int TessBaseAPIInit2(IntPtr engine, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string language, int mode);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TessBaseAPISetPageSegMode(IntPtr engine, int mode);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TessBaseAPISetImage(IntPtr engine, byte[] pixels, int width, int height, int bytesPerPixel, int stride);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TessBaseAPISetSourceResolution(IntPtr engine, int dpi);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int TessBaseAPIRecognize(IntPtr engine, IntPtr monitor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr TessBaseAPIGetUTF8Text(IntPtr engine);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TessDeleteText(IntPtr text);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr TessMonitorCreate();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TessMonitorDelete(IntPtr monitor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TessMonitorSetCancelFunc(IntPtr monitor, CancelCallback callback);

        [DllImport("ucrtbase.dll", EntryPoint = "_configthreadlocale", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ConfigureThreadLocale(int mode);
        [DllImport("ucrtbase.dll", EntryPoint = "_wsetlocale", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr SetLocale(int category, [MarshalAs(UnmanagedType.LPWStr)] string? name);
    }
}
