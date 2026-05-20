using FTP_Project.Models;

namespace FTP_Project.Services;

public sealed class StubFtpService : IFtpService
{
    public bool IsConnected { get; private set; }

    public Task ConnectAsync(FtpConfig config, CancellationToken cancellationToken = default)
    {
        // This is a placeholder implementation. We will replace it with real FTP logic later.
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListDirectoryAsync(string path = "/", CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> items =
        [
            "README.txt",
            "upload/",
            "download/"
        ];

        return Task.FromResult(items);
    }

    public Task UploadFileAsync(
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected");

        progress?.Report(new TransferProgress
        {
            Operation = "upload",
            SourcePath = localPath,
            TargetPath = remotePath,
            BytesTransferred = 1,
            TotalBytes = 1
        });

        return Task.CompletedTask;
    }

    public Task DownloadFileAsync(
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected");

        progress?.Report(new TransferProgress
        {
            Operation = "download",
            SourcePath = remotePath,
            TargetPath = localPath,
            BytesTransferred = 1,
            TotalBytes = 1
        });

        return Task.CompletedTask;
    }
}
