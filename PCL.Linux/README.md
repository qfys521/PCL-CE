# PCL CE Linux 原生版（1.0.0）

Linux 入口使用 Avalonia，复用 PCL.Core 和 PCL.Launcher.Core。
界面按 Windows 版移植：蓝色标题栏、启动 / 下载 / 设置 / 更多导航、300px 左侧栏、账号区、启动按钮、卡片、主题颜色和背景。
WPF 本身仍只适用于 Windows；Linux 界面是原有设计的原生实现。

发布产物是单一的 `PCL-CE-Linux-1.0.0-x86_64.AppImage`：自带 .NET 10 运行时、中文字体与图标主题，目标机无需安装 .NET、Mono 或 Wine。

## 已实现

- Mojang 正式版、快照及旧版列表，客户端下载、依赖 / 资源校验、Linux 原生库和日志。
- 继承版本、共享缓存、隔离实例、补全文件、所需 Java 自动下载、全局及版本独立 Java / 内存 / 启动参数。
- Fabric、Forge、NeoForge、Quilt 的版本查询与客户端安装；Forge 系列执行安装器处理步骤并校验输出。
- 离线账号、Microsoft 设备码登录及刷新、第三方 Yggdrasil 认证及 authlib-injector。
- Modrinth / CurseForge 搜索、兼容版本筛选、模组必要依赖、资源包及光影下载。
- 本地模组元数据、导入、启用 / 禁用、移至可恢复的回收目录。
- Modrinth、CurseForge、MultiMC / Prism、MCBBS、HMCL 和传统目录整合包导入，导出 `.mrpack`。
- 原 Windows 自定义主页常用 MyCard、MyButton、MyHint、文本与布局控件，以及 JSON 主页。
- Linux 原生 EasyTier 联机，沿用 Windows Scaffolding 的 `U/` 房间码、玩家列表、局域网广播和端口转发。
- 游戏日志查看、原版崩溃规则分析。游戏运行时仍可操作启动器与联机大厅。
- 页面淡入滑动、卡片依次进入、导航标记移动、按钮悬停与按压回弹、输入焦点和列表选中反馈，以及圆角窗口、卡片、输入框和弹窗。快速切页可取消未完成的动画；最大化时窗口使用直角。
- 单实例运行：再次打开 `.mrpack`、`pcl-ce://` 链接或启动第二个实例时，参数会转发给已运行的窗口。
- 用户级桌面集成：`--register-desktop` / 设置页可安装或刷新桌面入口、16–512 像素图标、MIME 类型与 `pcl-ce://` 协议，不写入 `/usr`、不需要 sudo。
- 系统通知（`org.freedesktop.Notifications`）与 XDG Desktop Portal 文件选择器，均可在设置中关闭。
- AppImage 内置 Noto Sans CJK SC 中文字体与 hicolor 图标主题，系统字体仍然可用；界面字体可在设置中选择。

“设置 → 个性化 → 启用界面动画”可即时开关动画，点击“应用”保存偏好。

## 运行（AppImage）

```sh
chmod +x PCL-CE-Linux-1.0.0-x86_64.AppImage
./PCL-CE-Linux-1.0.0-x86_64.AppImage          # 或在文件管理器中双击
```

- 自带 .NET 10 运行时与全部托管 / 原生库，**不需要**安装 .NET、Mono 或 Wine。
- 目标机需要的基础系统组件：glibc ≥ 2.27（Ubuntu 20.04 / Debian 10 或更新）、`libstdc++6` 与 `libgcc_s`（桌面发行版默认自带）、X11 或 XWayland（`libX11` / `libxcb`）、Fontconfig、Mesa / GPU 驱动（OpenGL，软件渲染时用 llvmpipe）。
- 可选：`libgtk-3`（Avalonia 回退到 GTK 原生文件对话框时需要）、DBus 会话（系统通知与 Portal 文件选择器）。
- 中文字体、图标主题、桌面入口模板、MIME 定义与许可证均包含在 AppImage 内。

### 没有 FUSE 时

AppImage 正常通过 FUSE 挂载运行；如果系统没有 FUSE（`libfuse3` / `/dev/fuse`），可以用解压运行模式：

```sh
APPIMAGE_EXTRACT_AND_RUN=1 ./PCL-CE-Linux-1.0.0-x86_64.AppImage
./PCL-CE-Linux-1.0.0-x86_64.AppImage --appimage-extract-and-run
```

也可以手动解包后直接运行 `squashfs-root/AppRun`：

