using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Media;
using ReactiveUI;
using TReader.Models;
using TReader.Services;

namespace TReader.ViewModels;

/// <summary>
/// 阅读器视图模型
/// </summary>
public class ReaderViewModel : ViewModelBase
{
    private readonly ILibraryService _libraryService;
    private readonly INovelParser _novelParser;
    private readonly ISettingsService _settingsService;

    private ReaderSettings _settings = null!;
    private Novel? _novel;
    private BookInfo? _currentBook;
    private int _currentChapterIndex;
    private int _currentReadLine; // 实际阅读位置（行号）
    private string _displayText = string.Empty;
    private string _originalContent = string.Empty; // 原始文本内容（用于段间距处理）
    private IBrush _foregroundBrush = new SolidColorBrush(Color.Parse(Defaults.ForegroundColor));
    private readonly ObservableCollection<BookmarkInfo> _bookmarks = new();

    public string DisplayText
    {
        get => _displayText;
        set => this.RaiseAndSetIfChanged(ref _displayText, value);
    }

    public double FontSize
    {
        get => _settings?.FontSize ?? 18;
        set
        {
            if (_settings != null && _settings.FontSize != value)
            {
                _settings.FontSize = value;
                this.RaisePropertyChanged();
                SaveSettings();
            }
        }
    }

    public string ForegroundColorHex
    {
        get => _settings?.ForegroundColor ?? Defaults.ForegroundColor;
        set
        {
            if (_settings != null && _settings.ForegroundColor != value)
            {
                _settings.ForegroundColor = value;
                this.RaisePropertyChanged();
                if (Color.TryParse(value, out var color))
                {
                    ForegroundBrush = new SolidColorBrush(color);
                }
                SaveSettings();
            }
        }
    }

    public IBrush ForegroundBrush
    {
        get => _foregroundBrush;
        set => this.RaiseAndSetIfChanged(ref _foregroundBrush, value);
    }

