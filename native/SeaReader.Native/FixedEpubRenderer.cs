using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using UglyToad.PdfPig.Content;

namespace SeaReader.Native;

internal sealed record FixedEpubCapture(BitmapSource Bitmap, PdfTextPage? Text, FixedEpubNavigation? Navigation = null);
internal sealed record FixedEpubNavigation(IReadOnlyList<FixedEpubLink> Links, IReadOnlyDictionary<string, Rect> Anchors);
internal sealed record FixedEpubLink(string Href, string Title, IReadOnlyList<Rect> Bounds, bool IsFootnote);

internal sealed class FixedEpubRenderer(string profileDirectory) : IDisposable
{
    private const int MaximumOutputDimension = 4096;
    private const int MaximumCaptureDimension = 8192;
    private const double MinimumRasterScale = .5;
    private const string OfflineBrowserArguments = "--proxy-server=http://seareader-offline-proxy.invalid:9 " +
        "--proxy-bypass-list=\"<-loopback>\" --host-resolver-rules=\"MAP * ~NOTFOUND\" --disable-background-networking";
    private const string ContentPolicy = "default-src 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        "font-src 'self' data:; script-src 'none'; object-src 'none'; frame-src 'none'; " +
        "form-action 'none'; base-uri 'none'; connect-src 'none'";
    private static readonly string VisibleTextRequest = GeometryRequest("true, false");
    private static readonly string VisiblePageRequest = GeometryRequest("true, true");
    private static readonly string VisibleNavigationRequest = GeometryRequest("false, true");
    private static readonly string PreparePageRequest = JsonSerializer.Serialize(new
    {
        expression = """
            (() => {
                const epub = 'http://www.idpf.org/2007/ops';
                for (const aside of document.getElementsByTagNameNS('http://www.w3.org/1999/xhtml', 'aside')) {
                    const types = aside.getAttributeNS(epub, 'type') ?? aside.getAttribute('epub:type') ?? '';
                    if (types.split(/\s+/u).includes('footnote')) aside.style.setProperty('display', 'none', 'important');
                }
                return document.fonts.ready.then(() => true);
            })()
            """,
        awaitPromise = true,
        returnByValue = true
    });
    private const string GeometryExpression = """
            ((readText, readNavigation) => {
                const width = innerWidth, height = innerHeight;
                const root = document.body || document.documentElement;
                const glyphs = [], links = [], anchors = [];
                let text = '';
                if (!root || width <= 0 || height <= 0) return { text, glyphs, links, anchors };

                const svgNamespace = 'http://www.w3.org/2000/svg';
                const ignored = new Set(['head', 'script', 'style', 'noscript', 'template', 'title', 'desc',
                    'metadata', 'defs', 'symbol', 'clippath', 'mask', 'pattern', 'marker', 'iframe', 'object']);
                const page = { left: 0, top: 0, right: width, bottom: height };
                const layout = new WeakMap();
                const isEmpty = (box) => box.right <= box.left || box.bottom <= box.top;
                const intersect = (a, b, x = true, y = true) => ({
                    left: x ? Math.max(a.left, b.left) : a.left,
                    top: y ? Math.max(a.top, b.top) : a.top,
                    right: x ? Math.min(a.right, b.right) : a.right,
                    bottom: y ? Math.min(a.bottom, b.bottom) : a.bottom
                });

                function elementLayout(element) {
                    if (layout.has(element)) return layout.get(element);
                    const parent = element.parentElement ? elementLayout(element.parentElement) : null;
                    const name = element.localName.toLowerCase();
                    const style = getComputedStyle(element);
                    const svg = element.namespaceURI === svgNamespace;
                    const svgText = svg && (name === 'text' || parent?.svgText === true);
                    const hidden = parent?.hidden === true || ignored.has(name) || style.display === 'none' ||
                        Number(style.opacity) === 0 || style.contentVisibility === 'hidden';
                    let clip = parent?.clip || page;
                    const canClip = svg ? name === 'svg' : style.display !== 'inline' && style.display !== 'contents';
                    if (!hidden && canClip) {
                        const clipX = style.overflowX !== 'visible';
                        const clipY = style.overflowY !== 'visible';
                        if (clipX || clipY) clip = intersect(clip, element.getBoundingClientRect(), clipX, clipY);
                    }
                    const hasPathClip = parent?.hasPathClip === true || style.clipPath !== 'none' || style.clip !== 'auto';
                    const value = { hidden, clip, style, svg, svgText, hasPathClip };
                    layout.set(element, value);
                    return value;
                }

                function orientation(element, entry) {
                    const style = entry.style;
                    const sign = style.direction === 'rtl' ? -1 : 1;
                    let x = /^(vertical|sideways)/.test(style.writingMode) ? 0 : sign;
                    let y = x === 0 ? (style.writingMode === 'sideways-lr' ? -sign : sign) : 0;
                    const svgMatrix = entry.svg && typeof element.getScreenCTM === 'function' ? element.getScreenCTM() : null;
                    if (svgMatrix) {
                        [x, y] = [svgMatrix.a * x + svgMatrix.c * y, svgMatrix.b * x + svgMatrix.d * y];
                    } else {
                        for (let item = element; item; item = item.parentElement) {
                            const transform = elementLayout(item).style.transform;
                            if (transform === 'none') continue;
                            const matrix = new DOMMatrixReadOnly(transform);
                            [x, y] = [matrix.a * x + matrix.c * y, matrix.b * x + matrix.d * y];
                        }
                    }
                    return Math.abs(y) > Math.abs(x) ? (y >= 0 ? 1 : 3) : (x >= 0 ? 0 : 2);
                }

                const transparent = (color) => color === 'transparent' || /^rgba\([^)]*,\s*0(?:\.0+)?\s*\)$/.test(color);
                function painted(entry) {
                    const style = entry.style;
                    if (entry.hidden || style.visibility !== 'visible' || isEmpty(entry.clip)) return false;
                    if (entry.svg) {
                        return entry.svgText && ((style.fill !== 'none' && Number(style.fillOpacity) !== 0 && !transparent(style.fill)) ||
                            (style.stroke !== 'none' && Number(style.strokeOpacity) !== 0 && !transparent(style.stroke) &&
                             parseFloat(style.strokeWidth) > 0));
                    }
                    return !transparent(style.webkitTextFillColor || style.color) || style.textShadow !== 'none' ||
                        parseFloat(style.webkitTextStrokeWidth) > 0 || style.backgroundClip.includes('text');
                }

                let hitTests = 0;
                function clippedGlyph(box, element, entry) {
                    if (!entry.hasPathClip || entry.style.pointerEvents === 'none') return box;
                    while (elementLayout(element).style.display === 'contents' && element.parentElement)
                        element = element.parentElement;
                    return hitBounds(box, (x, y) => document.elementsFromPoint(x, y).some(target =>
                        target === element || element.contains(target) ||
                        entry.svg && target.localName === 'text' && target.contains(element)));
                }

                function hitBounds(box, contains) {
                    const w = box.right - box.left, h = box.bottom - box.top;
                    const part = [.005, .25, .5, .75, .995];
                    const xs = part.map(value => box.left + value * w);
                    const ys = part.map(value => box.top + value * h);
                    const hit = (x, y) => ++hitTests <= 80000 && contains(x, y);
                    if (hit(xs[2], ys[2]) && hit(xs[0], ys[0]) && hit(xs[4], ys[0]) &&
                        hit(xs[0], ys[4]) && hit(xs[4], ys[4])) return box;

                    const samples = ys.map(y => xs.map(x => hit(x, y)));
                    let left = box.right, top = box.bottom, right = box.left, bottom = box.top;
                    for (let row = 0; row < ys.length; row++) {
                        for (let column = 0; column < xs.length; column++) {
                            if (!samples[row][column]) continue;
                            if (column === 0) left = box.left;
                            else if (!samples[row][column - 1] && xs[column - 1] < left)
                                left = Math.min(left, edge(xs[column - 1], xs[column], x => hit(x, ys[row])));
                            if (column === xs.length - 1) right = box.right;
                            else if (!samples[row][column + 1] && xs[column + 1] > right)
                                right = Math.max(right, edge(xs[column + 1], xs[column], x => hit(x, ys[row])));
                            if (row === 0) top = box.top;
                            else if (!samples[row - 1][column] && ys[row - 1] < top)
                                top = Math.min(top, edge(ys[row - 1], ys[row], y => hit(xs[column], y)));
                            if (row === ys.length - 1) bottom = box.bottom;
                            else if (!samples[row + 1][column] && ys[row + 1] > bottom)
                                bottom = Math.max(bottom, edge(ys[row + 1], ys[row], y => hit(xs[column], y)));
                        }
                    }
                    return right > left && bottom > top ? { left, top, right, bottom } : null;

                    function edge(outside, inside, contains) {
                        for (let attempt = 0; attempt < 5; attempt++) {
                            const middle = (outside + inside) / 2;
                            if (contains(middle)) inside = middle;
                            else outside = middle;
                        }
                        return inside;
                    }
                }

                function normalizedBox(box) {
                    return [box.left / width, box.top / height, box.right / width, box.bottom / height];
                }

                function readLinkMap() {
                    const htmlNamespace = 'http://www.w3.org/1999/xhtml';
                    const xlinkNamespace = 'http://www.w3.org/1999/xlink';
                    const xmlNamespace = 'http://www.w3.org/XML/1998/namespace';
                    const svgGeometry = new Set(['rect', 'circle', 'ellipse', 'line', 'polyline', 'polygon',
                        'path', 'image', 'text', 'tspan', 'textpath', 'use', 'foreignobject']);
                    const seenIds = new Set();
                    const elementWalker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
                    const domAnchor = node => node.localName.toLowerCase() === 'a' &&
                        (node.namespaceURI === htmlNamespace || node.namespaceURI === svgNamespace);
                    const enclosure = (a, b) => ({ left: Math.min(a.left, b.left), top: Math.min(a.top, b.top),
                        right: Math.max(a.right, b.right), bottom: Math.max(a.bottom, b.bottom) });
                    let inspected = 0, inspectedDescendants = 0, collectedBoxes = 0;

                    function readAnchor(element, entry) {
                        const legacyName = domAnchor(element) ? element.getAttribute('name') : null;
                        const identifiers = [element.getAttribute('id'), element.getAttributeNS(xmlNamespace, 'id'),
                            legacyName && !document.getElementById(legacyName) ? legacyName : null];
                        const names = identifiers.filter(name => name && name.length <= 1024 && !seenIds.has(name));
                        if (!names.length || entry.hidden || entry.style.visibility !== 'visible' || isEmpty(entry.clip)) return;
                        let box = null;
                        for (const rect of element.getClientRects()) {
                            // Empty inline fragment markers still identify a real, visible location.
                            if (entry.svg && rect.width === 0 && rect.height === 0) continue;
                            const position = { left: rect.left, top: rect.top,
                                right: Math.max(rect.right, rect.left + 1), bottom: Math.max(rect.bottom, rect.top + 1) };
                            const visible = intersect(entry.clip, position);
                            if (isEmpty(visible)) continue;
                            const clipped = clippedGlyph(visible, element, entry);
                            if (clipped) box = box ? enclosure(box, clipped) : clipped;
                        }
                        if (!box && !entry.svg && (entry.style.display === 'contents' || domAnchor(element))) {
                            const nearby = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
                            nearby.currentNode = element;
                            const textRange = document.createRange();
                            for (let node = nearby.nextNode(), checked = 0; node && ++checked <= 24; node = nearby.nextNode()) {
                                if (!node.parentElement || !node.length || !/\S/u.test(node.data.slice(0, 256))) continue;
                                const next = elementLayout(node.parentElement);
                                if (!painted(next)) continue;
                                textRange.setStart(node, 0);
                                textRange.setEnd(node, Math.min(node.length, 128));
                                for (const rect of textRange.getClientRects()) {
                                    const visible = intersect(next.clip, rect);
                                    if (!isEmpty(visible)) box = clippedGlyph(visible, node.parentElement, next);
                                    if (box) break;
                                }
                                if (box) break;
                            }
                        }
                        if (!box) return;
                        for (const name of names) {
                            if (anchors.length >= 2048) break;
                            if (seenIds.has(name)) continue;
                            seenIds.add(name);
                            anchors.push([name, normalizedBox(box)]);
                        }
                    }

                    function readLink(anchor, entry) {
                        const href = anchor.getAttribute('href') ?? anchor.getAttributeNS(xlinkNamespace, 'href');
                        if (!href || href.length > 4096 || !href.trim() || entry.hidden || isEmpty(entry.clip)) return;
                        const bounds = [];
                        function linkAt(x, y) {
                            for (let node = document.elementFromPoint(x, y); node; node = node.parentElement) {
                                if (node === anchor) return true;
                                if (domAnchor(node)) return false;
                            }
                            return false;
                        }
                        function fragments(box) {
                            const active = hitBounds(box, linkAt);
                            if (!active) return [];
                            const divisions = 5, runs = [];
                            const x = index => index === divisions ? active.right : active.left + (active.right - active.left) * index / divisions;
                            const y = index => index === divisions ? active.bottom : active.top + (active.bottom - active.top) * index / divisions;
                            for (let row = 0; row < divisions; row++) {
                                let start = -1;
                                for (let column = 0; column <= divisions; column++) {
                                    const hit = column < divisions && ++hitTests <= 60000 && linkAt(x(column + .5), y(row + .5));
                                    if (hit && start < 0) start = column;
                                    if (hit || start < 0) continue;
                                    const previous = runs.find(run => run.start === start && run.end === column && run.bottom === row);
                                    if (previous) previous.bottom++;
                                    else runs.push({ start, end: column, top: row, bottom: row + 1 });
                                    start = -1;
                                }
                            }
                            return runs.map(run => ({ left: x(run.start), top: y(run.top), right: x(run.end), bottom: y(run.bottom) }));
                        }
                        function add(element, shape) {
                            if (shape.hidden || shape.style.visibility !== 'visible' || isEmpty(shape.clip) ||
                                shape.style.pointerEvents === 'none') return;
                            for (const rectangle of element.getClientRects()) {
                                if (bounds.length >= 48 || hitTests >= 60000) break;
                                const visible = intersect(shape.clip, rectangle);
                                if (isEmpty(visible) || bounds.some(box => containsBox(box, visible))) continue;
                                for (const active of fragments(visible)) {
                                    if (bounds.length >= 48) break;
                                    if (!bounds.some(box => containsBox(box, active))) bounds.push(active);
                                }
                            }
                        }
                        function containsBox(outer, inner) {
                            return outer.left <= inner.left && outer.top <= inner.top &&
                                outer.right >= inner.right && outer.bottom >= inner.bottom;
                        }

                        if (!entry.svg) add(anchor, entry);
                        const childWalker = document.createTreeWalker(anchor, NodeFilter.SHOW_ELEMENT);
                        let alt = '';
                        for (let child = childWalker.nextNode(); child && ++inspectedDescendants <= 10000 && bounds.length < 48;
                             child = childWalker.nextNode()) {
                            const shape = elementLayout(child);
                            if (shape.hidden || isEmpty(shape.clip)) continue;
                            const name = child.localName.toLowerCase();
                            if (!alt && (name === 'img' || name === 'image')) alt = child.getAttribute('alt') || '';
                            if (!shape.svg || svgGeometry.has(name)) add(child, shape);
                        }
                        if (!bounds.length) return;
                        function labelText(element, allowHidden = false) {
                            if (!element) return '';
                            const labelWalker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
                            let result = '';
                            for (let node = labelWalker.nextNode(), checked = 0; node && ++checked <= 64 && result.length < 256;
                                 node = labelWalker.nextNode()) {
                                if (allowHidden || node.parentElement && painted(elementLayout(node.parentElement)))
                                    result += node.data.slice(0, 256 - result.length);
                            }
                            return result;
                        }
                        const named = (anchor.getAttribute('aria-labelledby') || '').slice(0, 1024).trim().split(/\s+/u).slice(0, 8)
                            .map(id => labelText(document.getElementById(id), true)).filter(Boolean).join(' ');
                        const aria = anchor.getAttribute('aria-label');
                        let title = anchor.getAttribute('title');
                        if (!title && entry.svg) {
                            for (let child = anchor.firstElementChild, checked = 0; child && ++checked <= 64;
                                 child = child.nextElementSibling) {
                                if (child.localName.toLowerCase() !== 'title') continue;
                                title = labelText(child, true);
                                break;
                            }
                        }
                        const cleanLabel = value => (value || '').slice(0, 1024).replace(/[\u0000-\u001f\u007f]+/gu, ' ')
                            .replace(/\s+/gu, ' ').trim().slice(0, 240);
                        const label = cleanLabel(named) || cleanLabel(aria) || cleanLabel(title) || cleanLabel(labelText(anchor)) || cleanLabel(alt);
                        const output = bounds.slice(0, 2048 - collectedBoxes).map(normalizedBox);
                        if (output.length) {
                            const epubType = anchor.getAttributeNS('http://www.idpf.org/2007/ops', 'type') ??
                                anchor.getAttribute('epub:type');
                            const tokens = (value) => (value ?? '').toLowerCase().split(/\s+/u);
                            const footnote = tokens(epubType).includes('noteref') ||
                                tokens(anchor.getAttribute('role')).includes('doc-noteref') ||
                                tokens(anchor.getAttribute('rel')).includes('footnote') ||
                                tokens(anchor.getAttribute('class')).some(value =>
                                    value === 'noteref' || value === 'footnote-ref' || value === 'footnoteref');
                            links.push([href, label, output, footnote]);
                            collectedBoxes += output.length;
                        }
                    }

                    for (let element = root; element && ++inspected <= 18000; element = elementWalker.nextNode()) {
                        if (anchors.length >= 2048 && (links.length >= 256 || collectedBoxes >= 2048)) break;
                        const named = anchors.length < 2048 && (element.hasAttribute('id') || element.hasAttributeNS(xmlNamespace, 'id') ||
                            domAnchor(element) && element.hasAttribute('name'));
                        const linked = links.length < 256 && collectedBoxes < 2048 && hitTests < 60000 && domAnchor(element);
                        if (!named && !linked) continue;
                        const entry = elementLayout(element);
                        if (named) readAnchor(element, entry);
                        if (linked) readLink(element, entry);
                    }
                }

                if (readNavigation) readLinkMap();
                if (!readText) return { text, glyphs, links, anchors };
                hitTests = 0;

                function lineRelation(box, prior, direction) {
                    const vertical = (direction & 1) !== 0;
                    const firstStart = vertical ? prior.left : prior.top;
                    const firstEnd = vertical ? prior.right : prior.bottom;
                    const nextStart = vertical ? box.left : box.top;
                    const nextEnd = vertical ? box.right : box.bottom;
                    const thickness = Math.min(firstEnd - firstStart, nextEnd - nextStart);
                    const overlap = Math.min(firstEnd, nextEnd) - Math.max(firstStart, nextStart);
                    const separation = vertical ? Math.max(box.top - prior.bottom, prior.top - box.bottom, 0) :
                        Math.max(box.left - prior.right, prior.left - box.right, 0);
                    const same = (direction & 1) === (prior.direction & 1) && thickness > 0 && overlap >= thickness * .33;
                    return { same, thickness, separation };
                }

                const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
                const range = document.createRange();
                const clusters = new Intl.Segmenter(undefined, { granularity: 'grapheme' });
                let prior = null, priorNode = null, pendingSpace = false, line = 0;
                let inspectedNodes = 0, inspectedUnits = 0, inspectedClusters = 0;

                function trimDanglingJoiner() {
                    if (!prior?.cluster.endsWith('\u200D')) return;
                    const cluster = prior.cluster.replace(/\u200D+$/u, '');
                    const removed = prior.cluster.length - cluster.length;
                    glyphs[glyphs.length - 1][1] -= removed;
                    text = text.slice(0, -removed);
                    prior.cluster = cluster;
                }

                function result() {
                    trimDanglingJoiner();
                    return { text, glyphs, links, anchors };
                }

                for (let node = walker.nextNode(); node && ++inspectedNodes <= 18000; node = walker.nextNode()) {
                    const element = node.parentElement;
                    if (!element || node.length === 0) continue;
                    const entry = elementLayout(element);
                    if (!painted(entry)) continue;
                    const direction = orientation(element, entry);
                    const remaining = 65536 - inspectedUnits;
                    if (remaining <= 0) break;
                    const sample = node.data.slice(0, remaining);
                    inspectedUnits += sample.length;

                    for (const { segment, index } of clusters.segment(sample)) {
                        if (++inspectedClusters > 18000 || glyphs.length >= 8192 || text.length + segment.length + 1 > 65536)
                            return result();
                        const whitespace = /^\s+$/u.test(segment);
                        const joins = prior && !whitespace && !pendingSpace && prior.cluster.length < 128 &&
                            (priorNode !== node || prior.cluster.endsWith('\u200D')) &&
                            clusters.segment(prior.cluster + segment).containing(prior.cluster.length)?.index === 0;
                        if (!whitespace && !joins && /^[\p{Cc}\p{Cf}]+$/u.test(segment)) continue;
                        range.setStart(node, index);
                        range.setEnd(node, index + segment.length);
                        let box = null, joinHint = null;
                        for (const rectangle of range.getClientRects()) {
                            const visible = intersect(entry.clip, rectangle);
                            if (!isEmpty(visible) && (box = clippedGlyph(visible, element, entry))) break;
                            // Chromium can put a continuation of a shaped cluster at a zero-width caret.
                            if (joins && !entry.hasPathClip && (rectangle.width === 0 || rectangle.height === 0) &&
                                visible.left <= visible.right && visible.top <= visible.bottom)
                                joinHint = visible;
                        }
                        if (joins && (box || joinHint)) {
                            const relation = lineRelation(box || joinHint, prior, direction);
                            if (relation.same && relation.separation <= relation.thickness) {
                                if (box) {
                                    prior.left = Math.min(prior.left, box.left);
                                    prior.top = Math.min(prior.top, box.top);
                                    prior.right = Math.max(prior.right, box.right);
                                    prior.bottom = Math.max(prior.bottom, box.bottom);
                                }
                                const last = glyphs[glyphs.length - 1];
                                last[1] += segment.length;
                                last[4] = prior.left / width;
                                last[5] = prior.top / height;
                                last[6] = prior.right / width;
                                last[7] = prior.bottom / height;
                                prior.cluster += segment;
                                priorNode = node;
                                text += segment;
                                continue;
                            }
                        }
                        if (!box) continue;
                        if (whitespace) {
                            pendingSpace = true;
                            continue;
                        }

                        if (prior) {
                            trimDanglingJoiner();
                            const { same, thickness, separation } = lineRelation(box, prior, direction);
                            if (!same || separation > thickness * 3) {
                                text += '\n';
                                line++;
                            } else if (pendingSpace || priorNode !== node && separation > Math.max(1, thickness * .18)) {
                                text += ' ';
                            }
                        }
                        glyphs.push([text.length, segment.length, line, direction,
                            box.left / width, box.top / height, box.right / width, box.bottom / height]);
                        text += segment;
                        prior = { ...box, direction, cluster: segment };
                        priorNode = node;
                        pendingSpace = false;
                    }
                }
                return result();
            })
            """;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private HwndSource? host;
    private CoreWebView2Environment? environment;
    private CoreWebView2Controller? controller;
    private Task? initialization;
    private CaptureRequest? current;
    private bool disposed;

