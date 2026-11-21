# PCL-CE-Linux 1.0.0 验证记录

发布产物：`PCL-CE-Linux-1.0.0-x86_64.AppImage`，72,550,904 字节（69.2 MiB），
SHA256 `fb0df446284a6be401898942c118175d852986c7d4e5149d82707372a5719dee`。
GitHub Release：<https://github.com/qfys521/PCL-CE/releases/tag/v1.0.0-linux>
界面截图：`docs/linux/`（启动页浅色/深色/2x、数据清理、系统诊断、桌面集成）。
二进制构建自源码提交（Linux UI 重建后的 `pcl-ce-linux` 分支 HEAD）（其后仅有文档与截图提交，不影响二进制；构建脚本从无 `.git` 的导出副本构建，故包内 BUILD-INFO 的 Source 显示为导出副本）。

本文件记录 1.0.0 AppImage 的构建与验证环境、命令和结果。验证日期为 2026-11-22，构建机为 Ubuntu 26.10（glibc 2.41，16 核），运行会话为 GNOME Wayland + XWayland、NVIDIA RTX 4060 Laptop。

## 构建与静态验证

| 项目 | 结果 |
| --- | --- |
| .NET SDK | 11.0.100（项目内 `.tools/dotnet`），发布目标框架 .NET 10.0.15 自包含 linux-x64 |
| `PCL.Linux` Release 构建 | 0 错误 |
| `PCL.Linux.Test` | 72 / 72 通过（XDG 路径、清理数据、桌面集成、远程文件名安全、大小写容错、镜像回退、Linux 启动规则、整合包、Java 检测等） |
| 原 Windows WPF 工程 | 0 错误（仅移除 CE 提示卡片与对应多语言键，未改动其余界面） |
| AppImage 构建 | `scripts/build-appimage.sh`（mksquashfs zstd-19 + 固定版本 type2 runtime，无需 FUSE / appimagetool / docker） |
| 产物 | `artifacts/PCL-CE-Linux-1.0.0-x86_64.AppImage`，约 69 MiB，SquashFS 内 383 个文件 |
| `--appimage-offset` / `--appimage-extract` | 通过；AppRun、`.desktop`、`.DirIcon`、图标、MIME、字体齐全 |
| `desktop-file-validate` | 通过（仅标准 “多个主分类” 提示）；AppStream `appstreamcli validate` 返回 0 |
| glibc 符号版本 | AppImage 运行时为 static-pie；核心原生库最高 `GLIBC_2.27`（Ubuntu 20.04 / Debian 10+）。包内两个当前未启用的可选组件 `libe_sqlite3.so`（2.34）与 `libdtls_native.so`（2.38）不影响启动与常规下载 |
| 空环境运行 | 解包后 `AppRun --version/--diagnose/--help` 在 `env -i` 下可运行，使用内置 .NET 运行时 |

## 功能验证

在隔离的 `HOME` / XDG 目录中通过 AppImage 与开发构建完成：

