using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FTP_Project.Services;

/// <summary>
/// FTP 控制连接 - 负责 Socket 连接、命令发送、响应读取
/// </summary>
public sealed class FtpControlConnection : IAsyncDisposable
{
    private Socket? _socket;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _isConnected;

    public bool IsConnected => _isConnected && _socket?.Connected == true;

    /// <summary>
    /// 连接到 FTP 服务器
    /// </summary>
    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            throw new InvalidOperationException("Already connected");

        try
        {
            Console.WriteLine($"[调试] 创建 Socket...");
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socket.ReceiveTimeout = 10000;  // 10 秒接收超时
            _socket.SendTimeout = 10000;     // 10 秒发送超时
            
            Console.WriteLine($"[调试] 正在连接到 {host}:{port}...");
            
            // 使用含有超时保护的连接
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                await _socket.ConnectAsync(host, port);
            }

            Console.WriteLine($"[调试] Socket 已连接，创建流...");
            
            _stream = new NetworkStream(_socket, ownsSocket: false);
            // FTP 控制通道使用 ASCII 文本命令，避免 UTF-8 BOM 干扰首条命令
            _reader = new StreamReader(_stream, Encoding.ASCII);
            _writer = new StreamWriter(_stream, Encoding.ASCII) 
            { 
                AutoFlush = true,
                NewLine = "\r\n"  // FTP 协议需要 CRLF
            };

            _isConnected = true;
            Console.WriteLine($"[调试] 连接层初始化完成");
        }
        catch (OperationCanceledException ex)
        {
            _socket?.Dispose();
            _stream?.Dispose();
            _reader?.Dispose();
            _writer?.Dispose();
            throw new FtpConnectionException($"Connection timeout to {host}:{port}", ex);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[调试] 连接错误: {ex.GetType().Name} - {ex.Message}");
            _socket?.Dispose();
            _stream?.Dispose();
            _reader?.Dispose();
            _writer?.Dispose();
            throw new FtpConnectionException($"Failed to connect to {host}:{port}", ex);
        }
    }

    /// <summary>
    /// 发送 FTP 命令
    /// </summary>
    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected");

        if (_writer == null)
            throw new InvalidOperationException("Writer is null");

        try
        {
            Console.WriteLine($"[C] {command}");
            await _writer.WriteLineAsync(command);
        }
        catch (Exception ex)
        {
            _isConnected = false;
            throw new FtpConnectionException($"Failed to send command: {command}", ex);
        }
    }

    /// <summary>
    /// 读取 FTP 响应（单行或多行）
    /// 多行响应格式: "XXX-..." 到 "XXX ..."（同一响应码）
    /// </summary>
    public async Task<FtpResponse> ReadResponseAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected");

        if (_reader == null)
            throw new InvalidOperationException("Reader is null");

        try
        {
            var lines = new List<string>();
            string? firstLine = null;
            int responseCode = 0;
            bool isMultiLine = false;

            Console.WriteLine("[调试] 等待服务器响应...");

            while (true)
            {
                // 读取一行，加超时
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    var line = await _reader.ReadLineAsync(cts.Token);
                    
                    if (line == null)
                    {
                        _isConnected = false;
                        throw new FtpConnectionException("Connection closed by server");
                    }

                    Console.WriteLine($"[S] {line}");
                    lines.Add(line);

                    // 解析第一行获取响应码
                    if (firstLine == null)
                    {
                        firstLine = line;
                        if (line.Length >= 3 && int.TryParse(line.Substring(0, 3), out var code))
                        {
                            responseCode = code;
                            isMultiLine = line.Length > 3 && line[3] == '-';
                            
                            // 单行响应立即返回
                            if (!isMultiLine)
                            {
                                break;
                            }
                        }
                    }
                    else if (isMultiLine && line.Length >= 3 && int.TryParse(line.Substring(0, 3), out var code))
                    {
                        // 多行响应结束条件：同样的响应码 + 空格（不是 -)
                        if (code == responseCode && line.Length > 3 && line[3] != '-')
                        {
                            break;
                        }
                    }
                }
            }

            return new FtpResponse(responseCode, lines);
        }
        catch (OperationCanceledException ex)
        {
            _isConnected = false;
            throw new FtpConnectionException("Read response timeout", ex);
        }
        catch (Exception ex) when (!(ex is FtpConnectionException))
        {
            _isConnected = false;
            throw new FtpConnectionException("Failed to read response", ex);
        }
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _isConnected = false;
        
        try
        {
            if (_writer != null)
            {
                await _writer.WriteLineAsync("QUIT");
            }
        }
        catch
        {
            // 忽略发送错误
        }

        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _stream?.Dispose();
        _socket?.Dispose();
        _isConnected = false;
    }
}

/// <summary>
/// FTP 响应数据
/// </summary>
public sealed class FtpResponse
{
    public int Code { get; }
    public IReadOnlyList<string> Lines { get; }
    public string Message => Lines.Count > 0 ? Lines[^1] : string.Empty;

    public FtpResponse(int code, IReadOnlyList<string> lines)
    {
        Code = code;
        Lines = lines;
    }

    public bool IsSuccess => Code >= 200 && Code < 300;
    public bool IsError => Code >= 400;

    public override string ToString()
    {
        return string.Join("\n", Lines);
    }
}

/// <summary>
/// FTP 连接异常
/// </summary>
public class FtpConnectionException : Exception
{
    public FtpConnectionException(string message) : base(message) { }
    public FtpConnectionException(string message, Exception innerException) 
        : base(message, innerException) { }
}
