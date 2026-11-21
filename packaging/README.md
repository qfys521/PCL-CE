# packaging/ — AppImage 打包资产

本目录是 `scripts/build-appimage.sh` 的输入，全部为提交到仓库的静态资产。
构建脚本不修改这里，只把它们复制进 AppDir。

| 文件 | 作用 |
| --- | --- |
| `AppRun` | AppImage 入口。导出 `FONTCONFIG_FILE="$APPDIR/usr/etc/fonts/fonts.conf"`，把 `$APPDIR/usr/share` 前置到 `XDG_DATA_DIRS`，把 `$APPDIR/usr/bin` 加入 `PATH`，最后 exec `usr/lib/pcl-ce/PCL-CE`。 |
| `pcl-ce.desktop` | 桌面入口模板（`Exec=PCL-CE %U`、`Icon=pcl-ce`、`Categories=Game;Utility;`），同时用于 AppDir 根目录与 `usr/share/applications/`。MIME 关联与 `x-scheme-handler/pcl-ce` 协议在此声明。 |
| `pcl-ce.xml` | shared-mime-info 定义：`application/x-mrpack`（`*.mrpack`）与 `application/x-minecraft-package`（`*.mcworld` `*.mcpack` `*.mctemplate` `*.mcaddon`）。 |
| `pcl-ce.metainfo.xml` | AppStream 元数据；组件 ID 为 `io.github.pcl_community.PCL_CE`（反向域名，`launchable` 指向 `pcl-ce.desktop`），项目许可证 Apache-2.0。 |
| `fonts.conf` | 内置中文字体 + 系统字体目录的 fontconfig 配置。`<dir prefix="relative">` 指向 AppDir 内的 `usr/share/fonts`（挂载点任意），`<cachedir prefix="xdg">pcl-ce/fontconfig</cachedir>` 把缓存放到 `$XDG_CACHE_HOME/pcl-ce/fontconfig`，绝不写入 AppImage 或 `/tmp`。 |
| `icons/hicolor/<N>x<N>/apps/pcl-ce.png` | 16/24/32/48/64/128/256/512 像素图标，由 `Plain Craft Launcher 2/Images/Heads/Logo-CE.png` 用 Pillow 生成并提交，构建时无需 Pillow。 |
| `icons/generate-icons.py` | 重新生成上述图标的脚本（仅在图标源变化时需要）。 |

## 构建

在仓库副本中（不要在共享工作区直接构建）：

```sh
rsync -a --exclude bin --exclude obj --exclude artifacts --exclude .tools \
      --exclude .git --exclude .notes "$REPO/" /tmp/pcl-build-appimage/
cd /tmp/pcl-build-appimage
DOTNET=/path/to/PCL-CE/.tools/dotnet/dotnet ./scripts/build-appimage.sh
```

输出只有 `artifacts/PCL-CE-Linux-1.0.0-x86_64.AppImage`（旧 tar.gz / AppImage /
发布目录会被清理）。固定下载的运行时与字体缓存在 `artifacts/.assets/`（隐藏目录，
不是发布产物）。构建需要网络下载 AppImage runtime 与 Noto Sans CJK（失败时回退到
构建机 `fonts-noto-cjk`），并验证固定的 SHA256。

### 运行时与压缩

`scripts/build-appimage.sh` 不需要 `appimagetool`、docker 或 FUSE：

1. 下载固定的 AppImage type-2 runtime（`type2-runtime 20251108`，SHA256 校验）；
2. 用 `mksquashfs` 生成 SquashFS；
3. `cat runtime squashfs > PCL-CE-Linux-1.0.0-x86_64.AppImage` 并 `chmod +x`。

该 runtime 实测只支持 zlib / zstd（xz 会报 `Squashfs image uses xz
compression, this version supports only zlib, zstd`），因此使用 `-comp zstd
-Xcompression-level 19`；脚本会先用一个最小 AppImage 验证 zstd 可运行，再构建正式
镜像。`-repro-time "$SOURCE_DATE_EPOCH"` 保证输入不变时镜像可复现。

### 构建机依赖

`dotnet` 11 SDK、`mksquashfs`（含 zstd）、`xz`/`zstd`、`python3`（可选
`fonttools`，缺失时自动 `pip install --target artifacts/.assets/python`，再缺失则
打包完整字体）、`curl` 或 `wget`、`file`、`desktop-file-validate`、
`update-mime-database`、`update-desktop-database`、`gtk-update-icon-cache`。

## 许可

- `icons/**` 来自 PCL-CE 项目素材，遵循 Apache-2.0（仓库根 `LICENSE`）。
- AppImage 内置 Noto Sans CJK SC，遵循 SIL OFL 1.1，许可文本随包安装到
  `usr/share/doc/pcl-ce/LICENSE-NotoSansCJK.txt`。
- 可选的内置 Adwaita 图标子集来自构建机 `adwaita-icon-theme`，其许可文本随包安装到
  `usr/share/doc/pcl-ce/LICENSE-adwaita-icon-theme.txt`；构建机没有 Adwaita 时自动跳过
  （Avalonia 11.3 使用 X11/portal，不依赖 GTK 主题，因此该子集只是 GTK 回退对话框的保险）。
