using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private sealed record FootnoteAnchor(RichTextBox Body, Hyperlink Link);
    private FootnoteAnchor? footnoteAnchor;
    private CancellationTokenSource? footnoteRequest;
    private bool footnotePlacementQueued;

    private async void OnReaderReference(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox body || (e.OriginalSource as Hyperlink ?? e.Source as Hyperlink) is not
            { Tag: EpubReference reference } link) return;
        e.Handled = true;
        if (epub is not EpubDocument document || reading is not { IsCancellationRequested: false } session ||
            closing || moving || turningPage || seekingPosition || !body.IsVisible) return;

        if (reference.Kind == EpubReferenceKind.InternalLink)
        {
            await FollowEpubLinkAsync(document, session, body, link, reference);
            return;
        }

        var glossary = reference.Kind == EpubReferenceKind.Glossary;
        var pending = BeginReferencePanel(session.Token, glossary ? "术语释义" : "脚注",
            glossary ? "正在读取术语释义…" : "正在读取脚注…");
        footnoteAnchor = new FootnoteAnchor(body, link);
        var token = pending.Token;
        OpenPanel(Panel.Footnote);
        try
        {
            if (glossary)
            {
                var entry = await document.ReadGlossaryDefinitionAsync(reference, token);
                if (ReferenceEquals(footnoteRequest, pending) && !token.IsCancellationRequested)
                    DisplayGlossaryDefinition(entry, "找不到这条术语的释义。");
            }
            else
            {
                var note = await document.ReadFootnoteAsync(reference, token);
                if (ReferenceEquals(footnoteRequest, pending) && !token.IsCancellationRequested)
                    DisplayFootnoteText(string.IsNullOrWhiteSpace(note) ? "找不到这条脚注的正文。" : note);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(footnoteRequest, pending) && !token.IsCancellationRequested)
                DisplayFootnoteText(glossary ? "暂时无法读取这条术语的释义。" : "暂时无法读取这条脚注。");
        }
    }

    private CancellationTokenSource BeginReferencePanel(CancellationToken session, string title, string loading)
    {
        StopReaderTap();
        ClosePageJump();
        if (shownPanel == Panel.Footnote) ClosePanel(false);
        FootnotePanelTitle.Text = title;
        AutomationProperties.SetName(FootnotePanel, title);
        AutomationProperties.SetName(FootnotePanelCloseButton, $"关闭{title}");
        AutomationProperties.SetName(FootnoteBody, title == "脚注" ? "脚注正文" : "术语释义正文");
        FootnotePanelCloseButton.ToolTip = $"关闭{title}（Esc）";
        FootnoteBody.Text = loading;
        return footnoteRequest = CancellationTokenSource.CreateLinkedTokenSource(session);
    }

    private void DisplayFootnoteText(string note)
    {
        FootnoteBody.Text = note;
        FootnoteBody.ScrollToHome();
        if (FootnoteBody.IsVisible && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(FootnoteBody)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void EndFootnoteSession()
    {
        footnoteAnchor = null;
        fixedFootnoteAnchor = null;
        if (footnoteRequest is { } pending)
        {
            pending.Cancel();
            pending.Dispose();
        }
        footnoteRequest = null;
        EndGlossaryView();
        FootnoteBody.Clear();
    }

    private void QueueReflowFootnotePlacement()
    {
        if (closing || footnotePlacementQueued || shownPanel != Panel.Footnote || footnoteAnchor == null) return;
        footnotePlacementQueued = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            footnotePlacementQueued = false;
            if (!closing && shownPanel == Panel.Footnote && footnoteAnchor != null) PlaceFootnotePanel();
        }, DispatcherPriority.Render);
    }

    private void PlaceFootnotePanel()
    {
        const double inset = 12;
        const double gap = 7;
        var browse = GlossaryBrowsePanel.Visibility == Visibility.Visible;
        var width = Math.Clamp(ReaderSurface.ActualWidth - inset * 2, 0, browse ? 404 : 360);
        var topEdge = controlsVisible ? 56 : inset;
        var bottomInset = controlsVisible ? 56 : 30;
        var bottomEdge = Math.Max(topEdge, ReaderSurface.ActualHeight - bottomInset);
        var anchor = (footnoteAnchor is { } source ? ReadFootnoteAnchor(source) : null) ??
            (fixedFootnoteAnchor is { } fixedSource ? ReadFixedFootnoteAnchor(fixedSource) : null) ??
            ReadGlossarySelectionAnchor() ?? new Rect(ReaderSurface.ActualWidth / 2, ReaderSurface.ActualHeight / 3, 0, 0);
        var left = Math.Clamp(anchor.X + anchor.Width / 2 - width / 2, inset,
            Math.Max(inset, ReaderSurface.ActualWidth - width - inset));
        FootnotePanel.Width = width;
        if (browse)
        {
            FootnotePanel.VerticalAlignment = VerticalAlignment.Top;
            FootnotePanel.MaxHeight = Math.Min(340, bottomEdge - topEdge);
            FootnotePanel.Margin = new Thickness(left, topEdge, 0, 0);
            return;
        }
        var below = Math.Clamp(anchor.Bottom + gap, topEdge, bottomEdge);
        var above = Math.Clamp(anchor.Top - gap, topEdge, bottomEdge);
        var placeBelow = bottomEdge - below >= Math.Min(170, above - topEdge);
        FootnotePanel.MaxHeight = Math.Min(280, placeBelow ? bottomEdge - below : above - topEdge);
        FootnotePanel.VerticalAlignment = placeBelow ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        FootnotePanel.Margin = placeBelow ? new Thickness(left, below, 0, 0) :
            new Thickness(left, 0, 0, ReaderSurface.ActualHeight - above);
    }

    private Rect? ReadFootnoteAnchor(FootnoteAnchor anchor)
    {
        var (body, link) = anchor;
        if (!body.IsVisible || link.Parent is not Paragraph row || !ReferenceEquals(row.Parent, body.Document) ||
            !link.ContentStart.HasValidLayout) return null;
        var bounds = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (bounds.IsEmpty) return null;
        return new Rect(body.TranslatePoint(bounds.TopLeft, ReaderSurface), bounds.Size);
    }
}
