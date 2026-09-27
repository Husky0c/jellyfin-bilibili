# Jellyfin Bilibili 收藏归档

面向 Jellyfin 10.10.7（.NET 8）、10.11.x（.NET 9）和 12.x（.NET 10）分别构建的原生插件。使用 Bilibili App 扫码登录，选择自己的收藏夹，轻量检测新增收藏并自动下载。视频以 BV 号、CID 去重；成功归档后即使取消收藏也不会删除本地文件。未下载且已下架的视频无法保证补回。

## 当前功能

- 管理员页面扫码登录，Cookie 只保存在 Jellyfin 插件数据目录，不写入公开配置或浏览器。
- 列出当前账号自建收藏夹，选择多个收藏夹；常规只取 BV 清单做差异，发现新视频后持久化入队。下载失败按指数退避重试，归档状态按 BV/CID 去重。
- Jellyfin 每 15 分钟唤醒一次任务，但只有到期才请求 B 站；收藏夹检查间隔在默认 30 分钟到 4 小时之间自适应，并加轻微随机偏移。旧内容按页轮换复核，用于发现新增分 P、失效项和漏项。管理员可点击“立即同步”。
- 逐个分 P 下载 B 站授权返回的 DASH 视频和音频，用 FFmpeg 无损封装为 MP4。单 P 作为电影直接播放；多 P 在电影库中组成一个 BV 合集，各 P 可独立选择、显示标题与简介并记录观看进度。视频和合集分别保存元数据，UP 主姓名、身份和头像写入演员信息。
- 按每个分 P 的 CID 获取当前弹幕池，生成与 MP4 同目录的 `.danmaku.ass` 外挂字幕。在 Jellyfin 播放器的字幕菜单中选择弹幕轨道即可显示；弹幕不会烧录进视频。旧归档会在后续同步中分批补齐，已生成的字幕不会被覆盖。
- `archive-state.json` 记录 BV、CID、状态、文件路径、错误、时间。只有确认输出文件存在且非空后才标记完成。当前账号不可访问的收藏视频标记为 `unavailable`。取消收藏不清理文件或状态。
- `sync-state.json` 保存各收藏夹的 BV 快照、下次检查时间、轮换复核游标和待下载队列；与 Cookie 和归档状态一起保存在稳定的 Jellyfin 插件数据目录，升级插件版本后仍沿用。
- Cookie 失效、B 站风控或限流会停止本轮任务；不绕过会员权限。

## 构建

按目标版本安装 .NET 8、9 或 10 SDK。以 Jellyfin 10.10 为例：

```powershell
dotnet restore SmokeTests/SmokeTests.csproj --configfile NuGet.Config -p:JellyfinAbi=10.10
dotnet run --project SmokeTests/SmokeTests.csproj -c Release --no-restore -p:JellyfinAbi=10.10
pwsh ./scripts/package-release.ps1 -JellyfinAbi 10.10 -NoRestore
```

其他版本将 `JellyfinAbi` 改为 `10.11` 或 `12`。每个目标都要分别还原、测试、打包；生成的 ZIP 和 SHA-256 校验文件位于 `dist/packages/`。不要把 `runtimes` 目录或 Jellyfin 自身依赖一起放进插件目录。QRCoder 固定为 1.7.0；每个目标使用对应 Jellyfin ABI 起始版本的 NuGet 包。

| Jellyfin 服务端 | ZIP 后缀 | 目标框架 |
| --- | --- | --- |
| 10.10.7 | `Jellyfin-10.10-anycpu` | .NET 8 |
| 10.11.x | `Jellyfin-10.11-anycpu` | .NET 9 |
| 12.x | `Jellyfin-12-anycpu` | .NET 10 |

同一 Jellyfin 版本的 ZIP 是无 RID 的托管程序集，x64 和 ARM64 的 Jellyfin 宿主共用，不需要为 CPU 分别下载。FFmpeg 是容器/系统内的外部程序，必须与宿主架构匹配，且可从配置路径、`/usr/lib/jellyfin-ffmpeg/ffmpeg` 或 `PATH` 找到。Jellyfin 10.11 起已移除 ARM32；官方 Linux 32 位 x86 宿主不受支持，因此不能承诺这些环境可用。跨架构构建与冒烟测试不等于真实服务器端到端测试；请先在测试收藏夹验证插件加载、扫码及 FFmpeg 封装。