```sh
./PCL-CE-Linux-1.0.0-x86_64.AppImage --appimage-extract
./squashfs-root/AppRun
```

### 命令行

```sh
./PCL-CE-Linux-1.0.0-x86_64.AppImage --help              # 全部选项
./PCL-CE-Linux-1.0.0-x86_64.AppImage --version
./PCL-CE-Linux-1.0.0-x86_64.AppImage --diagnose          # 路径、Java、显卡、桌面环境
./PCL-CE-Linux-1.0.0-x86_64.AppImage --list              # Mojang 正式版列表
./PCL-CE-Linux-1.0.0-x86_64.AppImage --smoke-test        # 检查全部页面与动画后退出（不联网）
./PCL-CE-Linux-1.0.0-x86_64.AppImage --screenshot <目录> # 渲染界面截图后退出
./PCL-CE-Linux-1.0.0-x86_64.AppImage --install 1.21.1    # 命令行安装原版（可加 --loader fabric|quilt）
./PCL-CE-Linux-1.0.0-x86_64.AppImage --launch 1.21.1     # 命令行启动，可加 --java/--memory/--timeout
./PCL-CE-Linux-1.0.0-x86_64.AppImage --import-pack 包.mrpack --instance 名称
./PCL-CE-Linux-1.0.0-x86_64.AppImage --verify-network    # 联网自检 Mojang/Modrinth/mrpack 解析
./PCL-CE-Linux-1.0.0-x86_64.AppImage --wipe-data config accounts java cache logs game --yes
./PCL-CE-Linux-1.0.0-x86_64.AppImage --register-desktop   # 安装/刷新桌面集成
./PCL-CE-Linux-1.0.0-x86_64.AppImage --unregister-desktop # 移除桌面集成
```

也可以把 `.mrpack` 整合包、`.minecraft` 目录或 `pcl-ce://` 链接作为参数传入；启动器已在运行时会把参数转发给已运行实例。

### 桌面集成

首次启动会自动安装（可在“设置 → 桌面集成”中关闭）用户级桌面集成：

- `$XDG_DATA_HOME/applications/pcl-ce.desktop`（`Exec` 指向 AppImage 绝对路径）
- `$XDG_DATA_HOME/icons/hicolor/{16,24,32,48,64,128,256,512}x…/apps/pcl-ce.png`
- `$XDG_DATA_HOME/mime/packages/pcl-ce.xml`：`application/x-mrpack`（`*.mrpack`）、`application/x-minecraft-package`（`*.mcworld` `*.mcpack` `*.mctemplate` `*.mcaddon`）
- `x-scheme-handler/pcl-ce` 协议，用于 `pcl-ce://import?url=…`、`pcl-ce://launch?version=…`、`pcl-ce://page/…`、`pcl-ce://open?path=…`

不会修改系统目录、不会覆盖其他应用的桌面文件，也不需要 sudo。移动 AppImage 后运行一次 `--register-desktop` 即可更新入口路径。

## 数据与路径（XDG）

| 用途 | 路径 |
| --- | --- |
| 设置 | `$XDG_CONFIG_HOME/pcl-ce/linux.json`（默认 `~/.config/pcl-ce/linux.json`，权限 0600） |
| 账号令牌 | `$XDG_CONFIG_HOME/pcl-ce/accounts.json`（0600） |
| 数据（Java 运行时、联机组件、下载缓存索引） | `$XDG_DATA_HOME/pcl-ce`（默认 `~/.local/share/pcl-ce`） |
| 缓存（下载、原生库临时目录）、AppImage 字体缓存 | `$XDG_CACHE_HOME/pcl-ce`（字体缓存：`$XDG_CACHE_HOME/pcl-ce/fontconfig`） |
| 状态与日志 | `$XDG_STATE_HOME/pcl-ce/logs/pcl-ce.log`（默认 `~/.local/state/pcl-ce/logs`） |
| 运行时（单实例锁 / socket） | `$XDG_RUNTIME_DIR/pcl-ce` |
| 游戏目录 | 默认 `~/PCL-CE`，可在设置中修改 |

默认游戏目录是 `~/PCL-CE`，可在设置中修改；数据清理只删除启动器自己写入的目录和带 `.pcl-ce-managed` 标记的游戏目录。
令牌未加密，请勿分享设置文件；退出账号会删除相应令牌，第三方账号密码不保存。
启动器写入的游戏运行日志位于 `$XDG_STATE_HOME/pcl-ce/logs/game-*.log`，游戏自身日志仍在实例的 `logs/`；删除的模组位于 `mods/.pcl-trash`。
网络下载遵循 .NET 的系统代理 / `HTTPS_PROXY` 设置；Java 安装器自身的网络行为取决于 Java 配置。

