using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace SeaReader.Native;

public partial class ReadingGoalsWindow : Window
{
    private const long MillisecondsPerMinute = 60_000;
    private static readonly string[] GoalBrushes =
        ["LibraryCanvas", "LibraryPaper", "LibraryInk", "LibraryQuiet", "LibraryRule", "LibraryAccent", "LibrarySelection", "LibraryOnAccent"];
    private static readonly string[] WeekdayLabels = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    internal ReadingGoalsWindow(ReadingGoalsSnapshot snapshot, MainWindow owner)
    {
        InitializeComponent();
        Owner = owner;
        foreach (string key in GoalBrushes)
        {
            if (owner.LibrarySurface.TryFindResource(key) is { } brush) Resources[key] = brush;
        }

        var workArea = SystemParameters.WorkArea;
        Height = Math.Clamp(workArea.Height - 24, MinHeight, Height);

        var settings = snapshot.Settings;
        EnableReadingGoals.IsChecked = settings.Enabled;
        IncludePdfReading.IsChecked = settings.IncludePdf;
        DailyMinutesInput.Text = settings.DailyMinutes.ToString(CultureInfo.InvariantCulture);
        AnnualBooksInput.Text = settings.AnnualBooks.ToString(CultureInfo.InvariantCulture);

        var today = snapshot.Week.LastOrDefault(day => day.Day == snapshot.Today);
        TodayReading.Text = $"{ReadingDuration(today.Milliseconds)} / {settings.DailyMinutes} 分钟";
        TodayProgress.Value = Progress(today.Milliseconds, settings.DailyMinutes * MillisecondsPerMinute);
        AutomationProperties.SetHelpText(TodayProgress, TodayReading.Text);
        WeekDays.ItemsSource = snapshot.Week.Select(day => new WeekDayView(day, snapshot.Today)).ToArray();
        StreakReading.Text = $"当前连续 {snapshot.Streak} 天 · 最长连续 {snapshot.BestStreak} 天";
        AnnualReading.Text = $"{snapshot.BooksFinished} / {settings.AnnualBooks} 本";
        AnnualProgress.Value = Progress(snapshot.BooksFinished, settings.AnnualBooks);
        AutomationProperties.SetHelpText(AnnualProgress, AnnualReading.Text);
    }

    internal ReadingGoalsSettings? Settings { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryReadGoal(DailyMinutesInput, 1440, "每日目标请输入 1 到 1440 分钟。", out int minutes)) return;
        if (!TryReadGoal(AnnualBooksInput, 9999, "年度目标请输入 1 到 9999 本。", out int books)) return;

        Settings = new ReadingGoalsSettings(EnableReadingGoals.IsChecked == true, minutes, books, IncludePdfReading.IsChecked == true);
        DialogResult = true;
    }

    private bool TryReadGoal(TextBox input, int maximum, string error, out int value)
    {
        string text = string.Concat(input.Text.Trim().Select(character =>
            character is >= '０' and <= '９' ? (char)('0' + character - '０') : character));
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= maximum)
            return true;

        SettingsStatus.Text = error;
        SettingsStatus.Visibility = Visibility.Visible;
        GoalsSettingsExpander.IsExpanded = true;
        GoalsSettingsExpander.UpdateLayout();
        input.Focus();
        input.SelectAll();
        SettingsStatus.BringIntoView();
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(SettingsStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        return false;
    }

    private static double Progress(long count, long target) => Math.Clamp((double)count / target, 0, 1);

    private static string ReadingDuration(long milliseconds) => milliseconds >= MillisecondsPerMinute
        ? $"{milliseconds / MillisecondsPerMinute} 分钟"
        : $"{milliseconds / 1000} 秒";

    private sealed class WeekDayView(ReadingGoalDay day, DateOnly today)
    {
        public string Label => day.Day == today ? "今天" : WeekdayLabels[(int)day.Day.DayOfWeek];
        public string Summary => day.Milliseconds >= MillisecondsPerMinute
            ? $"{day.Milliseconds / MillisecondsPerMinute}分"
            : $"{day.Milliseconds / 1000}秒";
        public double BarHeight => Progress(day.Milliseconds, day.TargetMinutes * MillisecondsPerMinute) * 35;
        public string Description =>
            $"{day.Day.Month} 月 {day.Day.Day} 日，已读 {ReadingDuration(day.Milliseconds)}，目标 {day.TargetMinutes} 分钟，" +
            (day.Milliseconds >= day.TargetMinutes * MillisecondsPerMinute ? "已达标" : "未达标");
    }
}
