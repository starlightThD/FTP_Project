# FTP_Project

一个基于 `Socket` 的 FTP 客户端项目，包含：
- 命令行版本（CLI）
- Avalonia 可视化版本（GUI，跨平台）

当前已支持连接、目录查看、上传下载、断点续传、传输进度、任务暂停/继续（GUI）。

## 启动方式

### 1) 命令行（CLI）

```bash
dotnet run --project FTP_Project.csproj
```

常用命令：
- `connect <host> [port]`
- `ls [path]`
- `upload <local> [remote]`
- `download <remote> [local]`
- `disconnect`
- `exit`

输入增强：
- `↑/↓` 历史命令
- `Tab` 基础命令补全

### 2) 可视化前端（GUI）

```bash
dotnet run --project FTP_Project.Gui/FTP_Project.Gui.csproj
```

GUI 功能：
- 连接服务器与连接记录缓存（自动加载）
- 远程目录刷新
- 上传/下载任务列表
- 暂停与继续（续传）
- 多任务并发传输

## 缓存文件

- `cache/command_history.txt`：CLI 历史命令（最多 25 条）
- `cache/gui_connections.json`：GUI 连接记录（最多 25 条）
- 下载占位文件：`<目标文件>.part`（用于断点续传）

## 后端接口文档

详细后端能力与对接约定见：

- [docs/BACKEND_API.md](/home/thd/CS/Project/FTP_Project/docs/BACKEND_API.md)
