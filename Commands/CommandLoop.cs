using FTP_Project.Models;
using FTP_Project.Services;
using System.Text;

namespace FTP_Project.Commands;

public sealed class CommandLoop
{
    private const int MaxHistoryCount = 25;
    private static readonly string[] BasicCommands =
    [
        "help", "connect", "ls", "upload", "download", "disconnect", "exit", "quit"
    ];

    private readonly IFtpService _ftpService;
    private readonly List<string> _commandHistory = [];
    private readonly string _historyFilePath;
    private int _historyIndex = -1;
    private string _historyDraft = string.Empty;

    public CommandLoop(IFtpService ftpService)
    {
        _ftpService = ftpService;
        var cacheDirectory = Path.Combine(Environment.CurrentDirectory, "cache");
        _historyFilePath = Path.Combine(cacheDirectory, "command_history.txt");
        LoadHistory();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        PrintHelp();

        while (!cancellationToken.IsCancellationRequested)
        {
            var input = ReadCommandLine("ftp> ")?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                continue;
            }

            AddHistory(input);
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0].ToLowerInvariant();

            try
            {
                switch (command)
                {
                    case "help":
                        PrintHelp();
                        break;
                    case "connect":
                        await ConnectAsync(parts, cancellationToken);
                        break;
                    case "ls":
                        await ListAsync(parts, cancellationToken);
                        break;
                    case "disconnect":
                        await DisconnectAsync(cancellationToken);
                        break;
                    case "upload":
                        await UploadAsync(parts, cancellationToken);
                        break;
                    case "download":
                        await DownloadAsync(parts, cancellationToken);
                        break;
                    case "exit":
                    case "quit":
                        if (_ftpService.IsConnected)
                        {
                            await DisconnectAsync(cancellationToken);
                        }
                        return;
                    default:
                        Console.WriteLine("未知命令，输入 help 查看可用命令。");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ 错误: {ex.Message}");
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("\n可用命令:");
        Console.WriteLine("  help                          显示帮助");
        Console.WriteLine("  connect <host> [port]         连接到 FTP 服务器");
        Console.WriteLine("  ls [path]                     列出目录（默认 /）");
        Console.WriteLine("  upload <local> [remote]       上传文件");
        Console.WriteLine("  download <remote> [local]     下载文件");
        Console.WriteLine("  disconnect                    断开连接");
        Console.WriteLine("  exit                          退出程序");
        Console.WriteLine("\n示例:");
        Console.WriteLine("  connect 192.168.1.105 21");
        Console.WriteLine("  ls /");
        Console.WriteLine("  upload ./a.txt");
        Console.WriteLine("  download /pub/readme.txt");
        Console.WriteLine();
    }

    private async Task ConnectAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (_ftpService.IsConnected)
        {
            Console.WriteLine("当前已连接。");
            return;
        }

        if (parts.Length < 2)
        {
            Console.WriteLine("用法: connect <host> [port]");
            return;
        }

        var host = parts[1];
        var port = parts.Length > 2 ? int.Parse(parts[2]) : 21;

        Console.Write("Username: ");
        var username = Console.ReadLine() ?? "anonymous";

        Console.Write("Password: ");
        var password = ReadPasswordFromConsole();

        var config = new FtpConfig
        {
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            UsePassiveMode = true
        };

        Console.WriteLine("正在连接...");
        await _ftpService.ConnectAsync(config, cancellationToken);
        Console.WriteLine("✓ 连接并登录成功。");
    }

    private async Task ListAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (!_ftpService.IsConnected)
        {
            Console.WriteLine("请先使用 connect 连接。");
            return;
        }