    public int CurrentChapterIndex
    {
        get => _currentChapterIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentChapterIndex, value);
            this.RaisePropertyChanged(nameof(CurrentChapterTitle));
        }
    }

    public string CurrentChapterTitle => _novel?.Chapters.ElementAtOrDefault(_currentChapterIndex)?.Title ?? "";

    public int TotalChapters => _novel?.Chapters.Count ?? 0;
    public List<Chapter> Chapters => _novel?.Chapters ?? new List<Chapter>();

    public int CurrentLine => _currentReadLine;

    public ObservableCollection<BookmarkInfo> Bookmarks => _bookmarks;

    public ReactiveCommand<string, Unit> ChangeFontSizeCommand { get; }
    public ReactiveCommand<string, Unit> ChangeColorCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenTableOfContentsCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousChapterCommand { get; }
    public ReactiveCommand<Unit, Unit> NextChapterCommand { get; }
    public ReactiveCommand<Unit, Unit> BackToShelfCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleBookmarkCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenBookmarksCommand { get; }

    public event Action? TableOfContentsRequested;
    public event Action? BackToShelfRequested;
    public event Action<int>? ScrollToLineRequested;
    public event Action? BookmarksRequested;

    public ReaderViewModel() : this(new LibraryService(), new NovelParser(), new SettingsService())
    {
    }

    public ReaderViewModel(ILibraryService libraryService, INovelParser novelParser, ISettingsService settingsService)
    {
        _libraryService = libraryService;
        _novelParser = novelParser;
        _settingsService = settingsService;

        ChangeFontSizeCommand = ReactiveCommand.Create<string>(sizeStr =>
        {
            if (double.TryParse(sizeStr, out var size))
            {
                FontSize = size;
            }
        });
        ChangeColorCommand = ReactiveCommand.Create<string>(color => ForegroundColorHex = color);
        OpenTableOfContentsCommand = ReactiveCommand.Create(() => TableOfContentsRequested?.Invoke());
        PreviousChapterCommand = ReactiveCommand.Create(GoPreviousChapter);
        NextChapterCommand = ReactiveCommand.Create(GoNextChapter);
        BackToShelfCommand = ReactiveCommand.Create(() => BackToShelfRequested?.Invoke());
        ToggleBookmarkCommand = ReactiveCommand.Create(ToggleBookmark);
        OpenBookmarksCommand = ReactiveCommand.Create(OpenBookmarks);

        LoadSettings();
    }

    /// <summary>
    /// 重新从服务加载设置并刷新 UI
    /// </summary>
    public void ReloadSettings()
    {
        LoadSettings();
    }

    public async Task LoadBookAsync(BookInfo book)
    {
        _currentBook = book;
        var filePath = _libraryService.GetBookFilePath(book);
        _novel = await _novelParser.ParseAsync(filePath);
        _originalContent = _novel.Content; // 保存原始内容

        // 加载书签
        _bookmarks.Clear();
        foreach (var bookmark in _libraryService.GetBookmarks(book.Id))
        {
            _bookmarks.Add(bookmark);
        }
        this.RaisePropertyChanged(nameof(Bookmarks));
        UpdateDisplayText(); // 根据段间距设置生成显示文本

        // 初始化当前阅读位置（统一使用原始文本的 0-based 行号）
        var savedLine = Math.Clamp(book.LastReadLine, 0, Math.Max(0, _novel.TotalLines - 1));
        _currentReadLine = savedLine;
        _currentChapterIndex = FindChapterIndexByLine(savedLine);

        this.RaisePropertyChanged(nameof(TotalChapters));
        this.RaisePropertyChanged(nameof(Chapters));
        this.RaisePropertyChanged(nameof(CurrentChapterTitle));

        if (savedLine > 0)
        {
            ScrollToLineRequested?.Invoke(savedLine);
        }
    }

    private int FindChapterIndexByLine(int lineNumber)
    {
        if (_novel == null || _novel.Chapters.Count == 0) return 0;
        for (int i = _novel.Chapters.Count - 1; i >= 0; i--)
        {
            if (lineNumber >= _novel.Chapters[i].StartLine) return i;
        }
        return 0;
    }

    public void GoToChapter(int chapterIndex)
    {
        if (_novel == null || chapterIndex < 0 || chapterIndex >= _novel.Chapters.Count) return;
        CurrentChapterIndex = chapterIndex;
        var chapter = _novel.Chapters[chapterIndex];
        ScrollToLineRequested?.Invoke(chapter.StartLine);
        SaveProgress();
    }

    /// <summary>
    /// 跳转到指定原始行号。
    /// </summary>
    public void GoToLine(int lineNumber)
    {
        if (_novel == null) return;

        var targetLine = Math.Clamp(lineNumber, 0, Math.Max(0, _novel.TotalLines - 1));
        _currentReadLine = targetLine;
        CurrentChapterIndex = FindChapterIndexByLine(targetLine);
        ScrollToLineRequested?.Invoke(targetLine);
        SaveProgress();
    }

    /// <summary>
    /// 打开书签列表。
    /// </summary>
    public void OpenBookmarks()
    {
        BookmarksRequested?.Invoke();
    }

    /// <summary>
    /// 添加或移除当前阅读位置的书签。
    /// </summary>
    public void ToggleBookmark()
    {
        if (_currentBook == null || _novel == null) return;

        var existing = _bookmarks.FirstOrDefault(b => b.LineNumber == _currentReadLine);
        if (existing != null)
        {
            DeleteBookmark(existing.Id);
            return;
        }

        var preview = _novel.Lines.ElementAtOrDefault(_currentReadLine) ?? string.Empty;
        var bookmark = new BookmarkInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = BuildBookmarkTitle(preview),
            ChapterTitle = CurrentChapterTitle,
            LineNumber = _currentReadLine,
            CreatedAt = DateTime.Now
        };

        _libraryService.AddBookmark(_currentBook.Id, bookmark);
        _bookmarks.Insert(0, bookmark);
        this.RaisePropertyChanged(nameof(Bookmarks));
    }

    /// <summary>
    /// 删除指定书签。
    /// </summary>
    public void DeleteBookmark(string bookmarkId)
    {
        if (_currentBook == null) return;

        _libraryService.RemoveBookmark(_currentBook.Id, bookmarkId);
        var bookmark = _bookmarks.FirstOrDefault(b => b.Id == bookmarkId);
        if (bookmark != null)
        {
            _bookmarks.Remove(bookmark);
        }

        this.RaisePropertyChanged(nameof(Bookmarks));
    }

    private string BuildBookmarkTitle(string preview)
    {
        var text = preview.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.IsNullOrWhiteSpace(CurrentChapterTitle)
                ? $"第 {_currentReadLine + 1} 行"
                : CurrentChapterTitle;
        }

        return text.Length <= 24 ? text : text[..24] + "…";
    }

    public void GoPreviousChapter()
    {
        if (_currentChapterIndex > 0) GoToChapter(_currentChapterIndex - 1);
    }

    public void GoNextChapter()
    {
        if (_novel != null && _currentChapterIndex < _novel.Chapters.Count - 1) GoToChapter(_currentChapterIndex + 1);
    }

    public void UpdateCurrentChapterByScrollRatio(double scrollRatio)
    {
        if (_novel == null || _novel.Chapters.Count == 0) return;
        int estimatedLine = (int)(scrollRatio * _novel.TotalLines);
        int newChapterIndex = FindChapterIndexByLine(estimatedLine);
        if (newChapterIndex != _currentChapterIndex) CurrentChapterIndex = newChapterIndex;
    }

    public void SaveProgress()
    {
        if (_currentBook == null || _novel == null) return;
        _libraryService.UpdateReadingProgress(_currentBook.Id, _currentReadLine);
    }

    /// <summary>
    /// 请求滚动到当前阅读位置（例如窗口缩放结束后恢复顶部行）。
    /// </summary>
    public void RequestScrollToCurrentLine()
    {
        if (_novel == null) return;
        ScrollToLineRequested?.Invoke(_currentReadLine);
    }

    /// <summary>
    /// 根据显示文本的滚动位置更新原始阅读进度并保存
    /// </summary>
    public void UpdateCurrentLine(int displayLineNumber)
    {
        var originalLine = DisplayLineToOriginalLine(displayLineNumber);
        _currentReadLine = originalLine;

        // 同时更新章节索引
        int newChapterIndex = FindChapterIndexByLine(originalLine);
        if (newChapterIndex != _currentChapterIndex)
        {
            CurrentChapterIndex = newChapterIndex;
        }

        // 保存进度
        SaveProgress();
    }

    /// <summary>
    /// 将原始文本行号转换为显示文本行号。
    /// 段间距会在每行后插入空行，因此需要按插入数量换算。
    /// </summary>
    public int GetDisplayLineNumber(int originalLineNumber)
    {
        if (_novel == null) return Math.Max(0, originalLineNumber);

        var originalLine = Math.Clamp(originalLineNumber, 0, Math.Max(0, _novel.TotalLines - 1));
        var emptyLineCount = GetInsertedEmptyLineCount();
        return emptyLineCount == 0 ? originalLine : originalLine * (emptyLineCount + 1);
    }

    private int DisplayLineToOriginalLine(int displayLineNumber)
    {
        if (_novel == null) return Math.Max(0, displayLineNumber);

        var emptyLineCount = GetInsertedEmptyLineCount();
        var originalLine = emptyLineCount == 0
            ? displayLineNumber
            : displayLineNumber / (emptyLineCount + 1);

        return Math.Clamp(originalLine, 0, Math.Max(0, _novel.TotalLines - 1));
    }

    private int GetInsertedEmptyLineCount()
    {
        var paragraphSpacing = _settings?.ParagraphSpacing ?? 0;
        if (paragraphSpacing < 0.5) return 0;

        var emptyLineCount = (int)paragraphSpacing;
        return Math.Max(1, emptyLineCount);
    }

    public double LineHeight
    {
        get => _settings?.LineHeight ?? 1.5;
        set
        {
            if (_settings != null && _settings.LineHeight != value)
            {
                _settings.LineHeight = value;
                this.RaisePropertyChanged();
                SaveSettings();
            }
        }
    }

    public string? FontFamily
    {
        get
        {
            var fontFamily = _settings?.FontFamily;
            return string.IsNullOrWhiteSpace(fontFamily) ? Defaults.FontFamily : fontFamily;
        }
        set
        {
            if (_settings != null && _settings.FontFamily != value)
            {
                _settings.FontFamily = value;
                this.RaisePropertyChanged();
                SaveSettings();
            }
        }
    }

    public double ParagraphSpacing
    {
        get => _settings?.ParagraphSpacing ?? 10;
        set
        {
            if (_settings != null && _settings.ParagraphSpacing != value)
            {
                _settings.ParagraphSpacing = value;
                this.RaisePropertyChanged();
                UpdateDisplayText(); // 段间距改变时重新生成显示文本
                SaveSettings();
            }
        }
    }

    public IBrush BackgroundBrush
    {
        get
        {
            if (_settings != null && Color.TryParse(_settings.BackgroundColor, out var color))
            {
                return new SolidColorBrush(color);
            }
            return new SolidColorBrush(Color.Parse(Defaults.BackgroundColor));
        }
    }

    public string BackgroundColor
    {
        get => _settings?.BackgroundColor ?? Defaults.BackgroundColor;
        set
        {
            if (_settings != null && _settings.BackgroundColor != value)
            {
                _settings.BackgroundColor = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(BackgroundBrush));
                SaveSettings();
            }
        }
    }

    private void LoadSettings()
    {
        _settings = _settingsService.Load();
        // 初始化 ForegroundBrush
        if (Color.TryParse(_settings.ForegroundColor, out var color))
        {
            _foregroundBrush = new SolidColorBrush(color);
        }
        else
        {
            _foregroundBrush = new SolidColorBrush(Color.Parse(Defaults.ForegroundColor));
        }
        this.RaisePropertyChanged(nameof(ForegroundBrush));
        this.RaisePropertyChanged(nameof(FontSize));
        this.RaisePropertyChanged(nameof(LineHeight));
        this.RaisePropertyChanged(nameof(BackgroundColor));
        this.RaisePropertyChanged(nameof(BackgroundBrush));
        this.RaisePropertyChanged(nameof(FontFamily));
    }

    /// <summary>
    /// 根据段间距设置更新显示文本
    /// 段间距通过在原始文本每行后添加空行来实现
    /// </summary>
    private void UpdateDisplayText()
    {
        if (string.IsNullOrEmpty(_originalContent))
        {
            DisplayText = string.Empty;
            return;
        }

        var paragraphSpacing = _settings?.ParagraphSpacing ?? 0;

        // 段间距为0或接近0时，直接使用原始内容
        if (paragraphSpacing < 0.5)
        {
            DisplayText = _originalContent;
            return;
        }

        // 计算需要插入的空行数量（段间距值向下取整作为空行数）
        int emptyLineCount = (int)paragraphSpacing;
        if (emptyLineCount < 1) emptyLineCount = 1;

        // 分割原始内容并在每行后添加空行
        var lines = _originalContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var emptyLines = new string('\n', emptyLineCount);

        // 使用 StringBuilder 高效拼接
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            // 不在最后一行后添加空行
            if (i < lines.Length - 1)
            {
                sb.Append('\n');
                sb.Append(emptyLines);
            }
        }

        DisplayText = sb.ToString();
    }

    private void SaveSettings()
    {
        if (_settings != null)
        {
            _settingsService.Save(_settings);
        }
    }
}