    private sealed class CaptureRequest(FixedEpubLayout layout, Uri source, CancellationToken token)
    {
        internal FixedEpubLayout Layout { get; } = layout;
        internal Uri Source { get; } = source;
        internal CancellationToken Token { get; } = token;
        internal ulong? NavigationId { get; set; }
        internal TaskCompletionSource<bool> Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal Task<BitmapSource> CaptureAsync(FixedEpubLayout layout, FixedEpubPage page, int pixelWidth,
        CancellationToken token) => RenderAsync(layout, page, pixelWidth, CaptureBitmapAsync, token);

    internal Task<FixedEpubCapture> CapturePageAsync(FixedEpubLayout layout, FixedEpubPage page, int pixelWidth,
        CancellationToken token) => RenderAsync(layout, page, pixelWidth, static async (browser, width, ct) =>
    {
        var geometry = await ReadVisibleGeometryAsync(browser, VisiblePageRequest, ct);
        return new FixedEpubCapture(await CaptureBitmapAsync(browser, width, ct), geometry.Text, geometry.Navigation);
    }, token);

    internal Task<PdfTextPage?> CaptureTextAsync(FixedEpubLayout layout, FixedEpubPage page, CancellationToken token)
    {
        return dispatcher.CheckAccess() ? Read() : dispatcher.InvokeAsync(Read, DispatcherPriority.Background, token).Task.Unwrap();

        Task<PdfTextPage?> Read() => RenderAsync(layout, page, 1, static async (browser, _, ct) =>
            (await ReadVisibleGeometryAsync(browser, VisibleTextRequest, ct)).Text, token);
    }

