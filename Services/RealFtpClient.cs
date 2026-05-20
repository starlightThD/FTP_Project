using FTP_Project.Models;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace FTP_Project.Services;

/// <summary>
/// 真实 FTP 客户端 - 使用 Socket 实现基本 FTP 协议
/// </summary>
public sealed class RealFtpClient : IFtpService
{
    private FtpControlConnection? _controlConnection;
    private FtpConfig? _config;

    public bool IsConnected => _controlConnection?.IsConnected == true;

    public async Task ConnectAsync(FtpConfig config, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            throw new InvalidOperationException("Already connected");

        try
        {
            _config = config;
            _controlConnection = new FtpControlConnection();

            // 连接到服务器
            await _controlConnection.ConnectAsync(config.Host, config.Port, cancellationToken);

            // 读取欢迎响应
            var welcomeResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
            Console.WriteLine($"[服务器响应 {welcomeResponse.Code}] {welcomeResponse.Message}");

            if (!welcomeResponse.IsSuccess)
                throw new FtpConnectionException($"Server rejected connection: {welcomeResponse.Message}");

            // 发送 USER 命令
            await _controlConnection.SendCommandAsync($"USER {config.Username}", cancellationToken);
            var userResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
            Console.WriteLine($"[USER 响应 {userResponse.Code}] {userResponse.Message}");

            if (userResponse.Code != 331 && userResponse.Code != 230)
                throw new FtpConnectionException($"USER failed: {userResponse.Message}");

            // 如果需要密码则发送 PASS（331 表示需要密码）
            if (userResponse.Code == 331)
            {
                await _controlConnection.SendCommandAsync($"PASS {config.Password}", cancellationToken);
                var passResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
                Console.WriteLine($"[PASS 响应 {passResponse.Code}] {passResponse.Message}");

                if (!passResponse.IsSuccess)
                    throw new FtpConnectionException($"PASS failed: {passResponse.Message}");
            }

            Console.WriteLine("✓ 登录成功");
        }
        catch (Exception)
        {
            await DisconnectAsync(cancellationToken);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_controlConnection != null)
        {
            try
            {
                await _controlConnection.DisconnectAsync(cancellationToken);
            }
            catch
            {
                // 忽略断开时的错误
            }

            await _controlConnection.DisposeAsync();
            _controlConnection = null;
        }
    }

    public async Task<IReadOnlyList<string>> ListDirectoryAsync(string path = "/", CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected");

        if (_controlConnection == null)
            throw new InvalidOperationException("Control connection is null");

        try
        {
            using var dataClient = await OpenPassiveDataClientAsync(cancellationToken);

            var listCommand = string.IsNullOrWhiteSpace(path) || path == "/" ? "LIST" : $"LIST {path}";
            await _controlConnection.SendCommandAsync(listCommand, cancellationToken);
            var listStartResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
            Console.WriteLine($"[LIST 开始响应 {listStartResponse.Code}] {listStartResponse.Message}");

            if (listStartResponse.Code != 125 && listStartResponse.Code != 150)
                throw new FtpConnectionException($"LIST failed: {listStartResponse.Message}");

            var dataLines = await ReadAllLinesFromDataConnectionAsync(dataClient, cancellationToken);

            var listDoneResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
            Console.WriteLine($"[LIST 完成响应 {listDoneResponse.Code}] {listDoneResponse.Message}");
            if (!listDoneResponse.IsSuccess)
                throw new FtpConnectionException($"LIST did not complete: {listDoneResponse.Message}");

            return dataLines;
        }
        catch (Exception ex)
        {
            throw new FtpConnectionException($"Failed to list directory: {ex.Message}", ex);
        }
    }

