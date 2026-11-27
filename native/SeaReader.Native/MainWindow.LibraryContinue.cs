using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SeaReader.Native;

public partial class MainWindow
{
    private sealed class LibraryResumeCard(BookEntry book) : INotifyPropertyChanged
    {
        public BookEntry Book { get; private set; } = book;
        public string Detail { get; private set; } = book.FormatLabel;
        public int? Percent { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void UpdateBook(BookEntry entry)
        {
            if (Book == entry) return;
            Book = entry;
            PropertyChanged?.Invoke(this, new(nameof(Book)));
        }

        internal void UpdatePosition(BookResumePosition position)
        {
            string location = Book.FormatLabel;
            if (Book.Format == "pdf" && int.TryParse(position.Position, NumberStyles.None, CultureInfo.InvariantCulture, out int page) && page > 0)
                location = $"第 {page} 页";
            else if (ParsePosition(position.Position)?.Chapter is int chapter && chapter >= 0)
                location = $"第 {chapter + 1} 章";
            var detail = position.Percent is int value ? $"{value}% · {location}" :
                location == Book.FormatLabel ? location : $"{location} · {Book.FormatLabel}";
            if (Percent != position.Percent)
            {
                Percent = position.Percent;
                PropertyChanged?.Invoke(this, new(nameof(Percent)));
            }
            if (Detail == detail) return;
            Detail = detail;
            PropertyChanged?.Invoke(this, new(nameof(Detail)));
        }
    }

    private LibraryResumeCard[] UpdateContinueCards(IEnumerable<BookEntry> recent)
    {
        var existing = LibraryContinueList.Items.OfType<LibraryResumeCard>().ToArray();
        var reuse = existing.ToDictionary(card => card.Book.Id, StringComparer.Ordinal);
        var cards = recent.Select(entry =>
        {
            if (!reuse.TryGetValue(entry.Id, out var card)) return new LibraryResumeCard(entry);
            card.UpdateBook(entry);
            return card;
        }).ToArray();
        if (existing.SequenceEqual(cards)) return cards;

        var focus = Keyboard.FocusedElement as Button;
        var focusedCard = focus?.DataContext as LibraryResumeCard;
        var actionName = focus != null && LibraryContinueList.IsAncestorOf(focus) ? focus.Name : null;
        LibraryContinueList.ItemsSource = cards;
        if (focusedCard == null || string.IsNullOrEmpty(actionName) || !cards.Contains(focusedCard)) return cards;

        LibraryContinueList.UpdateLayout();
        if (LibraryContinueList.ItemContainerGenerator.ContainerFromItem(focusedCard) is ContentPresenter container &&
            container.ContentTemplate?.FindName(actionName, container) is Button action) action.Focus();
        return cards;
    }

    private async Task LoadLibraryResumeCardsAsync(LibraryResumeCard[] cards, IReadOnlyList<BookEntry> snapshot)
    {
        try
        {
            var positions = await store.GetResumePositionsAsync(cards.Select(card => card.Book.Id).ToArray());
            if (closing || !ReferenceEquals(books, snapshot)) return;
            foreach (var card in cards) card.UpdatePosition(positions.GetValueOrDefault(card.Book.Id));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Trace.TraceWarning($"Unable to read library resume positions: {error.Message}");
        }
    }

    private int? ReadTextResumePercent(ReaderLocation location)
    {
        if (text == null) return null;
        int offset = location.TextPosition is { SourceOffset: >= 0 } exact ? exact.SourceOffset : location.Offset;
        if (epub == null) return ResumePercent(offset, text.Text.Length);
        if (chapterReadingStarts is not { Count: > 1 } starts || location.Chapter is not int index ||
            index < 0 || index >= starts.Count - 1) return null;
        long start = starts[index];
        return ResumePercent(start + Math.Clamp((long)offset, 0, starts[index + 1] - start), starts[^1]);
    }

    private static int? ResumePercent(long offset, long total) =>
        total > 0 ? (int)(Math.Clamp(offset, 0, total) * 100.0 / total) : null;
}