    internal Task<FixedEpubNavigation?> CaptureNavigationAsync(FixedEpubLayout layout, FixedEpubPage page, CancellationToken token)
    {
        return dispatcher.CheckAccess() ? Read() : dispatcher.InvokeAsync(Read, DispatcherPriority.Background, token).Task.Unwrap();

        Task<FixedEpubNavigation?> Read() => RenderAsync(layout, page, 1, static async (browser, _, ct) =>
            (await ReadVisibleGeometryAsync(browser, VisibleNavigationRequest, ct)).Navigation, token);
    }

    private async Task<TResult> RenderAsync<TResult>(FixedEpubLayout layout, FixedEpubPage page, int pixelWidth,
        Func<CoreWebView2, int, CancellationToken, Task<TResult>> readResult, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await captureGate.WaitAsync(operation.Token);
        try
        {
            operation.CancelAfter(TimeSpan.FromSeconds(25));
            if (initialization is { IsFaulted: true }) initialization = null;
            await (initialization ??= InitializeAsync()).WaitAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var view = controller!;
            var browser = view.CoreWebView2;
            var width = (int)Math.Ceiling(page.Size.Width);
            var height = (int)Math.Ceiling(page.Size.Height);
            var outputScale = Math.Min((double)Math.Clamp(pixelWidth, 1, MaximumOutputDimension) / width,
                (double)MaximumOutputDimension / height);
            var rasterScale = Math.Max(MinimumRasterScale, outputScale);
            if (width * rasterScale > MaximumCaptureDimension || height * rasterScale > MaximumCaptureDimension ||
                width * rasterScale * height * rasterScale > 32_000_000)
                throw new InvalidDataException("固定版式 EPUB 的页面尺寸超出渲染限制。");

            var path = string.Join('/', page.Path.Split('/').Select(Uri.EscapeDataString));
            var request = new CaptureRequest(layout, new Uri($"https://{Guid.NewGuid():N}.seareader.invalid/{path}"), operation.Token);
            current = request;
            view.RasterizationScale = rasterScale;
            view.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
            browser.Navigate(request.Source.AbsoluteUri);
            if (!await request.Loaded.Task.WaitAsync(operation.Token))
                throw new InvalidDataException("无法呈现此固定版式 EPUB 页面。");

            // Apply the footnote display rule and await fonts using trusted viewer code.
            await browser.CallDevToolsProtocolMethodAsync("Runtime.evaluate", PreparePageRequest)
                .WaitAsync(operation.Token);
            var outputWidth = Math.Max(1, (int)Math.Round(width * outputScale));
            return await readResult(browser, outputWidth, operation.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            throw new IOException("固定版式 EPUB 页面渲染超时。");
        }
        finally
        {
            current = null;
            if (operation.IsCancellationRequested && controller is { } view)
            {
                try { view.CoreWebView2.Stop(); }
                catch (COMException) { }
            }
            captureGate.Release();
        }
    }

    private static string GeometryRequest(string arguments) => JsonSerializer.Serialize(new
    {
        returnByValue = true,
        expression = GeometryExpression + "(" + arguments + ")"
    });

    private static async Task<(PdfTextPage? Text, FixedEpubNavigation? Navigation)> ReadVisibleGeometryAsync(CoreWebView2 browser,
        string request, CancellationToken token)
    {
        try
        {
            var response = await browser.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request).WaitAsync(token);
            if (response.Length > 6_000_000) return default;
            using var result = JsonDocument.Parse(response);
            if (!result.RootElement.TryGetProperty("result", out var evaluation) ||
                !evaluation.TryGetProperty("value", out var value)) return default;
            var text = ReferenceEquals(request, VisibleNavigationRequest) ? null : ReadVisibleText(value, token);
            var navigation = ReferenceEquals(request, VisibleTextRequest) ? null : ReadVisibleNavigation(value, token);
            return (text, navigation);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or
            IndexOutOfRangeException or KeyNotFoundException or COMException)
        {
            token.ThrowIfCancellationRequested();
            System.Diagnostics.Trace.TraceWarning($"Fixed-layout EPUB page geometry unavailable: {error.Message}");
            return default;
        }
    }

