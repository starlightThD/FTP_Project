using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FTP_Project.Models;
using FTP_Project.Services;
using Microsoft.Win32;

namespace FTP_Project.Wpf;

public partial class MainWindow : Window
{
    private FtpConfig? _config;
    private readonly ObservableCollection<string> _remoteFiles = new();
    private readonly ObservableCollection<TransferTask> _tasks = new();

    public MainWindow()
    {
        InitializeComponent();
        RemoteFileList.ItemsSource = _remoteFiles;
        TransferList.ItemsSource = _tasks;
    }

    // ===== 连接 =====
    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            _config = new FtpConfig
            {
                Host = HostInput.Text,
                Port = int.TryParse(PortInput.Text, out var p) ? p : 21,
                Username = UsernameInput.Text,
                Password = PasswordInput.Password
            };

            StatusText.Text = "正在连接...";
            var ftp = new RealFtpClient();
            await Task.Run(() => ftp.ConnectAsync(_config));

            DisconnectButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            UploadButton.IsEnabled = true;
            DownloadButton.IsEnabled = true;
            StatusText.Text = "已连接 " + _config.Host;

            await RefreshFiles();
        }
        catch (Exception ex)
        {
            StatusText.Text = "连接失败: " + (ex.InnerException?.Message ?? ex.Message);
            ConnectButton.IsEnabled = true;
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        Task.Run(async () => { var f = new RealFtpClient(); await f.DisconnectAsync(); });
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        UploadButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        _remoteFiles.Clear();
        StatusText.Text = "已断开";
    }

    // ===== 文件浏览 =====
    private async Task RefreshFiles()
    {
        if (_config == null) return;
        try
        {
            var path = CurrentPath.Text;
            var ftp = new RealFtpClient();
            var files = await Task.Run(async () =>
            {
                await ftp.ConnectAsync(_config);
                return await ftp.ListDirectoryAsync(path);
            });

            await Dispatcher.InvokeAsync(() =>
            {
                _remoteFiles.Clear();
                foreach (var f in files)
                {
                    var parts = f.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var name = parts.Length > 0 ? parts[^1] : f;
                    _remoteFiles.Add((f.StartsWith('d') ? "[DIR] " : "      ") + name);
                }
            });
            StatusText.Text = $"{files.Count} 个项目";
        }
        catch (Exception ex)
        {
            StatusText.Text = "刷新失败: " + ex.Message;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshFiles();

    private async void RemoteFileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RemoteFileList.SelectedItem is string item && item.StartsWith("[DIR]"))
        {
            CurrentPath.Text = CurrentPath.Text.TrimEnd('/') + "/" + item.Substring(6);
            await RefreshFiles();
        }
    }

    // ===== 上传 =====
    private void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        var dialog = new OpenFileDialog();
        if (dialog.ShowDialog() != true) return;

        var task = new TransferTask
        {
            Id = Guid.NewGuid().ToString(),
            FileName = Path.GetFileName(dialog.FileName),
            LocalPath = dialog.FileName,
            RemotePath = CurrentPath.Text.TrimEnd('/') + Path.GetFileName(dialog.FileName),
            Direction = "upload",
            DirectionIcon = "⬆"
        };
        _tasks.Insert(0, task);
        _ = RunTransfer(task);
    }

    // ===== 下载 =====
    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        if (RemoteFileList.SelectedItem is not string item || item.StartsWith("[DIR]"))
        {
            MessageBox.Show("请选择文件");
            return;
        }

        var remoteFileName = item.Trim();
        var dialog = new SaveFileDialog { FileName = remoteFileName };
        if (dialog.ShowDialog() != true) return;

        try { File.Delete(dialog.FileName + ".part"); } catch { }

        var task = new TransferTask
        {
            Id = Guid.NewGuid().ToString(),
            FileName = remoteFileName,
            LocalPath = dialog.FileName,
            RemotePath = CurrentPath.Text.TrimEnd('/') + remoteFileName,
            Direction = "download",
            DirectionIcon = "⬇"
        };
        _tasks.Insert(0, task);
        _ = RunTransfer(task);
    }

    // ===== 传输核心 =====
    private async Task RunTransfer(TransferTask task)
    {
        var cts = new CancellationTokenSource();
        task.Cts = cts;
        var isUpload = task.Direction == "upload";

        try
        {
            task.StatusText = "连接中...";
            task.ProgressPercent = 0;

            await Task.Run(async () =>
            {
                var ftp = new RealFtpClient();
                await ftp.ConnectAsync(_config!, cts.Token);

                var progress = new Progress<TransferProgress>(p =>
                {
                    var percent = p.TotalBytes > 0
                        ? (double)p.BytesTransferred / p.TotalBytes.Value * 100
                        : 0;
                    Dispatcher.Invoke(() =>
                    {
                        task.ProgressPercent = percent;
                        task.StatusText = p.TotalBytes > 0
                            ? $"{FormatSize(p.BytesTransferred)} / {FormatSize(p.TotalBytes.Value)}"
                            : FormatSize(p.BytesTransferred);
                    });
                });

                if (isUpload)
                    await ftp.UploadFileAsync(task.LocalPath, task.RemotePath, progress, cts.Token);
                else
                    await ftp.DownloadFileAsync(task.RemotePath, task.LocalPath, progress, cts.Token);
            }, cts.Token);

            task.StatusText = "✅ 完成";
            task.ProgressPercent = 100;
            StatusText.Text = (isUpload ? "上传" : "下载") + "完成: " + task.FileName;
            await RefreshFiles();
        }
        catch (OperationCanceledException)
        {
            task.StatusText = "⏸ 已暂停";
        }
        catch (Exception ex)
        {
            task.StatusText = "❌ " + (ex.InnerException?.Message ?? ex.Message);
        }
    }

    // ===== 任务控制 =====
    private void PauseTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            if (task == null) return;

            if (task.StatusText == "⏸ 已暂停")
            {
                task.StatusText = "继续中...";
                _ = RunTransfer(task);
            }
            else
            {
                task.Cts?.Cancel();
            }
        }
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            if (task == null) return;
            task.Cts?.Cancel();
            _tasks.Remove(task);
        }
    }

    // ===== 工具 =====
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
        return $"{len:0.##} {sizes[order]}";
    }
}

// ===== 任务模型 =====
public class TransferTask : INotifyPropertyChanged
{
    private string _statusText = "";
    private double _progressPercent;

    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public string Direction { get; set; } = "";
    public string DirectionIcon { get; set; } = "";

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set { _progressPercent = value; OnPropertyChanged(); }
    }

    public CancellationTokenSource? Cts { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}