using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    private readonly ObservableCollection<string> _logs = new();
    private CancellationTokenSource? _transferCts;

    public MainWindow()
    {
        InitializeComponent();
        RemoteFileList.ItemsSource = _remoteFiles;
        LogList.ItemsSource = _logs;
    }

    private void Log(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            _logs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
        });
    }

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

            Log("正在连接...");

            var ftp = new RealFtpClient();
            await Task.Run(() => ftp.ConnectAsync(_config));
            
            Log($"已连接 {_config.Host}");
            
            Dispatcher.Invoke(() =>
            {
                DisconnectButton.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                UploadButton.IsEnabled = true;
                DownloadButton.IsEnabled = true;
            });
            
            await RefreshFiles();
        }
        catch (Exception ex)
        {
            Log($"连接失败: {ex.InnerException?.Message ?? ex.Message}");
            ConnectButton.IsEnabled = true;
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        Task.Run(async () =>
        {
            var ftp = new RealFtpClient();
            await ftp.DisconnectAsync();
        });
        
        Log("已断开");
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        UploadButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        _remoteFiles.Clear();
    }

    private async Task RefreshFiles()
    {
        if (_config == null) return;
        
        try
        {
            Log("读取目录...");
            
            var path = CurrentPath.Text;
            
            var ftp = new RealFtpClient();
            IReadOnlyList<string> files = Array.Empty<string>();
            
            await Task.Run(async () =>
            {
                await ftp.ConnectAsync(_config);
                files = await ftp.ListDirectoryAsync(path);
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
            
            Log($"列出 {files.Count} 个文件");
        }
        catch (Exception ex)
        {
            Log($"刷新失败: {ex.Message}");
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) 
        => await RefreshFiles();

    private async void RemoteFileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RemoteFileList.SelectedItem is string item && item.StartsWith("[DIR]"))
        {
            CurrentPath.Text = CurrentPath.Text.TrimEnd('/') + "/" + item.Substring(6);
            await RefreshFiles();
        }
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;

        var dialog = new OpenFileDialog();
        if (dialog.ShowDialog() != true) return;

        _transferCts = new CancellationTokenSource();
        UploadButton.IsEnabled = false;
        var token = _transferCts.Token;
        
        try
        {
            var localPath = dialog.FileName;
            var remotePath = CurrentPath.Text.TrimEnd('/')  + Path.GetFileName(localPath);
            
            Log($"上传: {Path.GetFileName(localPath)}");
            
            await Task.Run(async () =>
            {
                var ftp = new RealFtpClient();
                await ftp.ConnectAsync(_config, token);
                await ftp.UploadFileAsync(localPath, remotePath, null, token);
            }, token);
            
            Log($"上传完成: {Path.GetFileName(localPath)}");
            await RefreshFiles();
        }
        catch (OperationCanceledException)
        {
            Log("上传已取消");
        }
        catch (Exception ex)
        {
            Log($"上传失败: {ex.InnerException?.Message ?? ex.Message}");
        }
        finally
        {
            UploadButton.IsEnabled = true;
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;

        if (RemoteFileList.SelectedItem is not string item || item.StartsWith("[DIR]"))
        {
            MessageBox.Show("请选择文件");
            return;
        }

        // 关键修复：去掉前导空格和 DIR 标记
        var remoteFileName = item.Trim();
        
        var dialog = new SaveFileDialog
        {
            FileName = remoteFileName,
            Title = "保存文件"
        };
        
        if (dialog.ShowDialog() != true) return;
        
        var localPath = dialog.FileName;
        try { File.Delete(localPath + ".part"); } catch { }
        
        _transferCts = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        var token = _transferCts.Token;
        
        try
        {
            var currentPath = CurrentPath.Text.TrimEnd('/');
            var remotePath = currentPath +  remoteFileName;
            
            Log("下载: " + remoteFileName);
            Log("远程: " + remotePath);
            
            await Task.Run(async () =>
            {
                var ftp = new RealFtpClient();
                await ftp.ConnectAsync(_config, token);
                await ftp.DownloadFileAsync(remotePath, localPath, null, token);
            }, token);
            
            if (File.Exists(localPath))
            {
                var size = new FileInfo(localPath).Length;
                Log("下载完成: " + FormatSize(size));
            }
            else
            {
                Log("下载失败: 文件未创建");
            }
        }
        catch (Exception ex)
        {
            Log("下载失败: " + (ex.InnerException?.Message ?? ex.Message));
        }
        finally
        {
            DownloadButton.IsEnabled = true;
        }
    }

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