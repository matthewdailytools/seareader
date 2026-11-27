using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeaReader.Native;

internal static class LibraryReadingDataImport
{
    private const int MaximumFileLength = 64 * 1024 * 1024;
    private const int MaximumRecordsPerSection = 250_000;
    private const int MaximumMetadataLength = 64 * 1024;
    private const int MaximumPositionLength = 256 * 1024;
    private const int MaximumTextLength = 4 * 1024 * 1024;
    private const int MaximumPreferenceLength = 16 * 1024;
    private const string NativeEpubPrefix = "native-epub:";

    private static readonly JsonSerializerOptions ImportJson = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        MaxDepth = 12,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonDocumentOptions EmbeddedJson = new() { AllowDuplicateProperties = false, MaxDepth = 8 };

    internal static Task<LibraryReadingData> ReadJsonAsync(string path, CancellationToken token) => Task.Run(async () =>
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is 0 or > MaximumFileLength)
            throw new InvalidDataException("阅读数据文件为空或超过 64 MiB。");

        try
        {
            var data = await JsonSerializer.DeserializeAsync<LibraryReadingData>(input, ImportJson, token).ConfigureAwait(false);
            if (data == null) throw new InvalidDataException("阅读数据文件缺少有效内容。");
            if (data.Schema != "seareader.windows.reading-data" || data.Version != 1)
                throw new InvalidDataException("请选择由 SeaReader Windows 版导出的第 1 版阅读数据。");
            return Validate(data, token);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("阅读数据的 JSON 结构无效。", error);
        }
    }, token);

    internal static bool IsSupportedPreference(string key) => key is
        "reading-appearance" or "reading-page-layout" or "reading-goals" or "library-display-mode" or
        "library-sort-order" or "txt-reading-mode" or "epub-reading-mode" or "pdf-reading-mode" or
        "fb2-reading-mode" or "html-reading-mode" or "docx-reading-mode";

    private static LibraryReadingData Validate(LibraryReadingData data, CancellationToken token)
    {
        var bookIds = ValidateRows(data.Books, book => book.Id, book =>
        {
            Require(IsBookId(book.Id) && IsBookFormat(book.Format) &&
                book.ReadingState is "none" or "wantToRead" or "finished" && ValidTimestamp(book.ImportedAt) &&
                (book.OpenedAt is not { } opened || ValidTimestamp(opened)), "书目");
            CheckText(book.OriginalTitle, MaximumMetadataLength, "原始书名", allowEmpty: true);
            CheckText(book.Title, MaximumMetadataLength, "书名", allowEmpty: true);
            CheckText(book.Author, MaximumMetadataLength, "作者", allowEmpty: true);
        }, "书目", token);
        var bookFormats = data.Books.ToDictionary(book => book.Id, book => book.Format, StringComparer.Ordinal);

        ValidateRows(data.ReadingPositions, position => position.BookId, position =>
        {
            Require(bookFormats.TryGetValue(position.BookId, out var format), "阅读位置的书籍引用");
            CheckPosition(position.Position, checkEncoding: format == "txt");
        }, "阅读位置", token);
        ValidateRows(data.NativeEpubPositions, position => position.BookId, position =>
        {
            Require(bookFormats.GetValueOrDefault(position.BookId) == "epub", "EPUB 阅读位置的书籍引用");
            CheckPosition(position.Position);
        }, "EPUB 阅读位置", token);

        var bookmarkIds = new HashSet<long>();
        ValidateRows(data.Bookmarks, bookmark => (bookmark.BookId, bookmark.Position), bookmark =>
        {
            Require(bookmark.Id > 0 && bookmarkIds.Add(bookmark.Id) && bookIds.Contains(bookmark.BookId) &&
                ValidTimestamp(bookmark.CreatedAt), "书签");
            CheckPosition(bookmark.Position);
        }, "书签", token);
        var annotationIds = new HashSet<long>();
        ValidateRows(data.Annotations, annotation => (annotation.BookId, annotation.Position), annotation =>
        {
            Require(annotation.Id > 0 && annotationIds.Add(annotation.Id) && bookIds.Contains(annotation.BookId) &&
                ValidTimestamp(annotation.UpdatedAt) && annotation.Highlight is "none" or "amber" or "mint" or "lavender" or "underline", "标注");
            CheckPosition(annotation.Position);
            CheckText(annotation.Quotation, MaximumTextLength, "摘录", allowEmpty: true);
            CheckText(annotation.Note, MaximumTextLength, "笔记", allowEmpty: true);
        }, "标注", token);
        ValidateRows(data.RecognizedPages, page => (page.BookId, page.Page), page =>
        {
            Require(bookIds.Contains(page.BookId) && page.Page is >= 1 and <= int.MaxValue, "OCR 页码");
            CheckText(page.Text, MaximumTextLength, "OCR 文字", allowEmpty: true);
        }, "OCR 页面", token);

        ValidateCollections(data, bookIds, token);
        ValidateRows(data.ReadingGoals.Days, day => day.DayNumber, day =>
            Require(ValidDay(day.DayNumber) && day.Milliseconds >= 0 && day.Milliseconds <= TimeSpan.MaxValue.TotalMilliseconds &&
                day.TargetMinutes is >= 1 and <= 1440, "阅读时长"), "阅读日期", token);
        ValidateRows(data.ReadingGoals.Finishes, finish => finish.BookId, finish =>
            Require(ValidDay(finish.DayNumber) && bookIds.Contains(finish.BookId), "读完记录"), "读完记录", token);

        var preferences = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in data.Preferences)
        {
            token.ThrowIfCancellationRequested();
            Require(IsSupportedPreference(key), "阅读偏好项");
            CheckText(value, MaximumPreferenceLength, "阅读偏好", allowEmpty: true);
            // Earlier versions exported the raw value, including settings the reader would ignore.
            if (ValidPreferenceValue(key, value))
                preferences.Add(key, key == "library-display-mode" && value == "list" ? "rows" : value);
        }
        token.ThrowIfCancellationRequested();
        return data with { Preferences = preferences };
    }

    private static void ValidateCollections(LibraryReadingData data, HashSet<string> bookIds, CancellationToken token)
    {
        var collectionNames = new HashSet<string>(StringComparer.Ordinal);
        var collections = ValidateRows(data.Collections, collection => collection.Id, collection =>
        {
            CheckText(collection.Name, LibraryStore.MaxCollectionNameLength, "收藏名称");
            var key = collection.Name.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
            Require(collection.Id > 0 && key.Length > 0 && !collection.Name.Any(char.IsControl) &&
                collectionNames.Add(key), "收藏名称或编号");
        }, "收藏", token);
        var memberships = ValidateRows(data.CollectionMemberships, item => (item.CollectionId, item.BookId), item =>
            Require(collections.Contains(item.CollectionId) && bookIds.Contains(item.BookId), "收藏成员引用"), "收藏成员", token);

        var ranks = new HashSet<(string Shelf, long Rank)>();
        ValidateRows(data.ManualOrder, item => (item.Shelf, item.BookId), item =>
        {
            Require(bookIds.Contains(item.BookId) && item.Rank is >= 0 and < int.MaxValue &&
                ranks.Add((item.Shelf, item.Rank)), "书架顺序");
            if (item.CollectionId is { } collectionId)
                Require(item.Shelf == "collection:" + collectionId.ToString(CultureInfo.InvariantCulture) &&
                    memberships.Contains((collectionId, item.BookId)), "书架收藏引用");
            else Require(item.Shelf is "all" or "want-to-read" or "finished" || IsBookFormat(item.Shelf), "书架名称");
        }, "书架顺序", token);
    }

    private static HashSet<TKey> ValidateRows<TRow, TKey>(IReadOnlyList<TRow> rows, Func<TRow, TKey> key,
        Action<TRow> validate, string label, CancellationToken token) where TRow : class where TKey : notnull
    {
        Require(rows.Count <= MaximumRecordsPerSection, label + "数量");
        var known = new HashSet<TKey>();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row == null) throw new InvalidDataException($"阅读数据包含空白{label}。");
            validate(row);
            if (!known.Add(key(row))) throw new InvalidDataException($"阅读数据包含重复的{label}。");
        }
        return known;
    }

    private static void CheckPosition(string position, bool checkEncoding = false)
    {
        CheckText(position, MaximumPositionLength, "阅读位置");
        var payload = position.StartsWith(NativeEpubPrefix, StringComparison.Ordinal)
            ? position.AsSpan(NativeEpubPrefix.Length) : position.AsSpan();
        var trimmed = payload.TrimStart();
        Require(!trimmed.IsEmpty && trimmed[0] is not ('/' or '\\') &&
            !(trimmed.Length >= 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':') &&
            !trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase), "阅读位置");

        // Legacy anchors may be opaque. Native TXT anchors can select a decoder on reopen.
        if (!checkEncoding || payload.IsEmpty || payload[0] != '{') return;
        using var json = JsonDocument.Parse(payload.ToString(), EmbeddedJson);
        foreach (var field in json.RootElement.EnumerateObject())
        {
            if (field.Name.Equals("encoding", StringComparison.OrdinalIgnoreCase) && field.Value.ValueKind != JsonValueKind.Null)
                Require(field.Value.ValueKind == JsonValueKind.String && ValidEncoding(field.Value.GetString()), "文本编码");
        }
    }

    private static bool ValidEncoding(string? name)
    {
        if (name is not { Length: > 0 and <= 128 } || name.AsSpan().Contains('\0')) return false;
        if (name.Equals("utf-16le", StringComparison.OrdinalIgnoreCase) || name.Equals("utf-16be", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(name) != null || Encoding.GetEncoding(name) != null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    private static bool ValidPreferenceValue(string key, string value) => key switch
    {
        "reading-appearance" => ValidPreferenceObject(value, ValidAppearanceField),
        "reading-goals" => ValidPreferenceObject(value, ValidGoalPreferenceField),
        "reading-page-layout" => value is "auto" or "single" or "double",
        "library-display-mode" => value is "grid" or "rows" or "list",
        "library-sort-order" => value is "recent" or "title" or "author" or "manual",
        _ => value is "page" or "scroll"
    };

    private static bool ValidPreferenceObject(string value, Func<JsonProperty, bool> validate)
    {
        try
        {
            using var json = JsonDocument.Parse(value, EmbeddedJson);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.EnumerateObject().All(validate);
        }
        catch (JsonException) { return false; }
    }

    private static bool ValidAppearanceField(JsonProperty field) => field.Name switch
    {
        "theme" => TextOf(field.Value) is "system" or "light" or "sepia" or "dark",
        "size" => NumberInRange(field.Value, 12, 44),
        "animatePageTurns" or "hyphenation" => IsBoolean(field.Value),
        "lineGuide" => TextOf(field.Value) is "off" or "soft" or "strong",
        "layout" => TextOf(field.Value) is "auto" or "single" or "double",
        "pageTurnStyle" => field.Value.ValueKind == JsonValueKind.Null || TextOf(field.Value) is "paper" or "fade" or "none",
        "customTypography" => field.Value.ValueKind == JsonValueKind.Null ||
            field.Value.ValueKind == JsonValueKind.Object && field.Value.EnumerateObject().All(ValidTypographyField),
        _ => ValidTypographyField(field)
    };

    private static bool ValidTypographyField(JsonProperty field) => field.Name switch
    {
        "font" => TextOf(field.Value) is "book" or "serif" or "system-ui" or "Georgia" or "sans-serif",
        "lineHeight" => NumberInRange(field.Value, 1.4 - .000001, 2.2 + .000001),
        "margin" => NumberInRange(field.Value, 12, 52),
        "bold" => IsBoolean(field.Value),
        "alignment" => TextOf(field.Value) is "left" or "justify",
        _ => false
    };

    private static bool ValidGoalPreferenceField(JsonProperty field) => field.Name switch
    {
        "enabled" or "includePdf" => IsBoolean(field.Value),
        "dailyMinutes" => IntegerInRange(field.Value, 1, 1440),
        "annualBooks" => IntegerInRange(field.Value, 1, 9999),
        _ => false
    };

    private static string? TextOf(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool NumberInRange(JsonElement value, double minimum, double maximum) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var scalar) && scalar >= minimum && scalar <= maximum;

    private static bool IntegerInRange(JsonElement value, int minimum, int maximum) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var scalar) && scalar >= minimum && scalar <= maximum;

    private static bool IsBoolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool IsBookId(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsBookFormat(string value) => value is "txt" or "pdf" || BookFormats.IsChapterBook(value);

    private static bool ValidTimestamp(long value) => value is >= -62_135_596_800_000 and <= 253_402_300_799_999;

    private static bool ValidDay(long day) => day >= 0 && day <= DateOnly.MaxValue.DayNumber;

    private static void CheckText(string? value, int maximum, string field, bool allowEmpty = false) =>
        Require(value != null && value.Length <= maximum && (allowEmpty || value.Length != 0) && !value.Contains('\0'), field);

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidDataException($"阅读数据包含无效的{detail}。");
    }
}
