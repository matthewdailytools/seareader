using System.Windows;

namespace SeaReader.Native;

public partial class MainWindow
{
    private ReaderLocation? pageResizeLocation;

    private double ReadingLaneHeight() => ReadingLane.ActualHeight > 0
        ? ReadingLane.ActualHeight : Math.Max(0, ((FrameworkElement)Content).ActualHeight - 34);

    private bool CanFitTextSpread(double width) => ReaderPageGeometry.CanFitSpread(width, ReadingLaneHeight(),
        appearance.Size, appearance.Layout == "double", doublePage);

    private void UpdateReaderLayoutStatus()
    {
        ReaderLayoutChoices.IsEnabled = text != null && !continuous;
        var status = continuous ? "切换到左右翻页后可调整" :
            HasSpread && centeredLastLeaf ? "末页单独显示" :
            HasSpread ? "当前显示双页" :
            doublePage && !paginationFailed ? "正在排版双页…" :
            appearance.Layout == "double" && !doublePage ? "窗口空间不足，暂用单页" : "当前显示单页";
        ReaderLayoutStatus.Text = !continuous && UsesRightToLeftPageProgression ? status + " · 从右向左" : status;
    }

    private void UpdatePaperGeometry(bool preservePosition = false)
    {
        var width = ReadingLane.ActualWidth;
        var height = ReadingLaneHeight();
        if (width <= 0 || height <= 0) return;
        var headerMargin = ReaderHeader.Margin;
        // Pull the floating header toward the window edge when the paper's top margin gets short.
        var headerTop = text != null && !continuous ? Math.Clamp(height * .10 - 31, 0, 10) : 10;
        if (headerMargin.Top != headerTop)
            ReaderHeader.Margin = new Thickness(headerMargin.Left, headerTop, headerMargin.Right, headerMargin.Bottom);
        if (text == null || continuous)
        {
            var scrollSide = Math.Clamp(width * .03, 14, 36);
            PaperFrame.Margin = new Thickness(scrollSide, 16, scrollSide, 16);
            PaperFrame.MaxWidth = pdf != null && WantsPdfSpread ? 1180 : 680;
            PaperFrame.MaxHeight = double.PositiveInfinity;
            SpreadPageContent.MaxWidth = double.PositiveInfinity;
            return;
        }

        var geometry = ReaderPageGeometry.Measure(width, height, doublePage);
        if (preservePosition && (PaperFrame.MaxWidth != geometry.FrameWidth || PaperFrame.MaxHeight != geometry.LeafHeight) &&
            !moving && !turningPage && !seekingPosition && ReferenceEquals(text, windowDocument))
        {
            if (HasSpread) RememberSpreadPosition();
            else pageResizeLocation ??= CaptureGuidedLocation() ?? retainedLayoutLocation ?? CaptureReaderLocation();
        }
        PaperFrame.Margin = geometry.StageInsets;
        PaperFrame.MaxWidth = geometry.FrameWidth;
        PaperFrame.MaxHeight = geometry.LeafHeight;
        // The animation area keeps its spread size. The paper and hit targets follow the visible leaf.
        SpreadPageContent.MaxWidth = centeredLastLeaf ? geometry.LeafWidth : double.PositiveInfinity;
        ReaderFolio.Margin = new Thickness(0, 0, 0, geometry.FolioInset);
    }

    private void OnReadingLaneSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DismissInlineSelection();
        UpdatePaperGeometry(preservePosition: true);
        UpdatePageEdges();
        if (pdf != null) QueuePdfResize();
        QueueSpreadUpdate();
    }

    private void OnPageEdgeSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePageEdges();

    private void UpdatePageEdges()
    {
        var width = SpreadPageContent.ActualWidth;
        var height = SpreadPageContent.ActualHeight;
        if (width <= 0 || height <= 0) return;
        // Leave room for the centered page range between the corner buttons.
        var compactCorners = text != null && !continuous && width < 320;
        ReaderJumpBackLabel.Visibility = ReaderJumpForwardLabel.Visibility = compactCorners ? Visibility.Collapsed : Visibility.Visible;
        ReaderJumpBackGlyph.Margin = compactCorners ? new Thickness(0) : new Thickness(0, 0, 5, 0);
        ReaderJumpForwardGlyph.Margin = compactCorners ? new Thickness(0) : new Thickness(5, 0, 0, 0);
        // The visible paper is centered in the lane. End the targets inside its clear margin.
        var side = Math.Max(0, (ReadingLane.ActualWidth - width) / 2);
        var outside = Math.Min(side, 20);
        var sideOffset = side - outside;
        var cap = Math.Min(height * .15, 24);
        var top = Math.Max(0, (ReadingLane.ActualHeight - height) / 2) + cap;
        var leftBody = PrimaryPageOnRight ? FacingTextList : TextList;
        var rightBody = HasFacingTextColumn && !PrimaryPageOnRight ? FacingTextList : TextList;
        LeftPageEdge.Width = outside + (pdf == null ? PageEdgeInset(leftBody.Padding.Left) : 0);
        RightPageEdge.Width = outside + (pdf == null ? PageEdgeInset(rightBody.Padding.Right) : 0);
        LeftPageEdge.Margin = new Thickness(sideOffset, top, 0, top);
        RightPageEdge.Margin = new Thickness(0, top, sideOffset, top);
    }

    private static double PageEdgeInset(double padding) => Math.Clamp(padding - 6, 0, 34);

    private void UpdateTextInsets()
    {
        var margin = Math.Clamp(appearance.Margin, 12, 52);
        var first = new Thickness(margin, 30, margin, 24);
        var facing = first;
        if (text != null && !continuous && Paper.ActualWidth > 0 && TextList.ActualHeight > 0)
            (first, facing) = ReaderPageGeometry.TextInsets(Paper.ActualWidth, TextList.ActualHeight,
                doublePage, margin, TextList.Document.LineHeight, Paper.BorderThickness.Left + TextPagination.DocumentPadding.Left);
        if (PrimaryPageOnRight) (first, facing) = (facing, first);
        if (centeredLastLeaf)
        {
            // Recenter the text without changing the line measure used by the same spread index.
            var side = (first.Left + first.Right) / 2;
            first = new Thickness(side, first.Top, side, first.Bottom);
        }
        if (!Equals(Resources["ReaderInsets"], first)) Resources["ReaderInsets"] = first;
        if (!Equals(Resources["FacingReaderInsets"], facing)) Resources["FacingReaderInsets"] = facing;
        UpdatePageEdges();
        QueueTextPageEdgeClip();
        QueueReflowFootnotePlacement();
    }
}
