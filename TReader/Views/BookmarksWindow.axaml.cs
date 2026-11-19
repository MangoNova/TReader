using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TReader.ViewModels;

namespace TReader.Views;

public partial class BookmarksWindow : Window
{
    public BookmarksWindow()
    {
        InitializeComponent();
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnListBoxDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is BookmarksViewModel vm)
        {
            vm.GoToBookmark();
        }
    }
}
