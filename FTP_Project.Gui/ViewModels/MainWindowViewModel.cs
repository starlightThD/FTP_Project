using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FTP_Project.Gui.Models;
using FTP_Project.Models;
using FTP_Project.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace FTP_Project.Gui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IFtpService _browseClient = new RealFtpClient();
    private readonly Dictionary<string, CancellationTokenSource> _taskTokens = [];
    private readonly string _connectionCachePath;

    [ObservableProperty]
    private string host = "127.0.0.1";

    [ObservableProperty]
    private int port = 21;

    [ObservableProperty]
    private string username = "anonymous";

    [ObservableProperty]
    private string password = string.Empty;

    [ObservableProperty]
    private string remotePath = "/";

    [ObservableProperty]
    private string uploadLocalPath = string.Empty;

    [ObservableProperty]
    private string uploadRemotePath = string.Empty;

    [ObservableProperty]
    private string downloadRemotePath = string.Empty;

    [ObservableProperty]
    private string downloadLocalPath = string.Empty;

    [ObservableProperty]
    private string statusMessage = "Ready";

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private ConnectionProfile? selectedConnection;

    public ObservableCollection<ConnectionProfile> SavedConnections { get; } = [];
    public ObservableCollection<string> RemoteFiles { get; } = [];
    public ObservableCollection<TransferTaskItem> Tasks { get; } = [];

    public MainWindowViewModel()
    {
        var cacheDirectory = Path.Combine(Environment.CurrentDirectory, "cache");
        Directory.CreateDirectory(cacheDirectory);
        _connectionCachePath = Path.Combine(cacheDirectory, "gui_connections.json");
        LoadConnectionsFromCache();
    }

    [RelayCommand]
    private void LoadSelectedConnection()
    {
        if (SelectedConnection == null)
            return;

        Host = SelectedConnection.Host;
        Port = SelectedConnection.Port;
        Username = SelectedConnection.Username;
        Password = SelectedConnection.Password;
        StatusMessage = $"Loaded profile: {SelectedConnection.Name}";
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected)
            return;

        try
        {
            await _browseClient.ConnectAsync(BuildConfig());
            IsConnected = true;
            StatusMessage = "Connected";
            SaveCurrentConnection();
            await RefreshRemoteFilesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connect failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (!IsConnected)
            return;

        await _browseClient.DisconnectAsync();
        IsConnected = false;
        StatusMessage = "Disconnected";
    }

    [RelayCommand]
    private async Task RefreshRemoteFilesAsync()
    {
        if (!IsConnected)
            return;

        try
        {
            var list = await _browseClient.ListDirectoryAsync(string.IsNullOrWhiteSpace(RemotePath) ? "/" : RemotePath);
            RemoteFiles.Clear();
            foreach (var line in list)
            {
                RemoteFiles.Add(ParseListName(line));
            }
            StatusMessage = $"Remote files loaded: {RemoteFiles.Count}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"List failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StartUploadAsync()
    {
        if (string.IsNullOrWhiteSpace(UploadLocalPath))
        {
            StatusMessage = "Upload local path required";
            return;
        }

        var remote = string.IsNullOrWhiteSpace(UploadRemotePath) ? Path.GetFileName(UploadLocalPath) : UploadRemotePath;
        await StartTaskAsync("Upload", UploadLocalPath, remote, (client, progress, token) =>
            client.UploadFileAsync(UploadLocalPath, remote, progress, token));
    }

    [RelayCommand]
    private async Task StartDownloadAsync()
    {
        if (string.IsNullOrWhiteSpace(DownloadRemotePath))
        {
            StatusMessage = "Download remote path required";
            return;
        }

        var local = string.IsNullOrWhiteSpace(DownloadLocalPath) ? Path.GetFileName(DownloadRemotePath) : DownloadLocalPath;
        await StartTaskAsync("Download", DownloadRemotePath, local, (client, progress, token) =>
            client.DownloadFileAsync(DownloadRemotePath, local, progress, token));
    }

    [RelayCommand]
    private void PauseTask(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return;

        if (_taskTokens.TryGetValue(taskId, out var cts))
        {
            cts.Cancel();
            StatusMessage = $"Task {taskId} paused";
        }
    }

    [RelayCommand]
    private async Task ContinueTaskAsync(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return;

        var existing = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (existing == null)
            return;

        await RestartTaskAsync(existing);
    }

    private async Task StartTaskAsync(
        string type,
        string source,
        string target,
        Func<RealFtpClient, IProgress<TransferProgress>, CancellationToken, Task> worker,
        TransferTaskItem? reuseTask = null,
        bool skipConflictCheck = false)
    {
        if (!skipConflictCheck)
        {
            var conflict = FindActiveTaskBySavedName(target);
            if (conflict != null)
            {
                var isSameSourceTarget =
                    string.Equals(conflict.Source, source, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(conflict.Target, target, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(conflict.Type, type, StringComparison.OrdinalIgnoreCase);

                if (isSameSourceTarget)
                {
                    var allowRestart = await ShowConfirmDialogAsync(
                        "任务已存在",
                        $"同一任务已存在（{conflict.Id}）。是否重启该任务？");
                    if (allowRestart)
                    {
                        await RestartTaskAsync(conflict);
                    }
                    else
                    {
                        StatusMessage = "已取消创建重复任务";
                    }

                    return;
                }

                StatusMessage = $"保存名冲突：已存在任务 {conflict.Id} 使用同名目标，请先暂停或完成它";
                return;
            }
        }

        var taskItem = reuseTask ?? new TransferTaskItem
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Type = type,
            Source = source,
            Target = target,
            Status = "Running",
            ProgressPercent = 0
        };
        if (reuseTask == null)
        {
            Tasks.Insert(0, taskItem);
        }
        else
        {
            taskItem.Type = type;
            taskItem.Source = source;
            taskItem.Target = target;
            taskItem.Status = "Running";
            taskItem.ProgressPercent = 0;
            taskItem.ProgressText = "0%";
        }

        var cts = new CancellationTokenSource();
        _taskTokens[taskItem.Id] = cts;
        var config = BuildConfig();

        var progress = new Progress<TransferProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0)
                {
                    taskItem.ProgressPercent = Math.Min(100, p.BytesTransferred * 100.0 / p.TotalBytes.Value);
                    taskItem.ProgressText = $"{taskItem.ProgressPercent:F1}% ({FormatBytes(p.BytesTransferred)}/{FormatBytes(p.TotalBytes.Value)})";
                }
                else
                {
                    taskItem.ProgressText = $"{FormatBytes(p.BytesTransferred)}";
                }
            });
        });

        _ = Task.Run(async () =>
        {
            await using var client = new RealFtpClientScope();
            try
            {
                await client.Client.ConnectAsync(config, cts.Token);
                await worker(client.Client, progress, cts.Token);

                Dispatcher.UIThread.Post(() =>
                {
                    taskItem.Status = "Completed";
                    taskItem.ProgressPercent = 100;
                    if (!taskItem.ProgressText.StartsWith("100", StringComparison.Ordinal))
                    {
                        taskItem.ProgressText = $"100% ({taskItem.ProgressText})";
                    }
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    taskItem.Status = "Paused";
                    taskItem.ProgressText = taskItem.ProgressText.Contains("100", StringComparison.Ordinal) ? "Paused" : taskItem.ProgressText;
                });
            }
            catch (FtpConnectionException ex) when (ex.InnerException is OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    taskItem.Status = "Paused";
                    taskItem.ProgressText = taskItem.ProgressText.Contains("100", StringComparison.Ordinal) ? "Paused" : taskItem.ProgressText;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    taskItem.Status = "Failed";
                    taskItem.ProgressText = ex.Message;
                });
            }
            finally
            {
                _taskTokens.Remove(taskItem.Id);
                cts.Dispose();
            }
        });

        StatusMessage = $"Task started: {taskItem.Id}";
    }

    private async Task RestartTaskAsync(TransferTaskItem task)
    {
        if (_taskTokens.TryGetValue(task.Id, out var existingCts))
        {
            existingCts.Cancel();
        }

        task.Status = "Restarted";
        await StartTaskAsync(task.Type, task.Source, task.Target, (client, progress, token) =>
            string.Equals(task.Type, "Upload", StringComparison.OrdinalIgnoreCase)
                ? client.UploadFileAsync(task.Source, task.Target, progress, token)
                : client.DownloadFileAsync(task.Source, task.Target, progress, token),
            reuseTask: task,
            skipConflictCheck: true);
    }

    private TransferTaskItem? FindActiveTaskBySavedName(string targetPath)
    {
        var fileName = Path.GetFileName(targetPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = targetPath;

        return Tasks.FirstOrDefault(t =>
            IsTaskActive(t.Status) &&
            string.Equals(Path.GetFileName(t.Target), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTaskActive(string status)
    {
        return status.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("Paused", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ShowConfirmDialogAsync(string title, string message)
    {
        if (Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow == null)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window();
        dialog.Width = 420;
        dialog.Height = 180;
        dialog.Title = title;
        dialog.CanResize = false;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        new Button
                        {
                            Content = "放弃",
                            MinWidth = 80,
                            Command = new RelayCommand(() =>
                            {
                                tcs.TrySetResult(false);
                                dialog.Close();
                            })
                        },
                        new Button
                        {
                            Content = "重启任务",
                            MinWidth = 100,
                            Command = new RelayCommand(() =>
                            {
                                tcs.TrySetResult(true);
                                dialog.Close();
                            })
                        }
                    }
                }
            }
        };

        _ = dialog.ShowDialog(desktop.MainWindow);
        return await tcs.Task;
    }

    private void SaveCurrentConnection()
    {
        var name = $"{Username}@{Host}:{Port}";
        var existing = SavedConnections.FirstOrDefault(x => x.Name == name);
        if (existing == null)
        {
            SavedConnections.Insert(0, new ConnectionProfile
            {
                Name = name,
                Host = Host,
                Port = Port,
                Username = Username,
                Password = Password
            });
        }
        else
        {
            existing.Host = Host;
            existing.Port = Port;
            existing.Username = Username;
            existing.Password = Password;
        }

        while (SavedConnections.Count > 25)
        {
            SavedConnections.RemoveAt(SavedConnections.Count - 1);
        }

        SaveConnectionsToCache();
    }

    private void LoadConnectionsFromCache()
    {
        try
        {
            if (!File.Exists(_connectionCachePath))
                return;

            var json = File.ReadAllText(_connectionCachePath);
            var list = JsonSerializer.Deserialize<List<ConnectionProfile>>(json);
            if (list == null)
                return;

            SavedConnections.Clear();
            foreach (var item in list.Take(25))
            {
                SavedConnections.Add(item);
            }
        }
        catch
        {
            StatusMessage = "Failed to read connection cache";
        }
    }

    private void SaveConnectionsToCache()
    {
        try
        {
            var json = JsonSerializer.Serialize(SavedConnections.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_connectionCachePath, json);
        }
        catch
        {
            StatusMessage = "Failed to save connection cache";
        }
    }

    private FtpConfig BuildConfig()
    {
        return new FtpConfig
        {
            Host = Host.Trim(),
            Port = Port,
            Username = Username.Trim(),
            Password = Password,
            UsePassiveMode = true
        };
    }

    private static string ParseListName(string listLine)
    {
        if (string.IsNullOrWhiteSpace(listLine))
            return string.Empty;

        var parts = listLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 9 ? string.Join(" ", parts.Skip(8)) : listLine;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return index == 0 ? $"{value:F0}{units[index]}" : $"{value:F2}{units[index]}";
    }

    private sealed class RealFtpClientScope : IAsyncDisposable
    {
        public RealFtpClient Client { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await Client.DisconnectAsync();
        }
    }
}
