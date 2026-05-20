using CommunityToolkit.Mvvm.ComponentModel;

namespace FTP_Project.Gui.Models;

public partial class TransferTaskItem : ObservableObject
{
    [ObservableProperty]
    private string id = string.Empty;

    [ObservableProperty]
    private string type = string.Empty;

    [ObservableProperty]
    private string source = string.Empty;

    [ObservableProperty]
    private string target = string.Empty;

    [ObservableProperty]
    private string status = "Pending";

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private string progressText = "0%";
}