## 中文字体与图标

- AppImage 内置 Noto Sans CJK SC Regular / Bold（SIL OFL 1.1），并带 `usr/etc/fonts/fonts.conf`。AppRun 通过 `FONTCONFIG_FILE` 加载该配置：内置字体目录 + 系统字体目录都会扫描，fontconfig 缓存写到 `$XDG_CACHE_HOME/pcl-ce/fontconfig`，不会写入 AppImage 或 `/tmp`。
- 内置 hicolor 图标 16/24/32/48/64/128/256/512 像素，AppRun 把 `$APPDIR/usr/share` 前置到 `XDG_DATA_DIRS`，桌面环境无需安装图标包即可显示启动器图标。
- 系统字体仍会参与匹配；字体子集仅按东亚字符集裁剪（CJK 统一表意文字及扩展 A、假名、谚文、拉丁、标点、兼容形式），用户名 / 模组名 / 日志中的常见中日韩字符均可显示，极罕见字符回退系统字体。
- 许可文本随包安装到 `usr/share/doc/pcl-ce/`（Noto OFL、Adwaita 若包含、Apache-2.0）。

## 账号与服务

- 离线账号开箱即用；Microsoft 正版登录需要在“设置 → 账号与服务”填写获准访问 Minecraft 服务的 OAuth Client ID（或设置 `PCL_MS_CLIENT_ID`），源码不包含官方凭据；登录使用设备码流程，令牌保存在 0600 权限的 `accounts.json`。
- 第三方 Yggdrasil / authlib-injector 账号可直接填写认证服务器地址登录。
- Modrinth 无需密钥；CurseForge 搜索与整合包下载需要自己的 API Key（界面填写或 `PCL_CURSEFORGE_API_KEY`）。作者禁止第三方下载的文件需要到项目网站手动下载。
- 下载源默认“自动”：Mojang 官方优先，网络不可达时回退到 BMCLAPI 公开镜像；`--verify-network` 可验证联网能力。

## 数据清理

“设置 → 清理数据”页面（等价于 `--wipe-data`）可以分别勾选并删除：

| 分类 | 删除内容 |
| --- | --- |
| 配置 | `$XDG_CONFIG_HOME/pcl-ce/linux.json` |
| 账号与令牌 | `$XDG_CONFIG_HOME/pcl-ce/accounts.json` |
| Java 运行时 | `$XDG_DATA_HOME/pcl-ce/runtime` |
| 启动器工具 | `$XDG_DATA_HOME/pcl-ce/tools` |
| 下载缓存 | `$XDG_CACHE_HOME/pcl-ce/downloads` |
| 其他缓存 | `$XDG_CACHE_HOME/pcl-ce` 下的原生库与临时文件 |
| 运行日志 | `$XDG_STATE_HOME/pcl-ce/logs` |
| 运行时状态 | `$XDG_RUNTIME_DIR/pcl-ce` |
| 游戏数据 | 带 `.pcl-ce-managed` 标记的游戏目录（版本、实例、存档、模组、整合包） |

安全策略：删除前必须二次确认（游戏数据额外确认一次）；只删除启动器自己拥有的 XDG 目录和带标记的游戏目录；用户主目录、系统目录、应用数据目录的上级、符号链接目标以及没有标记的既有 Minecraft 目录一律拒绝删除；配置目录里出现无法识别的文件时保留目录并提示。

## 已知限制

