using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace SeaReader.Native;

internal sealed record FeedLink(string Url, string? Label, string? Extension, string Action)
{
    public string Display => Label is not null && Label is not "下载" and not "打开目录" and not "打开网页" ? Label :
        Action == "catalog" ? "打开目录" : Action == "web" ? "打开网页" :
        Extension is null ? "下载" : Extension.ToUpperInvariant() + " 下载";
}

internal sealed record FeedEntry(string Title, IReadOnlyList<FeedLink> Links);
internal sealed record FeedCatalog(string Title, IReadOnlyList<FeedEntry> Entries, string? Next);
internal sealed record SavedSubscription(string Url, string Title, string Content);

internal sealed class SubscriptionCatalog(LibraryStore store)
{
    private const int FeedLimit = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        IgnoreReadOnlyProperties = true
    };
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    });

    static SubscriptionCatalog() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal Task<IReadOnlyList<SavedSubscription>> ListAsync() => Run<IReadOnlyList<SavedSubscription>>(database =>
        database.Read("SELECT url, title, content FROM feeds ORDER BY updated_at DESC, url",
            row => new SavedSubscription(row.Text(0), row.Text(1), row.Text(2))));

    internal async Task<SavedSubscription> SubscribeAsync(string address, CancellationToken ct)
    {
        var url = ValidateUrl(address);
        var catalog = await LoadAsync(url.AbsoluteUri, ct).ConfigureAwait(false);
        string content = JsonSerializer.Serialize(catalog, Json);
        if (content.Length > 8 * 1024 * 1024) throw new InvalidDataException("书源响应过大。");
        var feed = new SavedSubscription(url.AbsoluteUri, catalog.Title, content);
        ct.ThrowIfCancellationRequested();
        await Run(database =>
        {
            database.Execute("""
                INSERT INTO feeds(url,title,content,updated_at) VALUES (?, ?, ?, ?)
                ON CONFLICT(url) DO UPDATE SET title=excluded.title,content=excluded.content,updated_at=excluded.updated_at
                """, feed.Url, feed.Title, feed.Content, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return true;
        }).ConfigureAwait(false);
        return feed;
    }

    internal Task RemoveAsync(string address) => Run(database =>
    {
        database.Execute("DELETE FROM feeds WHERE url=?", address);
        return true;
    });

    internal FeedCatalog ReadSaved(SavedSubscription feed) =>
        JsonSerializer.Deserialize<FeedCatalog>(feed.Content, Json) ?? throw new InvalidDataException("无法读取已保存的书源。");

    private Task<T> Run<T>(Func<LibraryDatabase, T> operation) => Task.Run(() =>
    {
        using var database = new LibraryDatabase(Path.Combine(store.DataDirectory, "library.sqlite"));
        database.ExecuteScript("""
            CREATE TABLE IF NOT EXISTS feeds (
                url TEXT PRIMARY KEY, title TEXT NOT NULL, content TEXT NOT NULL, updated_at INTEGER NOT NULL);
            """);
        return operation(database);
    });

    internal static Uri ValidateUrl(string address, bool allowFtp = false)
    {
        if (address.Length > 4096 || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps &&
             (!allowFtp || uri.Scheme != Uri.UriSchemeFtp)))
            throw new InvalidDataException(allowFtp ? "请输入 HTTP、HTTPS 或 FTP 文件地址。" : "请输入 HTTP 或 HTTPS 地址。");
        return new UriBuilder(uri) { Fragment = "" }.Uri;
    }

    internal static async Task<FeedCatalog> LoadAsync(string address, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var current = ValidateUrl(address);
        try
        {
            for (int hops = 0; hops <= 5; hops++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Accept.ParseAdd("application/atom+xml,application/rss+xml,application/xml,text/xml,*/*;q=0.5");
                request.Headers.UserAgent.ParseAdd("SeaReader");
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not { } location)
                        throw new InvalidDataException("书源重定向缺少地址。");
                    current = ValidateUrl(new Uri(current, location).AbsoluteUri);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > FeedLimit) throw new InvalidDataException("书源响应过大。");
                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var payload = new MemoryStream();
                var buffer = new byte[32768];
                int size;
                while ((size = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    if (payload.Length + size > FeedLimit) throw new InvalidDataException("书源响应过大。");
                    payload.Write(buffer, 0, size);
                }
                payload.Position = 0;
                token.ThrowIfCancellationRequested();
                return Parse(payload, current, response.Content.Headers.ContentType?.CharSet);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("书源连接超时。");
        }
        throw new InvalidDataException("书源重定向次数过多。");
    }

    internal static FeedCatalog Parse(Stream xml, Uri address, string? charset = null)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = FeedLimit
        };
        using var decoded = string.IsNullOrWhiteSpace(charset) ? null :
            new StreamReader(xml, Encoding.GetEncoding(charset.Trim(' ', '"', '\'')), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var reader = decoded is null ? XmlReader.Create(xml, settings) : XmlReader.Create(decoded, settings);
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("书源没有内容。");

        if (root.Name.LocalName == "rss")
        {
            var channel = Children(root, "channel").FirstOrDefault() ?? throw new InvalidDataException("RSS 书源缺少频道。");
            return new FeedCatalog(Title(channel, address.AbsoluteUri),
                Children(channel, "item").Select(item => new FeedEntry(Title(item, "未命名条目"),
                    Children(item, "enclosure").Select(link => Link(address, link, (string?)link.Attribute("url"), "download"))
                        .Concat(Children(item, "link").Select(link => Link(address, link, link.Value.Trim(), "web")))
                        .OfType<FeedLink>().ToArray())).ToArray(), null);
        }

        if (root.Name.LocalName != "feed") throw new InvalidDataException("请输入 RSS 或 OPDS 书源地址。");
        var next = Children(root, "link").FirstOrDefault(link => HasRelation(link, "next"));
        var entries = Children(root, "entry").Select(item => new FeedEntry(Title(item, "未命名条目"),
            Children(item, "link").Select(link => AtomLink(address, link)).OfType<FeedLink>().ToArray())).ToArray();
        return new FeedCatalog(Title(root, address.AbsoluteUri), entries,
            next is null ? null : Link(address, next, (string?)next.Attribute("href"), "catalog")?.Url);
    }

    private static IEnumerable<XElement> Children(XElement element, string name) =>
        element.Elements().Where(child => child.Name.LocalName == name);

    private static string Title(XElement element, string fallback)
    {
        string? title = Children(element, "title").FirstOrDefault()?.Value.Trim();
        return string.IsNullOrWhiteSpace(title) ? fallback : title[..Math.Min(title.Length, 4096)];
    }

    private static string[] Relations(XElement element) =>
        ((string?)element.Attribute("rel") ?? "alternate").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool HasRelation(XElement element, string relation) => Relations(element).Contains(relation, StringComparer.OrdinalIgnoreCase);

    private static FeedLink? AtomLink(Uri address, XElement element)
    {
        var type = (string?)element.Attribute("type") ?? "";
        var relations = Relations(element);
        string? action = relations.Any(relation => relation.StartsWith("http://opds-spec.org/acquisition", StringComparison.OrdinalIgnoreCase) ||
                                                  relation.StartsWith("https://opds-spec.org/acquisition", StringComparison.OrdinalIgnoreCase)) ? "download" :
            type.Contains("application/atom+xml", StringComparison.OrdinalIgnoreCase) ? "catalog" :
            relations.Contains("alternate", StringComparer.OrdinalIgnoreCase) ? "web" : null;
        return action is null ? null : Link(address, element, (string?)element.Attribute("href"), action);
    }

    private static FeedLink? Link(Uri address, XElement element, string? value, string action)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var ancestor in element.AncestorsAndSelf().Reverse())
        {
            var basis = (string?)ancestor.Attribute(XNamespace.Xml + "base");
            if (basis is null) continue;
            if (!Uri.TryCreate(address, basis, out var resolved)) return null;
            address = resolved;
        }
        if (!Uri.TryCreate(address, value, out var target)) return null;
        try { target = ValidateUrl(target.AbsoluteUri, action == "download"); }
        catch (InvalidDataException) { return null; }
        var nameFormat = FormatForName(Uri.UnescapeDataString(target.AbsolutePath));
        var extension = FormatForMime((string?)element.Attribute("type"));
        if (extension is null || (extension == "html" && nameFormat is not null)) extension = nameFormat;
        return new FeedLink(target.AbsoluteUri, (string?)element.Attribute("title"), extension, action);
    }

    internal static string? FormatForMime(string? mime) => mime?.Split(';', 2)[0].Trim().ToLowerInvariant() switch
    {
        "application/epub+zip" => "epub",
        "application/pdf" => "pdf",
        "application/fb2+xml" or "application/x-fictionbook+xml" or "text/fb2+xml" => "fb2",
        "application/xhtml+xml" or "text/html" => "html",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
        "text/plain" => "txt",
        _ => null
    };

    internal static string? FormatForName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".epub" => "epub",
        ".pdf" => "pdf",
        ".fb2" => "fb2",
        ".html" or ".htm" or ".xhtml" => "html",
        ".docx" => "docx",
        ".txt" or ".md" or ".markdown" => "txt",
        _ => null
    };
}
