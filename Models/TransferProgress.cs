namespace FTP_Project.Models;

public sealed class TransferProgress
{
    public required string Operation { get; init; }
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public long BytesTransferred { get; init; }
    public long? TotalBytes { get; init; }
}
