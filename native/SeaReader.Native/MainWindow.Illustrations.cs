using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace SeaReader.Native;

public partial class MainWindow
{
    private EpubIllustrationWindow? illustrationWindow;

    private void OnOpenIllustration(object sender, RoutedEventArgs e)
    {
        if (sender is not EpubIllustrationButton button || book == null || closing || moving || seekingPosition ||
            turningPage || illustrationWindow != null || !ReaderSurface.IsVisible) return;

        var body = FacingTextList.IsAncestorOf(button) ? FacingTextList : TextList;
        if (!EpubIllustrationLayout.IsVisibleIn(body, button)) return;

        e.Handled = true;
        StopReaderTap();
        hoverTimer.Stop();
        var hadFocus = button.IsKeyboardFocusWithin;
        try
        {
            illustrationWindow = new EpubIllustrationWindow(button.Figure, this, epub as EpubDocument);
            illustrationWindow.ShowDialog();
        }
        finally
        {
            illustrationWindow = null;
            if (!closing)
            {
                if (hoverVisible) RestartHoverTimer();
                if (hadFocus && EpubIllustrationLayout.IsVisibleIn(body, button, fully: true)) button.Focus();
            }
        }
    }

    private static void UpdateIllustrationTabStops(RichTextBox body)
    {
        foreach (var button in EpubIllustrationLayout.Buttons(body))
            button.IsTabStop = EpubIllustrationLayout.IsVisibleIn(body, button, fully: true);
    }

    private bool MoveReaderInlineFocus(KeyEventArgs e, bool reverse)
    {
        var current = e.OriginalSource as IInputElement;
        var body = current switch
        {
            RichTextBox box => box,
            EpubIllustrationButton image => FacingTextList.IsAncestorOf(image) ? FacingTextList : TextList,
            Hyperlink { Parent: Paragraph row } when ReferenceEquals(row.Parent, FacingTextList.Document) => FacingTextList,
            Hyperlink { Parent: Paragraph row } when ReferenceEquals(row.Parent, TextList.Document) => TextList,
            _ => null
        };
        if (body == null) return false;
        var document = ReferenceEquals(body, TextList) ? text :
            ReferenceEquals(body, FacingTextList) && HasSpread ? facingPage?.Document : null;
        if (document == null || !document.HasIllustrations && !document.HasReferences) return false;

        var actions = VisibleInlineActions(body).ToArray();
        if (ReferenceEquals(current, body))
            return !reverse && actions.Length > 0 && ReferenceEquals(Keyboard.Focus(actions[0]), actions[0]);
        var index = Array.IndexOf(actions, current);
        if (reverse && index <= 0) return body.Focus();
        var next = index + (reverse ? -1 : 1);
        if (next >= 0 && next < actions.Length && ReferenceEquals(Keyboard.Focus(actions[next]), actions[next])) return true;

        // WPF walks past inline UI elements when tabbing out of a RichTextBox.
        return body.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private static IEnumerable<IInputElement> VisibleInlineActions(RichTextBox body)
    {
        foreach (var row in body.Document.Blocks.OfType<Paragraph>())
            foreach (var inline in row.Inlines)
            {
                if (inline is Hyperlink link && KeyboardNavigation.GetIsTabStop(link)) yield return link;
                else if (inline is InlineUIContainer { Child: Border { Child: EpubIllustrationButton { IsTabStop: true } image } })
                    yield return image;
            }
    }
}
