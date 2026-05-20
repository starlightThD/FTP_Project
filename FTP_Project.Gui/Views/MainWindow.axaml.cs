using Avalonia.Controls;
using Avalonia.Interactivity;
using FTP_Project.Gui.ViewModels;

namespace FTP_Project.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void PauseTask_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (sender is Button { Tag: string taskId })
        {
            vm.PauseTaskCommand.Execute(taskId);
        }
    }

    private void ContinueTask_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (sender is Button { Tag: string taskId })
        {
            vm.ContinueTaskCommand.Execute(taskId);
        }
    }
}
