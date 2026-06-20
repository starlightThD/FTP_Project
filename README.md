# FTP_Project

一个基于 `Socket` 的 FTP 客户端项目，包含：
- 命令行版本（CLI）
- Avalonia 可视化版本（GUI，跨平台）

当前已支持连接、目录查看、上传下载、断点续传、传输进度、任务暂停/继续（GUI）。

## 启动方式


### 可视化前端（WPF）

```bash
dotnet restore
cd FTP_Project.Wpf & dotnet run
```

## 缓存文件

- `cache/command_history.txt`：CLI 历史命令（最多 25 条）
- `cache/gui_connections.json`：GUI 连接记录（最多 25 条）
- 下载占位文件：`<目标文件>.part`（用于断点续传）

## 后端接口文档

详细后端能力与对接约定见：

- [docs/BACKEND_API.md](/home/thd/CS/Project/FTP_Project/docs/BACKEND_API.md)