    private static PdfTextPage? ReadVisibleText(JsonElement value, CancellationToken token)
    {
        var text = value.GetProperty("text").GetString() ?? "";
        var source = value.GetProperty("glyphs");
        if (text.Length > 65536 || source.GetArrayLength() > 8192) return null;
        var glyphs = new List<PdfTextPage.Glyph>(source.GetArrayLength());
        int previousEnd = 0;
        int previousLine = 0;
        foreach (var entry in source.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var offset = entry[0].GetInt32();
            var length = entry[1].GetInt32();
            var line = entry[2].GetInt32();
            var orientation = entry[3].GetInt32() switch
            {
                1 => TextOrientation.Rotate90,
                2 => TextOrientation.Rotate180,
                3 => TextOrientation.Rotate270,
                _ => TextOrientation.Horizontal
            };
            if (offset < previousEnd || length <= 0 || offset > text.Length - length || line < previousLine ||
                !TryReadBounds(entry, 4, out var bounds)) return null;
            glyphs.Add(new PdfTextPage.Glyph(offset, length, line, bounds, orientation));
            previousEnd = offset + length;
            previousLine = line;
        }
        return new PdfTextPage(text, glyphs);
    }

    private static FixedEpubNavigation? ReadVisibleNavigation(JsonElement value, CancellationToken token)
    {
        var sources = value.GetProperty("links");
        var destinations = value.GetProperty("anchors");
        if (sources.GetArrayLength() > 256 || destinations.GetArrayLength() > 2048) return null;
        var links = new List<FixedEpubLink>(sources.GetArrayLength());
        foreach (var entry in sources.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (entry[0].GetString() is not { Length: > 0 and <= 4096 } href || string.IsNullOrWhiteSpace(href) ||
                href.Any(char.IsControl) || entry[1].GetString() is not { Length: <= 240 } title ||
                entry[2].GetArrayLength() is < 1 or > 48) continue;
            var bounds = new List<Rect>(entry[2].GetArrayLength());
            foreach (var fragment in entry[2].EnumerateArray())
                if (TryReadBounds(fragment, 0, out var box)) bounds.Add(box);
            if (bounds.Count > 0) links.Add(new FixedEpubLink(href, title, bounds.AsReadOnly(), entry[3].GetBoolean()));
        }
        var anchors = new Dictionary<string, Rect>(destinations.GetArrayLength(), StringComparer.Ordinal);
        foreach (var entry in destinations.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (entry[0].GetString() is { Length: > 0 and <= 1024 } id && !id.Any(char.IsControl) &&
                TryReadBounds(entry[1], 0, out var bounds)) anchors.TryAdd(id, bounds);
        }
        return new FixedEpubNavigation(links.AsReadOnly(), new System.Collections.ObjectModel.ReadOnlyDictionary<string, Rect>(anchors));
    }