    public async Task UploadFileAsync(
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected");
        if (_controlConnection == null)
            throw new InvalidOperationException("Control connection is null");
        if (!File.Exists(localPath))
            throw new FileNotFoundException($"Local file not found: {localPath}", localPath);

        try
        {
            await SetBinaryTransferModeAsync(cancellationToken);
            var localFileInfo = new FileInfo(localPath);
            var localFileSize = localFileInfo.Length;
            var remoteFileSize = await TryGetRemoteFileSizeAsync(remotePath, cancellationToken);

            if (remoteFileSize.HasValue && remoteFileSize.Value > localFileSize)
                throw new FtpConnectionException($"Remote file is larger than local file, cannot resume upload: {remotePath}");

            var offset = remoteFileSize.GetValueOrDefault(0);
            if (offset == localFileSize)
            {
                progress?.Report(new TransferProgress
                {
                    Operation = "upload",
                    SourcePath = localPath,
                    TargetPath = remotePath,
                    BytesTransferred = localFileSize,
                    TotalBytes = localFileSize
                });
                return;
            }

            if (offset > 0)
            {
                await SendRestAsync(offset, cancellationToken);
                Console.WriteLine($"[续传] 上传偏移 {offset} 字节");
            }

            using var dataClient = await OpenPassiveDataClientAsync(cancellationToken);

            await _controlConnection.SendCommandAsync($"STOR {remotePath}", cancellationToken);
            var storStartResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
            Console.WriteLine($"[STOR 开始响应 {storStartResponse.Code}] {storStartResponse.Message}");
            if (storStartResponse.Code != 125 && storStartResponse.Code != 150)
                throw new FtpConnectionException($"STOR failed: {storStartResponse.Message}");

            using (var localFileStream = File.OpenRead(localPath))
            using (var dataStream = dataClient.GetStream())
            {
                localFileStream.Seek(offset, SeekOrigin.Begin);
                await CopyStreamWithProgressAsync(
                    source: localFileStream,
                    destination: dataStream,
                    operation: "upload",
                    sourcePath: localPath,
                    targetPath: remotePath,
                    initialTransferred: offset,
                    totalBytes: localFileSize,
                    progress: progress,
                    cancellationToken: cancellationToken);
                await dataStream.FlushAsync(cancellationToken);
            }

            var storDoneResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
            Console.WriteLine($"[STOR 完成响应 {storDoneResponse.Code}] {storDoneResponse.Message}");
            if (!storDoneResponse.IsSuccess)
                throw new FtpConnectionException($"STOR did not complete: {storDoneResponse.Message}");
        }
        catch (Exception ex) when (ex is not FtpConnectionException and not OperationCanceledException)
        {
            throw new FtpConnectionException($"Failed to upload file: {ex.Message}", ex);
        }
    }

    public async Task DownloadFileAsync(
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected");
        if (_controlConnection == null)
            throw new InvalidOperationException("Control connection is null");

        try
        {
            await SetBinaryTransferModeAsync(cancellationToken);
            var remoteFileSize = await TryGetRemoteFileSizeAsync(remotePath, cancellationToken);
            var localDirectory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(localDirectory))
            {
                Directory.CreateDirectory(localDirectory);
            }

            var placeholderPath = GetPlaceholderPath(localPath);
            EnsurePlaceholderFileExists(placeholderPath);

            var localOffset = File.Exists(placeholderPath) ? new FileInfo(placeholderPath).Length : 0;

            if (remoteFileSize.HasValue && localOffset > remoteFileSize.Value)
                throw new FtpConnectionException($"Local placeholder is larger than remote file, cannot resume download: {placeholderPath}");

            if (remoteFileSize.HasValue && localOffset == remoteFileSize.Value)
            {
                FinalizePlaceholderFile(placeholderPath, localPath);
                progress?.Report(new TransferProgress
                {
                    Operation = "download",
                    SourcePath = remotePath,
                    TargetPath = localPath,
                    BytesTransferred = localOffset,
                    TotalBytes = remoteFileSize.Value
                });
                return;
            }

            if (localOffset > 0)
            {
                await SendRestAsync(localOffset, cancellationToken);
                Console.WriteLine($"[续传] 下载偏移 {localOffset} 字节");
            }

            using var dataClient = await OpenPassiveDataClientAsync(cancellationToken);

            await _controlConnection.SendCommandAsync($"RETR {remotePath}", cancellationToken);
            var retrStartResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
            Console.WriteLine($"[RETR 开始响应 {retrStartResponse.Code}] {retrStartResponse.Message}");
            if (retrStartResponse.Code != 125 && retrStartResponse.Code != 150)
                throw new FtpConnectionException($"RETR failed: {retrStartResponse.Message}");

            using (var dataStream = dataClient.GetStream())
            using (var localFileStream = localOffset > 0
                ? new FileStream(placeholderPath, FileMode.Append, FileAccess.Write, FileShare.None)
                : new FileStream(placeholderPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await CopyStreamWithProgressAsync(
                    source: dataStream,
                    destination: localFileStream,
                    operation: "download",
                    sourcePath: remotePath,
                    targetPath: localPath,
                    initialTransferred: localOffset,
                    totalBytes: remoteFileSize,
                    progress: progress,
                    cancellationToken: cancellationToken);
                await localFileStream.FlushAsync(cancellationToken);
            }

            var retrDoneResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
            Console.WriteLine($"[RETR 完成响应 {retrDoneResponse.Code}] {retrDoneResponse.Message}");
            if (!retrDoneResponse.IsSuccess)
                throw new FtpConnectionException($"RETR did not complete: {retrDoneResponse.Message}");

            FinalizePlaceholderFile(placeholderPath, localPath);
        }
        catch (Exception ex) when (ex is not FtpConnectionException and not OperationCanceledException)
        {
            throw new FtpConnectionException($"Failed to download file: {ex.Message}", ex);
        }
    }