- **Wayland：** Avalonia 11.3 没有原生 Wayland 后端，因此 Wayland 会话通过 XWayland（X11 协议）运行。好处是输入法候选窗、弹窗与窗口定位使用真实 X11 坐标，不会出现 Wayland 下常见的候选框错位；代价是需要系统启用 XWayland。没有 XWayland 时程序会给出中文提示并退出（退出码 2），不会崩溃。
- **基础系统库不打包：** glibc、`libX11` / `libxcb`、Fontconfig、Mesa / GPU 驱动、可选的 GTK3 与 DBus 均由系统提供。AppImage 只保证 .NET 运行时、中文字体与图标主题自包含。
- **glibc 版本：** 构建于较新的发行版，运行时要求 glibc ≥ 2.27（Ubuntu 20.04 / Debian 10 或更新），AppImage 内核心原生库最高使用 `GLIBC_2.27` 符号；更旧的发行版请在对应系统上执行 `scripts/build-appimage.sh` 重新构建。包内附带但 Linux 版当前未启用的可选组件（`libe_sqlite3.so` SQLite 缓存、`libdtls_native.so` DTLS DNS）在启用时分别需要 glibc 2.34 / 2.38。
- **无 FUSE 时**必须使用 `APPIMAGE_EXTRACT_AND_RUN=1` 或 `--appimage-extract-and-run`（首次启动会多花 1–2 秒解压）。
- 移动 AppImage 文件后需要重新执行 `--register-desktop`，否则桌面入口仍指向旧路径。
- 字体子集不包含全部 Unicode 字符；缺失的罕见字符回退到系统字体（若系统也没有则显示豆腐块）。
- 与所有 Minecraft 启动器一样，登录令牌会作为 JVM 参数传给游戏进程，因此在本机 `/proc/<pid>/cmdline` 中可见；启动器只在日志中做了脱敏。多用户机器请限制其他用户读取进程信息。
- 内置 Adwaita 图标子集只是 GTK 回退对话框的保险；Avalonia 11.3 本身不依赖 GTK 主题。

## 移植边界

此版本仍在完善与 Windows 版的细节一致性，不能视为全部功能已完全对齐。

- 不直接加载任意 WPF XAML / CLR 对象、Windows 插件或 Windows 可执行文件；自定义主页中的未兼容控件和事件会明确报错。
- 自定义主页兼容常用展示、网页、复制、启动、切页、提示、对话框、文件下载和执行程序事件；Windows 设置键、自定义变量和帮助子页面尚未完整映射。
- OptiFine / LiteLoader 自动安装、皮肤上传与账号库、多语言界面、官方更新 / 赞助 / NAID 服务及 Windows 专属系统工具尚未移植。已包含在标准版本元数据中的加载器可通过目录包导入。
- 不自动迁移 Windows 的注册表或配置。整合包内的 Windows 脚本和本机程序不会获得 Linux 兼容性。
- 旧式整合包可能包含私有格式或无法从公开仓库取得的组件，这类文件需要包作者提供。
- 已实测 Linux 双端 Scaffolding 联机；尚未与真实 Windows 客户端及所有公网 NAT 环境逐一验证。

## 构建与验证

仓库使用 C# 15，需要 .NET 11 SDK，输出目标为 .NET 10。

```sh
DOTNET=/path/to/dotnet ./scripts/build-appimage.sh
/path/to/dotnet test PCL.Linux.Test/PCL.Linux.Test.csproj -c Release
```

脚本不依赖 appimagetool、docker 或 FUSE，产物只有 `artifacts/PCL-CE-Linux-1.0.0-x86_64.AppImage`。
构建机需要 `mksquashfs`（含 zstd）、`python3`（可选 fonttools）、`curl` 或 `wget`、`file`、`desktop-file-validate`、`update-mime-database`、`update-desktop-database`。
固定版本的 AppImage runtime 与 Noto Sans CJK 会校验 SHA256 并缓存在 `artifacts/.assets/`；下载失败时字体回退到构建机的 `fonts-noto-cjk` 并记录日志。
包内 `usr/share/doc/pcl-ce/BUILD-INFO.txt` 记录 SDK、源码版本、字体来源与压缩方式；`SOURCE_DATE_EPOCH` 控制可复现时间戳。
验证记录：`--appimage-offset`、`--appimage-extract`、解包后 `AppRun --version`、`APPIMAGE_EXTRACT_AND_RUN=1 … --help`、`desktop-file-validate` 与内置字体解析均由脚本自动执行。
本次发布的功能验证结果（原版 / Fabric / 旧版 Java 自动下载 / Modrinth / .mrpack 导入 / 单实例 / 桌面集成 / 数据清理 / XDG 合规）见 [VALIDATION.md](VALIDATION.md)。
`scripts/build-linux.sh` 仅保留为开发用文件夹 / tar.gz 构建，不是发布产物。

PCL.Core 与 Linux 界面遵循根目录 Apache-2.0；PCL.Launcher.Core 遵循包中的 `LICENCE-launcher`。
Noto Sans CJK 遵循 SIL OFL 1.1；可选 Adwaita 子集遵循其上游许可（许可证文本随包安装）。
EasyTier、authlib-injector 和 Eclipse Temurin 为运行时按需下载的独立项目，分别遵循其上游许可证。
