using FTP_Project.Models;

namespace FTP_Project.Services;

public interface IFtpService
{
    bool IsConnected { get; }
    Task ConnectAsync(FtpConfig config, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListDirectoryAsync(string path = "/", CancellationToken cancellationToken = default);
    Task UploadFileAsync(
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task DownloadFileAsync(
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
