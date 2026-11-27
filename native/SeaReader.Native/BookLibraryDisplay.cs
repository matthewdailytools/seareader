using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace SeaReader.Native;

public sealed class BookLibraryDisplay : INotifyPropertyChanged
{
    private readonly string fallbackTitle;

    internal BookLibraryDisplay(string fallbackTitle)
    {
        this.fallbackTitle = fallbackTitle;
        Title = fallbackTitle;
    }

    public string Title { get; private set; }
    public string Author { get; private set; } = "";
    public BitmapSource? Cover { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool Apply(LibraryBookMetadata metadata)
    {
        string title = metadata.Title.Length == 0 ? fallbackTitle : metadata.Title;
        bool titleChanged = Title != title;
        bool authorChanged = Author != metadata.Author;
        Title = title;
        Author = metadata.Author;
        if (titleChanged) PropertyChanged?.Invoke(this, new(nameof(Title)));
        if (authorChanged) PropertyChanged?.Invoke(this, new(nameof(Author)));
        return titleChanged || authorChanged;
    }

    internal void SetCover(BitmapSource? cover)
    {
        if (ReferenceEquals(Cover, cover)) return;
        Cover = cover;
        PropertyChanged?.Invoke(this, new(nameof(Cover)));
    }
}