| 场景 | 结果 |
| --- | --- |
| 界面冒烟测试 | `--smoke-test`：17 个页面、切页动画、弹窗、动画开关全部通过（FUSE 挂载与 `APPIMAGE_EXTRACT_AND_RUN=1` 两种方式） |
| 界面还原 | 按原版 WPF 界面重建：900×550 窗口 / 48 标题栏 / PCL Logo + CE 徽标 / 4 个胶囊标签 / 300 与 152 左侧栏 / 卡片与 42 像素列表 / PCL English + lucide 图标 / OKLCH 配色（猫蓝等 4 套）/ 彩色背景；浅色、深色、2x HiDPI 截图见 `docs/linux/` |
| 个性化 | 主题模式、主题色、彩色背景、界面字体、标题栏 Logo（默认/文本/无）、系统窗口装饰切换，应用后即时生效 |
| 首次启动稳定性 | 在 AppImage 内预置并预热 fontconfig 缓存（AppRun 使用稳定的用户级字体目录，程序启动时全局预加载 Skia 自带 HarfBuzz），空 HOME 首次启动连续 3 次、`APPIMAGE_EXTRACT_AND_RUN=1` 均通过，不再出现 `free(): invalid pointer` |
| 界面截图 | `--screenshot`：浅色 / 深色 / 2x 及 下载、设置、桌面集成、清理数据、系统信息、关于 页面；中文无豆腐块 |
| Minecraft 1.21.1 原版 | 安装 3948 个文件并 SHA-1 校验；启动 25 秒，LWJGL/OpenGL、纹理图集与渲染线程正常 |
| Fabric 0.20.1 / 1.21.1 | 加载器安装并启动成功（日志：`Loading Minecraft 1.21.1 with Fabric Loader 0.20.1`） |
| Minecraft 1.12.2（旧版） | 自动下载并校验 Eclipse Temurin Java 8 到 `$XDG_DATA_HOME/pcl-ce/runtime/...`；启动 20 秒正常 |
| Java 多版本 | 检测系统 8 个 Java；所需主版本缺失时自动下载对应 Temurin JRE，已在数据目录中管理 |
| Modrinth | 搜索 Sodium、筛选 1.21.1 + Fabric、下载 1.57 MB 模组并 SHA-1 校验通过 |
| `.mrpack` 导入 | 解析 modrinth.index.json，安装基础版本并把 `overrides/` 解压到隔离实例（`instances/<名称>`）；真实整合包导入命令 `--import-pack` 通过 |
| 下载镜像回退 | 单元测试覆盖官方→BMCLAPI 回退、4xx/哈希不符不切换；Mojang 直连可用 |
| 单实例 | 第二个进程把 `pcl-ce://page/settings` 转发给已运行实例并退出（日志“参数已转发”） |
| 桌面集成 | `--register-desktop` 写入 `$XDG_DATA_HOME/applications`、hicolor 16–512、MIME、`pcl-ce://`，`desktop-file-validate` 通过；`--unregister-desktop` 只删除自己的文件 |
| 数据清理 | 分类删除配置/账号/Java/缓存/日志/游戏数据；未标记的既有游戏目录被拒绝且保持完整；配置目录中未知文件被保留；HOME 中的哨兵文件不受影响 |
| XDG 合规 | 在空 HOME 中只创建 `~/PCL-CE`、`~/.config/pcl-ce`、`~/.cache/pcl-ce`、`~/.local/share/pcl-ce`、`~/.local/state/pcl-ce` 与 `$XDG_RUNTIME_DIR/pcl-ce`；启动器不写 `/tmp`、当前目录或 HOME 根目录 |
| 字体 | AppImage 内置 Noto Sans CJK SC Regular/Bold；fontconfig 缓存写入 `$XDG_CACHE_HOME/pcl-ce/fontconfig` |
| 系统诊断 | `--diagnose` 输出 XWayland 会话、OpenGL/Vulkan 设备、中文字体、Java、Portal、通知、输入法、缩放与主题、全部 XDG 路径 |

## 未验证 / 已知边界

- Microsoft 正版登录与第三方 Yggdrasil 仅为代码路径与单元测试，未使用真实账号完成登录（需要用户自己的 OAuth Client ID / 认证服务器）。
- CurseForge API 搜索未使用真实 API Key 验证；Modrinth 已验证。
- 未与 Windows 版客户端做联机互通测试；EasyTier 相关逻辑复用上一版本验证结果。
- 未做长时间游戏游玩、OptiFine / LiteLoader 自动安装、皮肤上传与账号库、Windows 专属工具与官方更新服务。
- Avalonia 11.3 无原生 Wayland 后端，Wayland 会话经 XWayland 运行（输入法候选窗与窗口定位使用 X11 坐标）。
- glibc、libX11/libxcb、Fontconfig、Mesa/GPU 驱动、可选 GTK3 与 DBus 属于基础系统组件，不打包进 AppImage。

## 复现命令

```sh
# 构建（建议在仓库副本中执行，避免污染工作树）
REPO=/home/qfys521/文档/Code/PCL-CE
rsync -a --exclude bin --exclude obj --exclude artifacts --exclude .tools \
      --exclude .git --exclude .notes "$REPO/" /tmp/pcl-build-appimage/
cd /tmp/pcl-build-appimage
DOTNET="$REPO/.tools/dotnet/dotnet" ./scripts/build-appimage.sh

# 测试
DOTNET="$REPO/.tools/dotnet/dotnet"
"$DOTNET" test PCL.Linux.Test/PCL.Linux.Test.csproj -c Release

# 验证 AppImage
./artifacts/PCL-CE-Linux-1.0.0-x86_64.AppImage --version
./artifacts/PCL-CE-Linux-1.0.0-x86_64.AppImage --diagnose
./artifacts/PCL-CE-Linux-1.0.0-x86_64.AppImage --smoke-test
APPIMAGE_EXTRACT_AND_RUN=1 ./artifacts/PCL-CE-Linux-1.0.0-x86_64.AppImage --smoke-test
```
