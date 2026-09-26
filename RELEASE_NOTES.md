1.2.1.0 修复无音轨视频的下载失败；“立即同步”现在会重试处于退避等待中的待处理视频。设置页按钮改用清晰的高对比度配色，并显示实际运行的插件版本与记录更新时间，方便辨别旧 DLL 或旧错误记录。已失效或无权访问的视频仍取决于 B 站 API 返回结果。

Jellyfin 10.10.7、10.11.x 和 12.x 分别提供安装包。请选择与 **Jellyfin 服务端版本**匹配的 ZIP；同一 ZIP 内的插件 DLL 为 AnyCPU 托管代码，供相应 Jellyfin/.NET 宿主上的 x64 或 ARM64 共用，不包含 FFmpeg 或平台运行库。

- `Jellyfin-10.10-anycpu`：Jellyfin 10.10.7（.NET 8）。
- `Jellyfin-10.11-anycpu`：Jellyfin 10.11.0 起（.NET 9）。
- `Jellyfin-12-anycpu`：Jellyfin 12.0 起（.NET 10）。

每个 ZIP 旁都有 SHA-256 校验文件。更新前备份旧插件目录，把旧版本从 `plugins/` 移出，再将所选 ZIP 的文件直接解压到新版本目录并重启 Jellyfin；不要删除插件数据目录或已归档视频。FFmpeg 必须由 Jellyfin 容器/系统提供，且与其 CPU 架构匹配。

这是预发布版本：三个 ABI 已完成编译和本地冒烟测试，但 10.11/12 及 ARM64 尚未在真实 Jellyfin 服务端完成端到端验证。Jellyfin 10.11 起不再提供 ARM32 支持；不承诺官方未支持的 32 位 x86 宿主。