    private static bool TryReadBounds(JsonElement entry, int offset, out Rect bounds)
    {
        double left = entry[offset].GetDouble(), top = entry[offset + 1].GetDouble();
        double right = entry[offset + 2].GetDouble(), bottom = entry[offset + 3].GetDouble();
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom) ||
            left < 0 || top < 0 || right > 1 || bottom > 1 || right <= left || bottom <= top)
        {
            bounds = Rect.Empty;
            return false;
        }
        bounds = new Rect(left, top, right - left, bottom - top);
        return true;
    }

    private async Task InitializeAsync()
    {
        try
        {
            // Subresource interception does not cover browser preconnects. Deny network routes as well.
            var offline = new CoreWebView2EnvironmentOptions(OfflineBrowserArguments);
            environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDirectory, options: offline);
            if (disposed) return;
            // Keep the HWND hidden while allowing its child compositor to produce a frame.
            host = new HwndSource(new HwndSourceParameters("SeaReader EPUB renderer")
            {
                Width = 1, Height = 1,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP without WS_VISIBLE
                ExtendedWindowStyle = 0x08000080 // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            });
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "SeaReaderFixedEpub";
            options.IsInPrivateModeEnabled = true;
            controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle, options);
            if (disposed) return;
            controller.ShouldDetectMonitorScaleChanges = false;
            controller.BoundsMode = CoreWebView2BoundsMode.UseRasterizationScale;
            controller.AllowExternalDrop = false;
            controller.IsVisible = true;
            var browser = controller.CoreWebView2;
            var settings = browser.Settings;
            settings.IsScriptEnabled = false;
            settings.IsWebMessageEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsStatusBarEnabled = false;
            browser.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            browser.WebResourceRequested += OnResourceRequested;
            browser.NavigationStarting += OnNavigationStarting;
            browser.NavigationCompleted += OnNavigationCompleted;
            browser.FrameNavigationStarting += (_, e) => e.Cancel = true;
            browser.NewWindowRequested += (_, e) => e.Handled = true;
            browser.DownloadStarting += (_, e) => e.Cancel = true;
            browser.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            browser.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            browser.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
        }
        catch (WebView2RuntimeNotFoundException error)
        {
            CloseHost();
            throw new InvalidOperationException("阅读固定版式 EPUB 需要 Microsoft Edge WebView2 运行时。", error);
        }
        catch
        {
            CloseHost();
            throw;
        }
        finally { if (disposed) CloseHost(); }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (current is { NavigationId: null } request && !request.Token.IsCancellationRequested &&
            Uri.TryCreate(e.Uri, UriKind.Absolute, out var address) && request.Source.Equals(address))
            request.NavigationId = e.NavigationId;
        else e.Cancel = true;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (current is { } request && request.NavigationId == e.NavigationId)
            request.Loaded.TrySetResult(e.IsSuccess && e.HttpStatusCode == 200);
    }

    private async void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var engine = environment;
        if (engine == null) return;
        if (current is not { } request || e.Request.Method != "GET" ||
            !TryReadPath(e.Request.Uri, request.Source.Host, out var path))
        {
            e.Response = BlockedResponse(engine);
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var asset = await request.Layout.ReadAssetAsync(path, request.Token);
            if (asset != null && ReferenceEquals(current, request) && !request.Token.IsCancellationRequested &&
                FixedEpubLayout.SupportedAssetMediaType(asset.MediaType) is { } type)
            {
                e.Response = engine.CreateWebResourceResponse(new MemoryStream(asset.Bytes, writable: false), 200, "OK",
                    $"Content-Type: {type}\r\nContent-Security-Policy: {ContentPolicy}\r\n" +
                    "X-Content-Type-Options: nosniff\r\nX-DNS-Prefetch-Control: off\r\n" +
                    "Cache-Control: no-store\r\nReferrer-Policy: no-referrer");
            }
            else e.Response = BlockedResponse(engine);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            try { e.Response = BlockedResponse(engine); }
            catch (COMException) { }
        }
        catch (COMException) { }
        finally
        {
            try { deferral.Complete(); }
            catch (COMException) { }
        }
    }

    private static bool TryReadPath(string resource, string hostName, out string path)
    {
        path = "";
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps ||
            address.Host != hostName || !address.IsDefaultPort || address.UserInfo.Length != 0 || address.Query.Length != 0)
            return false;
        var parts = address.GetComponents(UriComponents.Path, UriFormat.UriEscaped).Split('/');
        for (var index = 0; index < parts.Length; index++)
        {
            var part = Uri.UnescapeDataString(parts[index]);
            if (part.Length == 0 || part is "." or ".." || part.Contains('/') || part.Contains('\\') || part.Any(char.IsControl))
                return false;
            parts[index] = part;
        }
        path = string.Join('/', parts);
        return true;
    }

    private static CoreWebView2WebResourceResponse BlockedResponse(CoreWebView2Environment engine) =>
        engine.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Cache-Control: no-store");

    private static async Task<BitmapSource> CaptureBitmapAsync(CoreWebView2 browser, int width, CancellationToken token)
    {
        var image = new MemoryStream();
        Task? pending = null;
        try
        {
            pending = browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            await pending.WaitAsync(token);
            image.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = image;
            bitmap.DecodePixelWidth = width;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (pending is { IsCompleted: false }) _ = ReleaseCaptureStreamAsync(pending, image);
            else image.Dispose();
        }
    }

    private static async Task ReleaseCaptureStreamAsync(Task pending, MemoryStream image)
    {
        using (image)
        {
            try { await pending.ConfigureAwait(false); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning(error.Message); }
        }
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        if (initialization is not { IsCompleted: false }) CloseHost();
        lifetime.Dispose();
    }

    private void CloseHost()
    {
        controller?.Close();
        controller = null;
        host?.Dispose();
        host = null;
        environment = null;
    }
}
