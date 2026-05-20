using FTP_Project.Commands;
using FTP_Project.Services;

Console.WriteLine("FTP Terminal Client");
Console.WriteLine("使用 Socket 实现 FTP 协议\n");

var ftpService = new RealFtpClient();
var commandLoop = new CommandLoop(ftpService);
await commandLoop.RunAsync();

await ftpService.DisconnectAsync();
