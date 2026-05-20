# Backend API 文档（前端对接）

本文档面向前端开发同学，说明当前 FTP 后端服务可用接口、参数、行为约定和异常语义。

## 1. 入口与核心类型

- 服务接口：`Services/IFtpService.cs`
- 真实实现：`Services/RealFtpClient.cs`
- 连接配置：`Models/FtpConfig.cs`
- 进度模型：`Models/TransferProgress.cs`
- 协议异常：`Services/FtpControlConnection.cs` 中的 `FtpConnectionException`

## 2. IFtpService 接口

```csharp
public interface IFtpService
{
    bool IsConnected { get; }
    Task ConnectAsync(FtpConfig config, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListDirectoryAsync(string path = "/", CancellationToken cancellationToken = default);
    Task UploadFileAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task DownloadFileAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
}
```

## 3. 连接配置模型

`FtpConfig` 字段：

- `Host`：FTP 服务器地址（IPv4/域名）
- `Port`：默认 `21`
- `Username`
- `Password`
- `UsePassiveMode`：当前实现按被动模式传输

## 4. 进度模型

`TransferProgress`：

- `Operation`：`upload` / `download`
- `SourcePath`：源路径
- `TargetPath`：目标路径
- `BytesTransferred`：已传输字节
- `TotalBytes`：总字节（可能为空）

前端建议：
- 若 `TotalBytes` 有值：用百分比进度条
- 若 `TotalBytes` 为空：用“已传输字节 + 速度”展示

## 5. 行为约定

### 5.1 连接与认证

- `ConnectAsync` 会执行：
  1. TCP 连接
  2. 读取欢迎响应（220）
  3. `USER`
  4. 如需密码则 `PASS`

### 5.2 目录读取

- `ListDirectoryAsync` 走 `PASV + LIST`
- 返回 `IReadOnlyList<string>`，是 FTP `LIST` 原始文本行
- 前端可按空格切分后取文件名（当前 GUI 采用该策略）

### 5.3 上传续传

- 流程：`TYPE I -> SIZE -> REST(可选) -> PASV -> STOR`
- 若远端已存在部分文件，会从偏移续传
- 是否支持续传取决于服务器对 `SIZE/REST/STOR` 的支持

### 5.4 下载续传（占位文件）

- 流程：`TYPE I -> SIZE -> REST(可选) -> PASV -> RETR`
- 本地使用占位文件：`<final>.part`
- 下载中断后保留 `.part`
- 下次下载同一路径自动从 `.part` 偏移继续
- 完成后将 `.part` 原子替换为最终文件

## 6. 暂停/继续语义（前端）

- 暂停本质：取消传输任务的 `CancellationToken`
- 继续本质：用同一源/目标重新触发任务（后端自动按偏移续传）
- 后端会将取消抛为 `OperationCanceledException`（不当成普通失败）

## 7. 异常约定

常见异常类型：

- `InvalidOperationException`
  - 未连接就调用传输
  - 重复连接
- `FtpConnectionException`
  - 协议失败、服务器拒绝、读写超时、响应码不符合预期
- `FileNotFoundException`
  - 上传时本地文件不存在
- `OperationCanceledException`
  - 用户暂停/取消任务

前端建议处理：

- `OperationCanceledException` -> 标记任务为 `Paused`
- 其他异常 -> 标记为 `Failed` 并显示错误信息

## 8. 并发建议

- 每个任务使用独立 `RealFtpClient` 实例，避免控制连接互相干扰
- 同时传输任务数建议做上限（例如 3~5），避免资源争抢

## 9. 当前缓存与状态文件

- `cache/command_history.txt`：CLI 历史输入
- `cache/gui_connections.json`：GUI 连接配置
- `*.part`：下载断点续传文件

## 10. 前端对接注意事项

- 路径建议总是用完整路径（远端和本地都尽量明确）
- 上传和下载任务建议做“目标名冲突检测”
- 恢复任务时优先复用原任务 ID（更符合用户心智）