        var path = parts.Length > 1 ? parts[1] : "/";
        var items = await _ftpService.ListDirectoryAsync(path, cancellationToken);
        Console.WriteLine($"\n目录 {path}:");
        foreach (var item in items)
        {
            Console.WriteLine($"  {item}");
        }
        Console.WriteLine();
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!_ftpService.IsConnected)
        {
            Console.WriteLine("当前未连接。");
            return;
        }

        await _ftpService.DisconnectAsync(cancellationToken);
        Console.WriteLine("✓ 已断开连接。");
    }

    private async Task UploadAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (!_ftpService.IsConnected)
        {
            Console.WriteLine("请先使用 connect 连接。");
            return;
        }

        if (parts.Length < 2)
        {
            Console.WriteLine("用法: upload <local> [remote]");
            return;
        }

        var localPath = parts[1];
        var remotePath = parts.Length > 2 ? parts[2] : Path.GetFileName(localPath);
        var startedAt = DateTime.UtcNow;
        var progress = CreateProgressReporter(startedAt);

        Console.WriteLine($"正在上传: {localPath} -> {remotePath}");
        await _ftpService.UploadFileAsync(localPath, remotePath, progress, cancellationToken);
        Console.WriteLine();
        Console.WriteLine("✓ 上传成功。");
    }

    private async Task DownloadAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (!_ftpService.IsConnected)
        {
            Console.WriteLine("请先使用 connect 连接。");
            return;
        }

        if (parts.Length < 2)
        {
            Console.WriteLine("用法: download <remote> [local]");
            return;
        }

        var remotePath = parts[1];
        var localPath = parts.Length > 2 ? parts[2] : Path.GetFileName(remotePath);
        var startedAt = DateTime.UtcNow;
        var progress = CreateProgressReporter(startedAt);

        Console.WriteLine($"正在下载: {remotePath} -> {localPath}");
        await _ftpService.DownloadFileAsync(remotePath, localPath, progress, cancellationToken);
        Console.WriteLine();
        Console.WriteLine("✓ 下载成功。");
    }

    private static IProgress<TransferProgress> CreateProgressReporter(DateTime startedAt)
    {
        return new Progress<TransferProgress>(p =>
        {
            var elapsedSeconds = Math.Max((DateTime.UtcNow - startedAt).TotalSeconds, 0.001);
            var speedBytesPerSecond = p.BytesTransferred / elapsedSeconds;
            var speedText = $"{FormatBytes((long)speedBytesPerSecond)}/s";
            var transferredText = FormatBytes(p.BytesTransferred);

            string line;
            if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0)
            {
                var percent = (double)p.BytesTransferred / p.TotalBytes.Value * 100.0;
                line = $"进度: {percent,6:F2}%  {transferredText}/{FormatBytes(p.TotalBytes.Value)}  {speedText}";
            }
            else
            {
                line = $"进度: {transferredText}  {speedText}";
            }

            var width = Math.Max(Console.WindowWidth - 1, 20);
            if (line.Length > width)
            {
                line = line.Substring(0, width);
            }

            Console.Write($"\r{line.PadRight(width)}");
        });
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

    private static string ReadPasswordFromConsole()
    {
        var password = string.Empty;
        ConsoleKeyInfo key;

        do
        {
            key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password = password.Substring(0, password.Length - 1);
                }
            }
            else if (key.Key != ConsoleKey.Enter)
            {
                password += key.KeyChar;
            }
        } while (key.Key != ConsoleKey.Enter);

        Console.WriteLine();
        return password;
    }

    private string? ReadCommandLine(string prompt)
    {
        var buffer = new StringBuilder();
        var cursorIndex = 0;
        _historyIndex = -1;
        _historyDraft = string.Empty;

        Console.Write(prompt);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();
                case ConsoleKey.Backspace:
                    if (cursorIndex > 0)
                    {
                        buffer.Remove(cursorIndex - 1, 1);
                        cursorIndex--;
                        RenderInputLine(prompt, buffer, cursorIndex);
                    }
                    break;
                case ConsoleKey.Delete:
                    if (cursorIndex < buffer.Length)
                    {
                        buffer.Remove(cursorIndex, 1);
                        RenderInputLine(prompt, buffer, cursorIndex);
                    }
                    break;
                case ConsoleKey.LeftArrow:
                    if (cursorIndex > 0)
                    {
                        cursorIndex--;
                        RenderInputLine(prompt, buffer, cursorIndex);
                    }
                    break;
                case ConsoleKey.RightArrow:
                    if (cursorIndex < buffer.Length)
                    {
                        cursorIndex++;
                        RenderInputLine(prompt, buffer, cursorIndex);
                    }
                    break;
                case ConsoleKey.UpArrow:
                    ApplyHistoryNavigation(goUp: true, buffer, ref cursorIndex, prompt);
                    break;
                case ConsoleKey.DownArrow:
                    ApplyHistoryNavigation(goUp: false, buffer, ref cursorIndex, prompt);
                    break;
                case ConsoleKey.Tab:
                    ApplyTabCompletion(buffer, ref cursorIndex, prompt);
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(cursorIndex, key.KeyChar);
                        cursorIndex++;
                        RenderInputLine(prompt, buffer, cursorIndex);
                    }
                    break;
            }
        }
    }

    private void ApplyHistoryNavigation(bool goUp, StringBuilder buffer, ref int cursorIndex, string prompt)
    {
        if (_commandHistory.Count == 0)
            return;

        if (goUp)
        {
            if (_historyIndex == -1)
            {
                _historyDraft = buffer.ToString();
                _historyIndex = _commandHistory.Count - 1;
            }
            else if (_historyIndex > 0)
            {
                _historyIndex--;
            }
        }
        else
        {
            if (_historyIndex == -1)
                return;

            if (_historyIndex < _commandHistory.Count - 1)
            {
                _historyIndex++;
            }
            else
            {
                _historyIndex = -1;
                ReplaceBuffer(buffer, _historyDraft);
                cursorIndex = buffer.Length;
                RenderInputLine(prompt, buffer, cursorIndex);
                return;
            }
        }

        ReplaceBuffer(buffer, _commandHistory[_historyIndex]);
        cursorIndex = buffer.Length;
        RenderInputLine(prompt, buffer, cursorIndex);
    }

    private static void ApplyTabCompletion(StringBuilder buffer, ref int cursorIndex, string prompt)
    {
        var currentInput = buffer.ToString();
        var firstSpace = currentInput.IndexOf(' ');
        var commandToken = firstSpace == -1 ? currentInput : currentInput[..firstSpace];

        if (firstSpace != -1 && cursorIndex > firstSpace)
            return;

        var matches = BasicCommands
            .Where(cmd => cmd.StartsWith(commandToken, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
            return;

        if (matches.Length == 1)
        {
            var completed = matches[0] + " ";
            ReplaceBuffer(buffer, completed);
            cursorIndex = buffer.Length;
            RenderInputLine(prompt, buffer, cursorIndex);
            return;
        }

        var prefix = GetLongestCommonPrefix(matches);
        if (prefix.Length > commandToken.Length)
        {
            ReplaceBuffer(buffer, prefix);
            cursorIndex = buffer.Length;
            RenderInputLine(prompt, buffer, cursorIndex);
            return;
        }

        Console.WriteLine();
        Console.WriteLine(string.Join("  ", matches));
        RenderInputLine(prompt, buffer, cursorIndex);
    }

    private static string GetLongestCommonPrefix(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return string.Empty;

        var prefix = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            var candidate = values[i];
            var length = 0;
            while (length < prefix.Length &&
                   length < candidate.Length &&
                   char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(candidate[length]))
            {
                length++;
            }
            prefix = prefix[..length];
            if (prefix.Length == 0)
                break;
        }

        return prefix;
    }

    private static void ReplaceBuffer(StringBuilder buffer, string value)
    {
        buffer.Clear();
        buffer.Append(value);
    }

    private static void RenderInputLine(string prompt, StringBuilder buffer, int cursorIndex)
    {
        var text = buffer.ToString();
        var output = $"{prompt}{text}";
        var width = Math.Max(Console.WindowWidth - 1, output.Length + 1);

        Console.Write('\r');
        Console.Write(output.PadRight(width));
        Console.SetCursorPosition(prompt.Length + cursorIndex, Console.CursorTop);
    }

    private void AddHistory(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        if (_commandHistory.Count > 0 &&
            string.Equals(_commandHistory[^1], command, StringComparison.Ordinal))
        {
            return;
        }

        _commandHistory.Add(command);
        if (_commandHistory.Count > MaxHistoryCount)
        {
            _commandHistory.RemoveAt(0);
        }

        SaveHistory();
    }

    private void LoadHistory()
    {
        try
        {
            var directory = Path.GetDirectoryName(_historyFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(_historyFilePath))
                return;

            var lines = File.ReadAllLines(_historyFilePath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(MaxHistoryCount);

            _commandHistory.Clear();
            _commandHistory.AddRange(lines);
        }
        catch
        {
            // 缓存读取失败不影响主流程
        }
    }

    private void SaveHistory()
    {
        try
        {
            var directory = Path.GetDirectoryName(_historyFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(_historyFilePath, _commandHistory.TakeLast(MaxHistoryCount));
        }
        catch
        {
            // 缓存写入失败不影响主流程
        }
    }
}
