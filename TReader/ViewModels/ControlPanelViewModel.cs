using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ReactiveUI;
using TReader.Models;
using TReader.Services;

namespace TReader.ViewModels;

/// <summary>
/// 控制面板（书架）视图模型
/// </summary>
public class ControlPanelViewModel : ViewModelBase
{
    private readonly ILibraryService _libraryService;
    private readonly INovelParser _novelParser;
    private readonly ISettingsService _settingsService;
    private BookInfo? _selectedBook;
    private bool _isLoading;
    private bool _isSettingsMode;
    private bool _isTutorialMode;

    // 编辑中的设置（临时副本）
    private double _editFontSize;
    private decimal _editLineHeight;
    private decimal _editParagraphSpacing;
    private Color _editForegroundColor;
    private Color _editBackgroundColor;
    private string? _editFontFamily;
    private bool _isBlackThemeSelected;
    private bool _isWhiteThemeSelected;

    private static readonly Color BlackThemeForeground = Color.Parse(Defaults.BlackThemeForegroundColor);
    private static readonly Color BlackThemeBackground = Color.Parse(Defaults.BlackThemeBackgroundColor);
    private static readonly Color WhiteThemeForeground = Color.Parse(Defaults.WhiteThemeForegroundColor);
    private static readonly Color WhiteThemeBackground = Color.Parse(Defaults.WhiteThemeBackgroundColor);

    // 推荐阅读字体，优先显示在字体下拉框顶部
    private static readonly string[] RecommendedFonts =
    {
        Defaults.FontFamily,
        "Microsoft YaHei",
        "DengXian",
        "SimSun",
        "KaiTi",
        "Source Han Serif SC",
        "Noto Serif CJK SC"
    };

    public ObservableCollection<BookInfo> Books { get; } = new();
    public ObservableCollection<string> AvailableFonts { get; } = new();

    public BookInfo? SelectedBook
    {
        get => _selectedBook;
        set => this.RaiseAndSetIfChanged(ref _selectedBook, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    // 界面模式切换
    public bool IsSettingsMode
    {
        get => _isSettingsMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSettingsMode, value);
            RefreshNavigationState();

            // 进入设置页面时加载配置
            if (value)
            {
                LoadEditingSettings();
            }
        }
    }

