using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using TReader.Models;

namespace TReader.ViewModels;

/// <summary>
/// 书签列表视图模型
/// </summary>
public class BookmarksViewModel : ViewModelBase
{
    private string _searchText = string.Empty;
    private BookmarkInfo? _selectedBookmark;
    private ObservableCollection<BookmarkInfo> _filteredBookmarks = new();

    public List<BookmarkInfo> AllBookmarks { get; private set; } = new();

    public ObservableCollection<BookmarkInfo> FilteredBookmarks
    {
        get => _filteredBookmarks;
        set => this.RaiseAndSetIfChanged(ref _filteredBookmarks, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            FilterBookmarks();
        }
    }

    public BookmarkInfo? SelectedBookmark
    {
        get => _selectedBookmark;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedBookmark, value);
            this.RaisePropertyChanged(nameof(CanOperate));
        }
    }

    public bool HasBookmarks => AllBookmarks.Count > 0;
    public bool CanOperate => SelectedBookmark != null;

    public ReactiveCommand<Unit, Unit> GoToBookmarkCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteBookmarkCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>
    /// 书签跳转事件，参数为原始文本 0-based 行号
    /// </summary>
    public event Action<int>? BookmarkSelected;

    /// <summary>
    /// 书签删除事件，参数为书签 Id
    /// </summary>
    public event Action<string>? BookmarkDeleted;

    /// <summary>
    /// 关闭事件
    /// </summary>
    public event Action? CloseRequested;

    public BookmarksViewModel()
    {
        var canOperate = this.WhenAnyValue(x => x.SelectedBookmark)
            .Select(bookmark => bookmark != null);

        GoToBookmarkCommand = ReactiveCommand.Create(GoToBookmark, canOperate);
        DeleteBookmarkCommand = ReactiveCommand.Create(DeleteSelectedBookmark, canOperate);
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
    }

    public void Initialize(IEnumerable<BookmarkInfo> bookmarks)
    {
        AllBookmarks = bookmarks.OrderBy(b => b.LineNumber).ToList();
        FilterBookmarks();
        SelectedBookmark = FilteredBookmarks.FirstOrDefault();
        this.RaisePropertyChanged(nameof(HasBookmarks));
    }

    private void FilterBookmarks()
    {
        FilteredBookmarks.Clear();

        var bookmarks = string.IsNullOrWhiteSpace(_searchText)
            ? AllBookmarks
            : AllBookmarks.Where(b =>
                b.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                b.ChapterTitle.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

        foreach (var bookmark in bookmarks)
        {
            FilteredBookmarks.Add(bookmark);
        }
    }

    public void GoToBookmark()
    {
        if (SelectedBookmark != null)
        {
            BookmarkSelected?.Invoke(SelectedBookmark.LineNumber);
        }
    }

    private void DeleteSelectedBookmark()
    {
        if (SelectedBookmark == null) return;

        var bookmarkId = SelectedBookmark.Id;
        BookmarkDeleted?.Invoke(bookmarkId);

        AllBookmarks.RemoveAll(b => b.Id == bookmarkId);
        FilterBookmarks();
        SelectedBookmark = FilteredBookmarks.FirstOrDefault();

        this.RaisePropertyChanged(nameof(HasBookmarks));
    }
}
