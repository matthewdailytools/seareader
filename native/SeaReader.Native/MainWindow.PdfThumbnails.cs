using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const int PdfThumbnailCacheSize = 48;
    private const int PdfThumbnailPreload = 2;

    private IPageBook? pdfThumbnailBook;
    private PdfPageThumbnail[] pdfThumbnailItems = [];
    private PdfPageThumbnail[] pdfThumbnailTargets = [];
    private readonly LinkedList<PdfPageThumbnail> pdfThumbnailCache = new();
    private CancellationTokenSource? pdfThumbnailWork;
    private PdfPageThumbnail? pdfThumbnailLoading;
    private int pdfThumbnailCurrent = -1;
    private bool pdfThumbnailsEnabled;
    private bool pdfThumbnailRefreshQueued;

    private bool CanLoadPdfThumbnails => !closing && pdfThumbnailsEnabled && shownPanel == Panel.PdfPages &&
        PdfThumbnailList is { IsVisible: true } && reading is { IsCancellationRequested: false } &&
        pdfThumbnailBook != null && ReferenceEquals(pdfThumbnailBook, pdf);

    private void DisplayPdfThumbnails()
    {
        if (pdf is not { } source || shownPanel != Panel.PdfPages) return;
        if (!ReferenceEquals(pdfThumbnailBook, source))
        {
            ResetPdfThumbnails();
            pdfThumbnailBook = source;
            pdfThumbnailItems = Enumerable.Range(0, source.PageCount).Select(index => new PdfPageThumbnail(index)).ToArray();
            PdfThumbnailList.ItemsSource = pdfThumbnailItems;
        }

        pdfThumbnailsEnabled = true;
        SetCurrentPdfThumbnail(scroll: true);
        QueuePdfThumbnailRefresh();
    }

    private void SuspendPdfThumbnails()
    {
        pdfThumbnailsEnabled = false;
        pdfThumbnailTargets = [];
        pdfThumbnailWork?.Cancel();
    }

    private void ResetPdfThumbnails()
    {
        SuspendPdfThumbnails();
        foreach (var item in pdfThumbnailCache) item.SetImage(null);
        pdfThumbnailCache.Clear();
        pdfThumbnailBook = null;
        pdfThumbnailItems = [];
        pdfThumbnailCurrent = -1;
        if (PdfThumbnailList != null) PdfThumbnailList.ItemsSource = null;
    }

    private void UpdatePdfThumbnailSelection()
    {
        if (shownPanel != Panel.PdfPages || pdf == null || !ReferenceEquals(pdfThumbnailBook, pdf)) return;
        pdfThumbnailsEnabled = true;
        SetCurrentPdfThumbnail(scroll: pdfThumbnailCurrent != pdfPage);
        QueuePdfThumbnailRefresh();
    }

    private void SetCurrentPdfThumbnail(bool scroll)
    {
        if (pdfThumbnailCurrent != pdfPage)
        {
            if (pdfThumbnailCurrent >= 0) pdfThumbnailItems[pdfThumbnailCurrent].SetCurrent(false);
            pdfThumbnailCurrent = pdfPage;
            pdfThumbnailItems[pdfThumbnailCurrent].SetCurrent(true);
        }
        if (!scroll) return;
        PdfThumbnailList.SelectedIndex = pdfPage;
        PdfThumbnailList.ScrollIntoView(pdfThumbnailItems[pdfPage]);
    }

    private void OnPdfThumbnailViewportChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 && CanLoadPdfThumbnails) RefreshPdfThumbnailTargets();
        else if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0 || e.ExtentHeightChange != 0)
            QueuePdfThumbnailRefresh();
    }

    private void OnPdfThumbnailSizeChanged(object sender, SizeChangedEventArgs e) => QueuePdfThumbnailRefresh();

    private void QueuePdfThumbnailRefresh()
    {
        if (!CanLoadPdfThumbnails || pdfThumbnailRefreshQueued) return;
        pdfThumbnailRefreshQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            pdfThumbnailRefreshQueued = false;
            if (CanLoadPdfThumbnails) RefreshPdfThumbnailTargets();
        }, DispatcherPriority.Background);
    }

    private void RefreshPdfThumbnailTargets()
    {
        pdfThumbnailTargets = ReadPdfThumbnailTargets();
        if (pdfThumbnailLoading is { } current && !pdfThumbnailTargets.Contains(current) &&
            pdfThumbnailWork is { IsCancellationRequested: false } work) work.Cancel();

        // Touch visible items before more work can evict one of them.
        for (int index = pdfThumbnailTargets.Length - 1; index >= 0; index--)
            if (pdfThumbnailTargets[index] is { Image: not null } item) TouchPdfThumbnail(item);
        EnsurePdfThumbnailWork();
    }

    private PdfPageThumbnail[] ReadPdfThumbnailTargets()
    {
        if (Descendant<VirtualizingStackPanel>(PdfThumbnailList) is not { } host ||
            Descendant<ScrollContentPresenter>(PdfThumbnailList) is not { ActualWidth: > 0, ActualHeight: > 0 } viewport)
            return [];

        var visible = new List<PdfPageThumbnail>();
        var bounds = new Rect(viewport.RenderSize);
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(host); index++)
        {
            if (VisualTreeHelper.GetChild(host, index) is not ListBoxItem { DataContext: PdfPageThumbnail item } container)
                continue;
            var card = container.TransformToAncestor(viewport).TransformBounds(new Rect(container.RenderSize));
            var intersect = Rect.Intersect(card, bounds);
            if (intersect.Width > 0 && intersect.Height > 0) visible.Add(item);
        }
        if (visible.Count == 0) return [];

        var ordered = visible.OrderBy(item => item.Index == pdfPage ? 0 : 1).ThenBy(item => item.Index)
            .Take(PdfThumbnailCacheSize).ToList();
        int first = visible.Min(item => item.Index);
        int last = visible.Max(item => item.Index);
        for (int distance = 1; distance <= PdfThumbnailPreload && ordered.Count < PdfThumbnailCacheSize; distance++)
        {
            if (first - distance >= 0) ordered.Add(pdfThumbnailItems[first - distance]);
            if (last + distance < pdfThumbnailItems.Length && ordered.Count < PdfThumbnailCacheSize)
                ordered.Add(pdfThumbnailItems[last + distance]);
        }
        return ordered.ToArray();
    }

    private PdfPageThumbnail? NextPdfThumbnail() =>
        pdfThumbnailTargets.FirstOrDefault(item => item.Image == null && !item.Failed);

    private void EnsurePdfThumbnailWork()
    {
        if (!CanLoadPdfThumbnails || pdfThumbnailWork != null || NextPdfThumbnail() == null) return;
        var work = CancellationTokenSource.CreateLinkedTokenSource(reading?.Token ?? CancellationToken.None);
        pdfThumbnailWork = work;
        _ = RunPdfThumbnailWorkAsync(pdfThumbnailBook!, work);
    }

    private async Task RunPdfThumbnailWorkAsync(IPageBook source, CancellationTokenSource work)
    {
        var ct = work.Token;
        try
        {
            while (true)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                ct.ThrowIfCancellationRequested();
                if (!CanLoadPdfThumbnails || !ReferenceEquals(source, pdfThumbnailBook) || NextPdfThumbnail() is not { } item)
                    break;

                pdfThumbnailLoading = item;
                try
                {
                    var image = await source.RenderThumbnailAsync(item.Index, ct);
                    ct.ThrowIfCancellationRequested();
                    if (CanLoadPdfThumbnails && ReferenceEquals(source, pdfThumbnailBook) && pdfThumbnailTargets.Contains(item))
                        CachePdfThumbnail(item, image);
                }
                catch (Exception error)
                {
                    ct.ThrowIfCancellationRequested();
                    item.Failed = true;
                    Trace.TraceWarning("SeaReader PDF page {0} thumbnail: {1}", item.Index + 1, error.Message);
                }
                finally { pdfThumbnailLoading = null; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            work.Dispose();
            pdfThumbnailWork = null;
            EnsurePdfThumbnailWork();
        }
    }

    private void CachePdfThumbnail(PdfPageThumbnail item, BitmapSource image)
    {
        item.SetImage(image);
        TouchPdfThumbnail(item);
        if (pdfThumbnailCache.Count <= PdfThumbnailCacheSize) return;
        var oldest = pdfThumbnailCache.Last!.Value;
        pdfThumbnailCache.RemoveLast();
        oldest.SetImage(null);
    }

    private void TouchPdfThumbnail(PdfPageThumbnail item)
    {
        pdfThumbnailCache.Remove(item);
        pdfThumbnailCache.AddFirst(item);
    }

    private sealed class PdfPageThumbnail(int index) : INotifyPropertyChanged
    {
        public int Index { get; } = index;
        public string Label => $"第 {Index + 1} 页";
        public BitmapSource? Image { get; private set; }
        public bool IsCurrent { get; private set; }
        internal bool Failed { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void SetImage(BitmapSource? image)
        {
            if (ReferenceEquals(Image, image)) return;
            Image = image;
            PropertyChanged?.Invoke(this, new(nameof(Image)));
        }

        internal void SetCurrent(bool value)
        {
            if (IsCurrent == value) return;
            IsCurrent = value;
            PropertyChanged?.Invoke(this, new(nameof(IsCurrent)));
        }
    }
}
