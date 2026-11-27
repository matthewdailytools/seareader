using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace SeaReader.Native;

public partial class StudyCardsWindow : Window
{
    private const int HorizontalMouseWheel = 0x020E;
    private const int WheelStep = 120;
    private const int WheelQuietMilliseconds = 220;
    private static readonly string[] ReaderBrushes =
        ["WindowSurface", "PaperSurface", "ReaderPaper", "ReaderPaperBorder", "MainInk", "MutedInk", "BorderInk", "AccentInk", "HoverSurface"];

    private readonly AnnotationEntry[] cards;
    private int position;
    private bool showingNote;
    private HwndSource? windowSource;
    private long lastWheelTime;
    private int wheelDirection;
    private int wheelDelta;
    private bool wheelTurned;

    internal StudyCardsWindow(IReadOnlyList<AnnotationEntry> cards, MainWindow owner, long? selectedId)
    {
        this.cards = cards.ToArray();
        position = Math.Max(0, Array.FindIndex(this.cards, card => card.Id == selectedId));
        InitializeComponent();
        Owner = owner;
        foreach (string key in ReaderBrushes)
            if (owner.TryFindResource(key) is { } brush) Resources[key] = brush;
        if (SystemParameters.HighContrast)
        {
            Resources["WindowSurface"] = Resources["PaperSurface"] = Resources["ReaderPaper"] = SystemColors.WindowBrush;
            Resources["MainInk"] = Resources["MutedInk"] = Resources["BorderInk"] =
                Resources["ReaderPaperBorder"] = Resources["AccentInk"] = SystemColors.WindowTextBrush;
            Resources["HoverSurface"] = SystemColors.ControlBrush;
        }

        var ownerArea = owner.ActualHeight > 0 ? owner.ActualHeight : SystemParameters.WorkArea.Height;
        Height = Math.Clamp(ownerArea, MinHeight, Height);
        ShowCard(announce: false);
    }

    internal AnnotationEntry? Destination { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(OnWindowMessage);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (windowSource is { IsDisposed: false }) windowSource.RemoveHook(OnWindowMessage);
        windowSource = null;
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var target = cards.Length == 0 ? CloseStudyCardsButton : FlipStudyCardButton;
        FocusManager.SetFocusedElement(this, target);
        target.Focus();
    }

    private void OnPreviousCard(object sender, RoutedEventArgs e) => MoveCard(-1);

    private void OnNextCard(object sender, RoutedEventArgs e) => MoveCard(1);

    private void MoveCard(int direction)
    {
        var next = position + direction;
        if (next < 0 || next >= cards.Length) return;
        position = next;
        showingNote = false;
        ShowCard(announce: true);
    }

    private void OnFlipCard(object sender, RoutedEventArgs e) => FlipCard();

    private void FlipCard()
    {
        if (cards.Length == 0) return;
        showingNote = !showingNote;
        ShowCard(announce: true);
    }

    private void OnLocateCard(object sender, RoutedEventArgs e)
    {
        if (cards.Length == 0 || string.IsNullOrWhiteSpace(cards[position].Position)) return;
        Destination = cards[position];
        DialogResult = true;
    }

    private void ShowCard(bool announce)
    {
        var current = cards.Length == 0 ? null : cards[position];
        StudyCardPosition.Text = current == null ? "0 / 0" : $"{position + 1} / {cards.Length}";
        AutomationProperties.SetName(StudyCardPosition, current == null ? "暂无卡片" : $"第 {position + 1} 张，共 {cards.Length} 张");
        StudyCardSide.Text = showingNote ? "笔记" : "摘录";
        StudyCardFlipLabel.Text = current == null ? "" : showingNote ? "翻到摘录" : "翻到笔记";
        AutomationProperties.SetName(FlipStudyCardButton, StudyCardFlipLabel.Text);
        AutomationProperties.SetHelpText(FlipStudyCardButton, "按 Enter 翻面，按空格前往下一张。");
        StudyCardText.Text = CardText(current);
        AutomationProperties.SetName(StudyCardText, current == null ? StudyCardText.Text :
            $"第 {position + 1} 张，共 {cards.Length} 张。{StudyCardSide.Text}。{StudyCardText.Text}");
        StudyCardScroll.ScrollToTop();
        PreviousStudyCardButton.IsEnabled = position > 0;
        NextStudyCardButton.IsEnabled = position + 1 < cards.Length;
        FlipStudyCardButton.IsEnabled = current != null;
        LocateStudyCardButton.IsEnabled = !string.IsNullOrWhiteSpace(current?.Position);
        if (announce && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(StudyCardText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private string CardText(AnnotationEntry? current)
    {
        if (current == null) return "保存高亮或笔记后，可以在这里复习。";
        if (showingNote) return string.IsNullOrWhiteSpace(current.Note) ? "定位原文后，可在笔记面板添加笔记。" : current.Note;
        return string.IsNullOrWhiteSpace(current.Quotation) ? "可定位原文查看这条笔记对应的内容。" : current.Quotation;
    }

    private void OnStudyCardKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Left:
                MoveCard(-1);
                break;
            case Key.Right:
                MoveCard(1);
                break;
            case Key.Space when Keyboard.FocusedElement == FlipStudyCardButton || Keyboard.FocusedElement == null:
                MoveCard(1);
                break;
            case Key.Enter when Keyboard.FocusedElement == FlipStudyCardButton || Keyboard.FocusedElement is not Button:
                if (!e.IsRepeat) FlipCard();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != HorizontalMouseWheel || Keyboard.Modifiers != ModifierKeys.None) return IntPtr.Zero;
        var delta = unchecked((short)(wParam.ToInt64() >> 16));
        var direction = Math.Sign(delta);
        if (direction == 0) return IntPtr.Zero;
        var now = Environment.TickCount64;
        if (now - lastWheelTime > WheelQuietMilliseconds || direction != wheelDirection)
        {
            wheelDirection = direction;
            wheelDelta = 0;
            wheelTurned = false;
        }
        lastWheelTime = now;
        if (!wheelTurned)
        {
            wheelDelta += delta;
            if (Math.Abs(wheelDelta) >= WheelStep)
            {
                wheelTurned = true;
                MoveCard(direction);
            }
        }
        handled = true;
        return IntPtr.Zero;
    }
}
