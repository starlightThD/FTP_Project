namespace FTP_Project.Models;

public sealed class FtpConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;
    public string Username { get; set; } = "anonymous";
    public string Password { get; set; } = string.Empty;
    public bool UsePassiveMode { get; set; } = true;
}
