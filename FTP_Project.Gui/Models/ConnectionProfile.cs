namespace FTP_Project.Gui.Models;

public sealed class ConnectionProfile
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;
    public string Username { get; set; } = "anonymous";
    public string Password { get; set; } = string.Empty;
    public override string ToString() => Name;
}
