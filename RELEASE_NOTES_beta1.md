# BiliDesktop beta1

BiliDesktop 的首个公开测试版本，提供哔哩哔哩视频下载、批量管理和直播录制功能。

## 主要功能

- 视频链接、BV 号和内建搜索；支持分 P、合集以及部分番剧。
- 选择可用清晰度、编码和音质；支持仅音频、封面与音频元数据。
- aria2 下载队列，支持并发上限、暂停、重试和批量操作。
- UP 主投稿与收藏夹批量下载、收藏夹自动备份、关键词自动下载。
- 通过直播间链接或 ID 添加直播间，设置录制画质并管理录制任务。
- 扫码或 Cookie 登录、User-Agent 设置、托盘后台运行、自动检查更新；支持简体中文、繁体中文及英文。

## 安装与运行

下载 `BiliDesktop-beta1-win-x64.zip`，完整解压后运行 `BiliDesktop.exe`。本版本适用于 Windows x64，不需要单独安装 .NET；首次需要处理音视频时，程序可能需要联网下载并校验 FFmpeg。自动备份和监控需要程序保持运行。

## 测试版须知

本版本仍在测试中。真实付费内容、长期自动任务和部分直播场景尚未完整验证；平台接口、登录状态、会员权限以及网络状况都可能影响结果。活动下载任务在程序退出后不能恢复。请在有权使用相关内容的前提下使用，并通过 [Issues](https://github.com/LycheeMiku/BiliDesktop/issues) 反馈问题。

感谢 [录播姬](https://github.com/BililiveRecorder/BililiveRecorder)、[bilibili-API-collect](https://github.com/rinnein/bilibili-API-collect) 和其他参考项目。