GitHub Actions 会在推送 `main` 时运行三个目标的构建和冒烟测试。推送与 `meta.json` 版本一致的 `v*` 标签后，三个目标全部通过才创建带 ZIP 和 SHA-256 文件的 [GitHub Release](https://github.com/Husky0c/jellyfin-bilibili/releases)，随后更新 `manifest.json`。Jellyfin 插件仓库清单另外记录 ZIP 的 MD5，这是 Jellyfin 安装器使用的校验格式。

## 从 Jellyfin 插件仓库安装

在 **控制台 → 插件 → 存储库** 中新增一个存储库，名称可填 `Bilibili 收藏归档`，URL 填：

```text
https://raw.githubusercontent.com/Husky0c/jellyfin-bilibili/main/manifest.json
```

保存后到 **插件 → 目录** 中找到 **Bilibili 收藏归档**，点击安装，再重启 Jellyfin。仅添加存储库不会自动安装插件；安装后 Jellyfin 会通过自己的插件更新任务获取后续兼容版本。如果先前手动安装了旧版，请先备份并移走旧插件目录，再用目录安装，避免相同插件同时存在两份。Jellyfin 容器仍需能够访问 GitHub 下载发布包。

## 部署到 Jellyfin Docker（飞牛 NAS 示例）

1. 在 NAS 上准备一个独立目录，并将其以读写方式挂载进 Jellyfin 容器，例如映射为 `/media/bilibili`。确认容器用户有写入权限；不要直接把整个已有媒体库当归档目录。FFmpeg 路径因镜像而异，可在插件页面手动指定。
2. 找到 Jellyfin 容器的 `/config` 对应的 NAS 目录。下载与服务端版本匹配的 ZIP，将包内六个运行文件及 `LICENSE` 解压到其 `plugins/BiliArchive_1.3.0.0/` 子目录；不要多套一层目录。升级时先备份并移走旧版本插件目录，不要清理归档视频或插件数据目录。
3. 重启 Jellyfin 容器，确认插件页面出现“Bilibili 收藏归档”。若服务不能启动，移走刚才添加的插件文件夹并重启。
4. 在插件页面扫码登录，刷新并勾选收藏夹，设置容器内归档目录和检查间隔后保存。
5. 点击插件页面“立即同步”，确认生成 MP4 与 NFO；然后在 Jellyfin 添加**电影**媒体库指向同一容器内目录，建议关闭在线电影元数据提供者，并扫描媒体库。手动运行 Jellyfin 计划任务只执行到期检查；“立即同步”会复查收藏夹并重试待处理视频，但不会绕过风控冷却。一次最多处理 10 个视频，可重复点击继续处理。

弹幕保存在视频旁的 `.danmaku.ass` 文件中。旧视频补齐弹幕后，请重新扫描或刷新 Jellyfin 媒体库，再在播放时手动选择“danmaku”字幕轨道。弹幕来自 B 站当前弹幕池，不包含已移入历史池的弹幕；高级弹幕等特殊模式暂不转换。具体显示效果取决于 Jellyfin 客户端对 ASS 的支持。

不建议在未确认 Docker 挂载与插件启动日志前直接对生产媒体目录进行首次同步。先用独立测试目录、小收藏夹验证。

若曾安装 `1.1.0.0` 且状态为 `Malfunctioned`，请先备份旧插件目录；旧包包含多个平台同名运行库，Jellyfin 可能在启动时重复加载 DLL。若新版仍失败，请查看 Jellyfin 启动日志中 `BiliArchive` 附近的第一条异常。

## 存档行为与限制

目录结构为 `归档目录/BV号/BV号.mp4`（单 P）；多 P 位于 `归档目录/BV号 [boxset]/P01-CID/P01-CID.mp4`、`P02-CID/P02-CID.mp4` 等目录。`[boxset]` 让 Jellyfin 电影库把 BV 文件夹识别为合集；合集的 `collection.xml` 保存整条视频标题与简介，每个 P 的同名 NFO 保存分 P 标题、B 站整体简介、UP 主演员、封面来源和各自的 BV/CID。B 站详情接口若没有独立分 P 简介，插件会在各 P 简介中标明分 P 名称并附整体简介；用户可分别编辑各 P 的 NFO。升级后，下一次归档任务会把状态记录中仍存在的旧版 `BV号/P序号-CID/video.mp4` 和 1.2.4.0 的 `BV号/BV号-cd01.mp4` 等文件移入新结构，并移动 NFO；旧目录只有 `movie.nfo` 时，也会移动为视频同名 NFO。已归档且仍在所选收藏夹中的视频会分批补充新版简介，每次最多处理 10 个 BV；无需重新下载现存视频。迁移不会覆盖目标文件，冲突会在日志中报错。迁移前请备份归档目录，完成后扫描或刷新 Jellyfin 媒体库；已删除或移出当前归档目录的文件不会被迁移。Jellyfin 不同客户端的演员详情页展示可能不同。收藏夹移除、取消选择收藏夹和退出登录都不会清理归档。若状态显示完成而文件丢失，下次遇到该收藏视频时会重新下载。B 站返回 `-404`、`62002` 或 `62012` 时记录为当前账号不可访问，并每天复查；如果 UP 主重新公开视频，后续同步仍可归档。B 站风控、Cookie 过期、会员或地域限制、API 变更、已删除视频都可能导致失败；失败状态会记录供查看。服务器必须能直接访问 B 站 API 和 CDN。

本项目不提供版权或访问控制绕过；请仅归档你有权访问和保存的内容。

项目以 [GNU GPL-3.0](LICENSE) 授权。
