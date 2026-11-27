using System.Runtime.InteropServices;
using System.Windows;

namespace SeaReader.Native;

public partial class MainWindow
{
    private long pageTransitionVersion;
    private (int Direction, long Version, bool Released)? draggedPageTurn;

    private PageTurnEffect AvailablePageTurnEffect =>
        pdf is not PdfBook && !continuous && PageTurnLayer.IsVisible && SystemParameters.ClientAreaAnimation
            ? ConfiguredPageTurnEffect : PageTurnEffect.None;

    private PageTurnEffect PageTurnEffectFor(int direction)
    {
        var effect = AvailablePageTurnEffect;
        if (effect != PageTurnEffect.Paper || !HasSpread || textPagination is not { } pages) return effect;
        var last = pages.Count - 1;
        // A change in paper width is a fade across the stable stage, including the surrounding margin.
        return centeredLastLeaf || (last & 1) == 0 && NextIndexedPage(pages, direction) == last
            ? PageTurnEffect.Fade : effect;
    }

    private long? BeginPageTransition(int direction)
    {
        var effect = PageTurnEffectFor(direction);
        if (draggedPageTurn is { Released: true } drag && drag.Direction == direction && PageTurnLayer.HasFrame &&
            effect == PageTurnEffect.Paper)
        {
            draggedPageTurn = null;
            return drag.Version;
        }
        ResetPageTransition();
        return effect != PageTurnEffect.None && HoldPageTransition(direction, effect, .65) ? pageTransitionVersion : null;
    }

    private bool HoldPageTransition(int direction, PageTurnEffect effect, double anchorY)
    {
        try
        {
            RefreshTextPageEdgeClip();
            var snapshot = PageTurnSurface.Capture(PaperFrame, ReaderSurface.Background);
            return snapshot != null && PageTurnLayer.Hold(snapshot, MapHorizontalPageTurn(direction), effect,
                pdf is FixedEpubPageBook ? (System.Windows.Media.Brush)Resources["ReaderPaper"] : Paper.Background, anchorY);
        }
        catch (Exception error) when (IsPageTransitionError(error))
        {
            ResetPageTransition();
            return false;
        }
    }

    private void PreviewTouchPageTurn(double horizontal, double anchorY)
    {
        var direction = MapHorizontalPageTurn(-Math.Sign(horizontal));
        if (direction == 0 || PageTurnEffectFor(direction) != PageTurnEffect.Paper || moving || turningPage || seekingPosition ||
            chapterLoadPending != null || !CanTurnPage(direction))
        {
            if (draggedPageTurn is { Released: false }) ResetPageTransition();
            return;
        }

        if (draggedPageTurn is not { Released: false } drag || drag.Direction != direction)
        {
            ResetPageTransition();
            if (!HoldPageTransition(direction, PageTurnEffect.Paper, anchorY)) return;
            draggedPageTurn = (direction, pageTransitionVersion, false);
        }

        // Follow the finger early in a short swipe, but leave room to finish after the next page loads.
        var distance = Math.Max(220, SpreadPageContent.ActualWidth * (doublePage ? .55 : .80));
        try { PageTurnLayer.Preview(Math.Min(Math.Abs(horizontal) / distance, .65)); }
        catch (Exception error) when (IsPageTransitionError(error)) { ResetPageTransition(); }
    }

    private long? ReleaseTouchPageTurn(int direction)
    {
        if (draggedPageTurn is not { Released: false } drag || drag.Direction != direction) return null;
        draggedPageTurn = (drag.Direction, drag.Version, true);
        return drag.Version;
    }

    private void RewindTouchPageTurn(long? releasedVersion = null)
    {
        if (draggedPageTurn is not { } drag || drag.Released != releasedVersion.HasValue ||
            releasedVersion.HasValue && releasedVersion != drag.Version) return;
        draggedPageTurn = null;
        try { PageTurnLayer.Rewind(() => FinishPageTransition(drag.Version)); }
        catch (Exception error) when (IsPageTransitionError(error)) { ResetPageTransition(); }
    }

    private void CompletePageTransition(long? version, bool changed)
    {
        if (version is not { } transition || transition != pageTransitionVersion) return;
        if (!changed || AvailablePageTurnEffect == PageTurnEffect.None)
        {
            ResetPageTransition();
            return;
        }

        try
        {
            RefreshTextPageEdgeClip();
            PageTurnLayer.Reveal(() => FinishPageTransition(transition));
        }
        catch (Exception error) when (IsPageTransitionError(error)) { ResetPageTransition(); }
    }

    private static bool IsPageTransitionError(Exception error) =>
        error is ExternalException or ArgumentException or InvalidOperationException or OutOfMemoryException;

    private void FinishPageTransition(long version)
    {
        if (version == pageTransitionVersion) ResetPageTransition();
    }

    private void ResetPageTransition()
    {
        pageTransitionVersion++;
        draggedPageTurn = null;
        PageTurnLayer.Clear();
    }

    private void OnPageTurnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => ResetPageTransition();
}