    public bool IsTutorialMode
    {
        get => _isTutorialMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isTutorialMode, value);
            RefreshNavigationState();
        }
    }

    public IBrush ShelfNavBackground =>
        !IsSettingsMode && !IsTutorialMode ? Brush.Parse("#16A34A") : Brushes.Transparent;

    public IBrush SettingsNavBackground =>
        IsSettingsMode ? Brush.Parse("#16A34A") : Brushes.Transparent;

    public IBrush TutorialNavBackground =>
        IsTutorialMode ? Brush.Parse("#16A34A") : Brushes.Transparent;

    public IBrush ShelfNavForeground =>
        !IsSettingsMode && !IsTutorialMode ? Brushes.White : Brush.Parse("#64748B");

    public IBrush SettingsNavForeground =>
        IsSettingsMode ? Brushes.White : Brush.Parse("#64748B");

    public IBrush TutorialNavForeground =>
        IsTutorialMode ? Brushes.White : Brush.Parse("#64748B");

    public bool HasBooks => Books.Count > 0;

    // 编辑中的设置属性（绑定到 UI）
    public double FontSize
    {
        get => _editFontSize;
        set => this.RaiseAndSetIfChanged(ref _editFontSize, value);
    }

    public decimal LineHeight
    {
        get => _editLineHeight;
        set => this.RaiseAndSetIfChanged(ref _editLineHeight, value);
    }

    public decimal ParagraphSpacing
    {
        get => _editParagraphSpacing;
        set => this.RaiseAndSetIfChanged(ref _editParagraphSpacing, value);
    }

    public Color ForegroundColor
    {
        get => _editForegroundColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _editForegroundColor, value);
            RefreshThemeSelection();
        }
    }

    public Color BackgroundColor
    {
        get => _editBackgroundColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _editBackgroundColor, value);
            RefreshThemeSelection();
        }
    }

    public bool IsBlackThemeSelected
    {
        get => _isBlackThemeSelected;
        private set
        {
            if (_isBlackThemeSelected == value) return;
            _isBlackThemeSelected = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(BlackThemeCardBorder));
        }
    }

    public bool IsWhiteThemeSelected
    {
        get => _isWhiteThemeSelected;
        private set
        {
            if (_isWhiteThemeSelected == value) return;
            _isWhiteThemeSelected = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(WhiteThemeCardBorder));
        }
    }

    public IBrush BlackPreviewForeground => new SolidColorBrush(BlackThemeForeground);
    public IBrush BlackPreviewBackground => new SolidColorBrush(BlackThemeBackground);
    public IBrush WhitePreviewForeground => new SolidColorBrush(WhiteThemeForeground);
    public IBrush WhitePreviewBackground => new SolidColorBrush(WhiteThemeBackground);

    public IBrush BlackThemeCardBorder =>
        IsBlackThemeSelected ? Brush.Parse("#27AE60") : Brush.Parse("#3A3A3A");

    public IBrush WhiteThemeCardBorder =>
        IsWhiteThemeSelected ? Brush.Parse("#27AE60") : Brush.Parse("#555555");

    public string? FontFamily
    {
        get => _editFontFamily;
        set => this.RaiseAndSetIfChanged(ref _editFontFamily, value);
    }

    public ReactiveCommand<Unit, Unit> ImportBookCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenBookCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteBookCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchToShelfCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchToSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchToTutorialCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetToDefaultCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyBlackThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyWhiteThemeCommand { get; }

    public event Action<BookInfo>? BookOpened;
    public event Action? SettingsSaved; // 通知设置已保存
    public Func<Task<IStorageFile?>>? FilePickerCallback { get; set; }
    public Func<string, string, Task<bool>>? ConfirmDialogCallback { get; set; } // 确认对话框回调

    public ControlPanelViewModel() : this(new LibraryService(), new NovelParser(), new SettingsService())
    {
    }

    public ControlPanelViewModel(ILibraryService libraryService, INovelParser novelParser, ISettingsService settingsService)
    {
        _libraryService = libraryService;
        _novelParser = novelParser;
        _settingsService = settingsService;

        // 初始化编辑属性
        LoadEditingSettings();

        var canOpenOrDelete = this.WhenAnyValue(x => x.SelectedBook)
            .Select(book => book != null);

        ImportBookCommand = ReactiveCommand.CreateFromTask(ImportBookAsync);
        OpenBookCommand = ReactiveCommand.Create(OpenBook, canOpenOrDelete);
        DeleteBookCommand = ReactiveCommand.Create(DeleteBook, canOpenOrDelete);

        SwitchToShelfCommand = ReactiveCommand.Create(() => { IsSettingsMode = false; IsTutorialMode = false; });
        SwitchToSettingsCommand = ReactiveCommand.Create(() => { IsSettingsMode = true; IsTutorialMode = false; LoadEditingSettings(); });
        SwitchToTutorialCommand = ReactiveCommand.Create(() => { IsSettingsMode = false; IsTutorialMode = true; });

        SaveSettingsCommand = ReactiveCommand.Create(SaveSettings);
        CancelSettingsCommand = ReactiveCommand.Create(CancelSettings);
        ResetToDefaultCommand = ReactiveCommand.CreateFromTask(ResetToDefaultAsync);
        ApplyBlackThemeCommand = ReactiveCommand.Create(() => ApplyTheme(BlackThemeForeground, BlackThemeBackground));
        ApplyWhiteThemeCommand = ReactiveCommand.Create(() => ApplyTheme(WhiteThemeForeground, WhiteThemeBackground));

        LoadBooks();
    }

    private void LoadEditingSettings()
    {
        var settings = _settingsService.Load();
        _editFontSize = settings.FontSize;
        _editLineHeight = (decimal)settings.LineHeight;
        _editParagraphSpacing = (decimal)settings.ParagraphSpacing;

        if (Color.TryParse(settings.ForegroundColor, out var fg))
            _editForegroundColor = fg;
        else
            _editForegroundColor = Color.Parse(Defaults.ForegroundColor);

        if (Color.TryParse(settings.BackgroundColor, out var bg))
            _editBackgroundColor = bg;
        else
            _editBackgroundColor = Color.Parse(Defaults.BackgroundColor);

        // 加载字体列表
        if (AvailableFonts.Count == 0)
        {
            var systemFontNames = FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .ToList();

            foreach (var recommendedFont in RecommendedFonts)
            {
                if (systemFontNames.Contains(recommendedFont, StringComparer.OrdinalIgnoreCase))
                {
                    AvailableFonts.Add(recommendedFont);
                }
            }

            foreach (var name in systemFontNames.OrderBy(n => n))
            {
                if (!AvailableFonts.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    AvailableFonts.Add(name);
                }
            }
        }

        _editFontFamily = ResolveFontFamily(settings.FontFamily);

        // 通知 UI 刷新
        this.RaisePropertyChanged(nameof(FontSize));
        this.RaisePropertyChanged(nameof(LineHeight));
        this.RaisePropertyChanged(nameof(ParagraphSpacing));
        this.RaisePropertyChanged(nameof(ForegroundColor));
        this.RaisePropertyChanged(nameof(BackgroundColor));
        this.RaisePropertyChanged(nameof(FontFamily));
        RefreshThemeSelection();
    }

    private string ResolveFontFamily(string? configuredFont)
    {
        if (!string.IsNullOrWhiteSpace(configuredFont) &&
            AvailableFonts.Contains(configuredFont, StringComparer.OrdinalIgnoreCase))
        {
            return configuredFont;
        }

        if (AvailableFonts.Contains(Defaults.FontFamily, StringComparer.OrdinalIgnoreCase))
        {
            return Defaults.FontFamily;
        }

        return AvailableFonts.FirstOrDefault() ?? Defaults.FontFamily;
    }

    private void ApplyTheme(Color foreground, Color background)
    {
        _editForegroundColor = foreground;
        _editBackgroundColor = background;
        this.RaisePropertyChanged(nameof(ForegroundColor));
        this.RaisePropertyChanged(nameof(BackgroundColor));
        RefreshThemeSelection();
    }

    private void RefreshThemeSelection()
    {
        IsBlackThemeSelected = _editForegroundColor.Equals(BlackThemeForeground) &&
                               _editBackgroundColor.Equals(BlackThemeBackground);
        IsWhiteThemeSelected = _editForegroundColor.Equals(WhiteThemeForeground) &&
                               _editBackgroundColor.Equals(WhiteThemeBackground);
    }

    private void RefreshNavigationState()
    {
        this.RaisePropertyChanged(nameof(ShelfNavBackground));
        this.RaisePropertyChanged(nameof(SettingsNavBackground));
        this.RaisePropertyChanged(nameof(TutorialNavBackground));
        this.RaisePropertyChanged(nameof(ShelfNavForeground));
        this.RaisePropertyChanged(nameof(SettingsNavForeground));
        this.RaisePropertyChanged(nameof(TutorialNavForeground));
    }

    private void SaveSettings()
    {
        // 先加载现有设置，保留窗口大小等未在此处编辑的属性
        var settings = _settingsService.Load();
        settings.FontSize = _editFontSize;
        settings.LineHeight = (double)_editLineHeight;
        settings.ParagraphSpacing = (double)_editParagraphSpacing;
        settings.ForegroundColor = _editForegroundColor.ToString();
        settings.BackgroundColor = _editBackgroundColor.ToString();
        settings.FontFamily = _editFontFamily;
        _settingsService.Save(settings);

        // 通知其他组件设置已更新
        SettingsSaved?.Invoke();

        // 返回书架
        IsSettingsMode = false;
    }

    private void CancelSettings()
    {
        // 重新加载，丢弃更改
        LoadEditingSettings();
        // 返回书架
        IsSettingsMode = false;
    }

    private async Task ResetToDefaultAsync()
    {
        if (ConfirmDialogCallback != null)
        {
            var confirmed = await ConfirmDialogCallback("确认", "确定要将用户配置恢复为默认值吗？");
            if (!confirmed) return;
        }

        _settingsService.ResetToDefault();
        LoadEditingSettings();
        SettingsSaved?.Invoke();
    }

    private void LoadBooks()
    {
        Books.Clear();
        foreach (var book in _libraryService.GetAllBooks())
        {
            Books.Add(book);
        }

        this.RaisePropertyChanged(nameof(HasBooks));
    }

    private async Task ImportBookAsync()
    {
        if (FilePickerCallback == null) return;

        var file = await FilePickerCallback();
        if (file == null) return;

        IsLoading = true;
        try
        {
            var filePath = file.Path.LocalPath;
            var bookInfo = await _libraryService.ImportBookAsync(filePath);
            Books.Insert(0, bookInfo);
            SelectedBook = bookInfo;
            this.RaisePropertyChanged(nameof(HasBooks));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OpenBook()
    {
        if (SelectedBook != null)
        {
            BookOpened?.Invoke(SelectedBook);
        }
    }

    private void DeleteBook()
    {
        if (SelectedBook != null)
        {
            _libraryService.DeleteBook(SelectedBook.Id);
            Books.Remove(SelectedBook);
            SelectedBook = null;
            this.RaisePropertyChanged(nameof(HasBooks));
        }
    }
}
