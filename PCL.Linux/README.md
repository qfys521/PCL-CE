# PCL CE Linux 原生版（0.2.1 预览）

Linux 入口使用 Avalonia，复用 PCL.Core 和 PCL.Launcher.Core。
界面按 Windows 版移植：蓝色标题栏、启动 / 下载 / 设置 / 更多导航、300px 左侧栏、账号区、启动按钮、卡片、主题颜色和背景。
WPF 本身仍只适用于 Windows；Linux 界面是原有设计的原生实现。

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

“设置 → 个性化 → 启用界面动画”可即时开关动画，点击“应用”保存偏好。

## 运行

解压后，在桌面会话中运行 `./PCL-CE`，无需安装 .NET 或 Wine。
需要 glibc Linux x86_64、X11 或 XWayland、Fontconfig、libX11、libICE、libSM、系统字体和 OpenGL / Mesa。
Ubuntu 可安装 `libx11-6 libice6 libsm6 libfontconfig1 libgl1 fonts-noto-cjk`。
游戏需要对应版本的 Java，例如 1.12.2 使用 Java 8、1.20.1 使用 Java 17、1.21.1 使用 Java 21。
启动器会扫描安装的 Java 并选择所需版本，未找到时从 Adoptium 下载并校验 Eclipse Temurin Linux 运行时。
也可在启动选项或版本设置中指定 `bin/java`，或手动点击“下载当前版本所需 Java”。

1. 在“下载”中刷新 Minecraft 列表，选择游戏及加载器后安装。
2. 在“版本选择”中选中版本，或创建隔离实例。
3. 填写离线用户名，或登录账号，然后启动。
4. 联机房主先在游戏中开放局域网，将游戏显示的端口填入“更多 → 联机大厅”；加入者填写 `U/` 编号。

Microsoft 登录需要获准访问 Minecraft 服务的 OAuth Client ID（界面填写或 `PCL_MS_CLIENT_ID`）。
CurseForge 需要自己的 API Key（界面填写或 `PCL_CURSEFORGE_API_KEY`）；Modrinth 无需密钥。
源码不包含官方服务凭据。作者禁止外部下载的 CurseForge 文件需在项目网站手动下载。
首次联机会下载并校验 EasyTier 2.6.4 Linux 组件，无需 root / TUN。

```sh
./PCL-CE --diagnose   # 检测 Java 与本地版本
./PCL-CE --list       # 获取 Mojang 正式版列表
./PCL-CE --smoke-test # 检查所有页面、快速切页和动画开关后退出，不联网或启动游戏
```

默认游戏目录：`$XDG_DATA_HOME/pcl-ce/minecraft`，未设置时为 `~/.local/share/pcl-ce/minecraft`。
设置和账号：`$XDG_CONFIG_HOME/pcl-ce/linux.json`，默认 `~/.config/pcl-ce/linux.json`，权限 0600。
令牌未加密，请勿分享设置文件；退出账号会删除相应令牌，第三方账号密码不保存。
游戏日志位于实例的 `logs/pcl-*.log`，删除的模组位于 `mods/.pcl-trash`。
网络下载遵循 .NET 的系统代理 / `HTTPS_PROXY` 设置；Java 安装器自身的网络行为取决于 Java 配置。

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
DOTNET=/path/to/dotnet ./scripts/build-linux.sh
/path/to/dotnet test PCL.Linux.Test/PCL.Linux.Test.csproj -c Release
```

产物：`artifacts/pcl-ce-linux-x64/`、`artifacts/PCL-CE-linux-x64.tar.gz` 和 `.sha256`。
包内 `BUILD-INFO.txt` 记录 SDK 与源码版本；验证记录位于 `artifacts/linux-validation.txt`。
测试运行需要 .NET 10 运行时，发布包已经自带。
`pcl-ce.desktop` 为桌面入口模板，安装时将程序加入 PATH 或修改 Exec 为绝对路径，并安装随附图标。

PCL.Core 与 Linux 界面遵循根目录 Apache-2.0；PCL.Launcher.Core 遵循包中的 `LICENCE-launcher`。
EasyTier、authlib-injector 和 Eclipse Temurin 为运行时按需下载的独立项目，分别遵循其上游许可证。
