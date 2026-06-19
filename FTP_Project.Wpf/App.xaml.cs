using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FTP_Project.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // 捕获 UI 线程异常
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        
        // 捕获后台线程异常
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        
        // 捕获所有未处理异常
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("UI线程异常", e.Exception);
        MessageBox.Show($"UI错误:\n{e.Exception.Message}\n\n详细信息已写入 error.log", 
            "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // 防止崩溃
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("后台任务异常", e.Exception);
        e.SetObserved(); // 防止崩溃
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException("未处理异常", ex);
            MessageBox.Show($"严重错误:\n{ex.Message}\n\n程序将退出。详细信息已写入 error.log",
                "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void LogException(string context, Exception ex)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}\n" +
                          $"类型: {ex.GetType().FullName}\n" +
                          $"消息: {ex.Message}\n" +
                          $"堆栈: {ex.StackTrace}\n" +
                          $"内部异常: {ex.InnerException?.Message}\n" +
                          $"---\n";
            File.AppendAllText(logPath, logEntry);
        }
        catch { }
    }
}