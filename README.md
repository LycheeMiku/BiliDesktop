# BiliDesktop

BiliDesktop 是一款面向 Windows 的哔哩哔哩视频下载与直播录制桌面工具。它使用原生桌面界面，支持下载队列、收藏夹备份和关键词自动下载。

> 当前版本为 beta3，仍在开发和测试中。平台接口、账号权限和网络状况可能影响功能；请在获得授权并遵守适用规则的前提下使用。

## 功能

- 通过视频链接、BV 号或站内搜索查找视频；支持分 P、合集及部分番剧。
- 选择可用画质、视频编码和音质，下载音视频、单独的视频、音频或封面；支持为音频添加封面和元数据。
- 使用 aria2 下载，支持任务并发上限、排队、暂停、重试和批量操作；可配置默认下载目录。
- 查询 UP 主投稿、收藏夹，手动批量下载，或设置收藏夹定期备份和关键词自动下载。
- 支持扫码或手动输入 Cookie 登录，可设置 User-Agent；部分清晰度和内容需要相应账号权限。
- 集成修改后的录播姬核心，支持直播间链接或 ID、自动录制、录制设置和记录管理。
- 支持简体中文、繁体中文和英文，支持托盘后台运行和版本更新提示。

## 使用

从 [Releases](https://github.com/LycheeMiku/BiliDesktop/releases) 下载 Windows x64 版本，完整解压后运行 `BiliDesktop.exe`。自包含版本无需单独安装 .NET。发行版包含 FFmpeg 和 ffprobe；从源码直接构建时，缺少这些组件会触发下载，也可以自行提供。不要仅将 exe 从解压目录中单独移走。

在主界面输入视频链接、BV 或 AV 号并解析；搜索、UP 主、收藏夹、关键词和直播录制位于独立入口。展开“账号”可以登录或退出。下载目录、性能、User-Agent、语言以及启动和后台运行选项集中在“设置”中。

自动备份、关键词监控和自动录制需要程序保持运行。保存的登录信息使用 Windows 当前用户的加密机制保护；登录有效期不保证服务端不会提前使会话失效。

## 从源码构建

需要 Windows 和 .NET 10 SDK。在仓库根目录执行：

```powershell
dotnet restore BiliDesktop/BiliDesktop.csproj
dotnet build BiliDesktop/BiliDesktop.csproj -c Release
dotnet run --project BiliDesktop/BiliDesktop.csproj
```

构建 Windows x64 自包含版本（此命令不包含 FFmpeg）：

```powershell
dotnet publish BiliDesktop/BiliDesktop.csproj -c Release -r win-x64 --self-contained true -o ./publish/win-x64
```

要生成包含 FFmpeg 的离线运行版本，请先取得并校验 FFmpeg 9.0.2 Essentials 构建包，再运行 `./scripts/Publish-Windows.ps1 -FfmpegArchive <压缩包路径>`；脚本会核对内置 SHA-256，并携带其许可证。

详细的实现、限制和测试记录见 [开发说明](BiliDesktop/README.md)。仓库中 `BililiveRecorder` 包含本项目使用并修改过的录播核心源码，不能直接替换为上游原版。

## 当前限制

- 这是测试版本，真实付费内容、长期自动备份和关键词监控等场景尚未全部端到端验证；不保证所有付费类型和权限均可使用。
- 直播源的可用画质、编码和连接情况取决于直播间及平台；网络中断可能导致录制不完整。
- 活动中的下载任务不会在程序退出或崩溃后恢复。部分平台返回的错误与系统对话框可能未翻译。

## 参考和致谢

- [BililiveRecorder（录播姬）](https://github.com/BililiveRecorder/BililiveRecorder)：直播录制核心。
- [bilibili-API-collect](https://github.com/rinnein/bilibili-API-collect)：接口资料。
- [DownKyi（下载姬）](https://github.com/leiurayer/downkyi)、[BilibiliDown](https://github.com/QuanXplor/BilibiliDown)、[bilidown-web](https://github.com/firstack-it/bilidown-web)：功能和使用体验参考。
- [aria2](https://github.com/aria2/aria2) 与 [FFmpeg](https://ffmpeg.org/)：下载与媒体处理。

## 许可证

本仓库原有代码许可证见 [LICENSE](LICENSE)。本项目包含和使用不同许可证的第三方代码及组件，其中录播姬核心以 [GPL-3.0](BililiveRecorder/LICENSE) 发布；aria2 等许可文本见 `BiliDesktop/vendor`。分发组合程序时还需遵守这些依赖的适用条款；完整依赖和源码清单仍需进一步核对。