    private static IPEndPoint ParsePasvEndpoint(string pasvMessage)
    {
        var match = Regex.Match(pasvMessage, @"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)");
        if (!match.Success)
            throw new FtpConnectionException($"Invalid PASV response: {pasvMessage}");

        var ip = string.Join(".",
            match.Groups[1].Value,
            match.Groups[2].Value,
            match.Groups[3].Value,
            match.Groups[4].Value);

        var p1 = int.Parse(match.Groups[5].Value);
        var p2 = int.Parse(match.Groups[6].Value);
        var port = p1 * 256 + p2;

        return new IPEndPoint(IPAddress.Parse(ip), port);
    }

    private async Task SetBinaryTransferModeAsync(CancellationToken cancellationToken)
    {
        if (_controlConnection == null)
            throw new InvalidOperationException("Control connection is null");

        await _controlConnection.SendCommandAsync("TYPE I", cancellationToken);
        var typeResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
        Console.WriteLine($"[TYPE 响应 {typeResponse.Code}] {typeResponse.Message}");
        if (!typeResponse.IsSuccess)
            throw new FtpConnectionException($"TYPE I failed: {typeResponse.Message}");
    }

    private async Task<long?> TryGetRemoteFileSizeAsync(string remotePath, CancellationToken cancellationToken)
    {
        if (_controlConnection == null)
            throw new InvalidOperationException("Control connection is null");

        await _controlConnection.SendCommandAsync($"SIZE {remotePath}", cancellationToken);
        var sizeResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
        Console.WriteLine($"[SIZE 响应 {sizeResponse.Code}] {sizeResponse.Message}");

        if (sizeResponse.Code == 213)
        {
            var parts = sizeResponse.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out var size))
                return size;
        }

        if (sizeResponse.Code == 550 || sizeResponse.Code == 500 || sizeResponse.Code == 502)
            return null;

        throw new FtpConnectionException($"SIZE failed: {sizeResponse.Message}");
    }

    private async Task SendRestAsync(long offset, CancellationToken cancellationToken)
    {
        if (_controlConnection == null)
            throw new InvalidOperationException("Control connection is null");

        await _controlConnection.SendCommandAsync($"REST {offset}", cancellationToken);
        var restResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
        Console.WriteLine($"[REST 响应 {restResponse.Code}] {restResponse.Message}");
        if (restResponse.Code != 350)
            throw new FtpConnectionException($"REST failed: {restResponse.Message}");
    }

    private async Task<TcpClient> OpenPassiveDataClientAsync(CancellationToken cancellationToken)
    {
        if (_controlConnection == null)
            throw new InvalidOperationException("Control connection is null");

        await _controlConnection.SendCommandAsync("PASV", cancellationToken);
        var pasvResponse = await _controlConnection.ReadResponseAsync(cancellationToken);
        Console.WriteLine($"[PASV 响应 {pasvResponse.Code}] {pasvResponse.Message}");

        if (pasvResponse.Code != 227)
            throw new FtpConnectionException($"PASV failed: {pasvResponse.Message}");

        var dataEndpoint = ParsePasvEndpoint(pasvResponse.Message);

        var dataClient = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            await dataClient.ConnectAsync(dataEndpoint.Address, dataEndpoint.Port, connectCts.Token);
            Console.WriteLine($"[数据连接] 已连接到 {dataEndpoint.Address}:{dataEndpoint.Port}");
            return dataClient;
        }
        catch
        {
            dataClient.Dispose();
            throw;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadAllLinesFromDataConnectionAsync(
        TcpClient dataClient,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();

        using var dataStream = dataClient.GetStream();
        using var reader = new StreamReader(dataStream, Encoding.ASCII);

        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(TimeSpan.FromSeconds(20));

            var line = await reader.ReadLineAsync(readCts.Token);
            if (line == null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static async Task CopyStreamWithProgressAsync(
        Stream source,
        Stream destination,
        string operation,
        string sourcePath,
        string targetPath,
        long initialTransferred,
        long? totalBytes,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var transferred = initialTransferred;

        progress?.Report(new TransferProgress
        {
            Operation = operation,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            BytesTransferred = transferred,
            TotalBytes = totalBytes
        });

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            transferred += read;

            progress?.Report(new TransferProgress
            {
                Operation = operation,
                SourcePath = sourcePath,
                TargetPath = targetPath,
                BytesTransferred = transferred,
                TotalBytes = totalBytes
            });
        }
    }

    private static string GetPlaceholderPath(string localPath)
    {
        return $"{localPath}.part";
    }

    private static void EnsurePlaceholderFileExists(string placeholderPath)
    {
        if (File.Exists(placeholderPath))
            return;

        using var _ = File.Create(placeholderPath);
    }

    private static void FinalizePlaceholderFile(string placeholderPath, string finalPath)
    {
        if (!File.Exists(placeholderPath))
            throw new FtpConnectionException($"Placeholder file missing: {placeholderPath}");

        File.Move(placeholderPath, finalPath, overwrite: true);
    }
}
