#!/usr/bin/env bash
# =============================================================================
# PCL-CE-Linux — build the single self-contained release AppImage
# -----------------------------------------------------------------------------
# Produces exactly one user-facing artifact:
#
#     artifacts/PCL-CE-Linux-<version>-x86_64.AppImage
#
# The script deliberately needs neither appimagetool nor docker and works
# without FUSE: the AppImage type-2 runtime is downloaded (pinned + SHA256
# verified) and concatenated with a SquashFS image created by mksquashfs.
#
# Layout of the AppDir:
#
#     AppRun                              -> sets up FONTCONFIG_FILE / XDG paths
#     pcl-ce.desktop, pcl-ce.png, .DirIcon <- AppImage root metadata
#     usr/bin/PCL-CE                      -> symlink into usr/lib/pcl-ce
#     usr/lib/pcl-ce/                     -> complete self-contained publish
#                                            output (apphost + runtime + native
#                                            libraries in one directory)
#     usr/share/{applications,mime,metainfo,icons,fonts,doc}
#     usr/etc/fonts/fonts.conf
#
# Compression: the pinned type2-runtime 20251108 only supports zlib and zstd
# (verified by running a minimal AppImage built with it), so SquashFS is built
# with zstd level 19 instead of xz.  `-repro-time` makes the image reproducible
# when the inputs are unchanged.
#
# Usage:
#     DOTNET=/path/to/dotnet ./scripts/build-appimage.sh
#
# Environment:
#     DOTNET            .NET SDK executable
#                       (default: <repo>/.tools/dotnet/dotnet, else `dotnet`)
#     PCL_BUILD_WORKDIR staging directory (default: artifacts/.build/appimage)
#     PCL_ASSET_CACHE   pinned runtime/font cache (default: artifacts/.assets)
#     PCL_KEEP_WORKDIR  set to 1 to keep the staging AppDir after success
#     PCL_BUNDLE_ADWAITA set to 0 to skip the optional Adwaita icon subset
#     SOURCE_DATE_EPOCH fixed timestamp used for reproducible SquashFS builds
# =============================================================================
set -euo pipefail

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_dir"

project="$repo_dir/PCL.Linux/PCL.Linux.csproj"
app_assembly="PCL-CE"
app_id="pcl-ce"
rid="linux-x64"
arch="x86_64"

version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$project" | head -n 1)"
[[ -n "$version" ]] || { echo "无法从 $project 读取 <Version>" >&2; exit 1; }

artifact="$repo_dir/artifacts/PCL-CE-Linux-$version-$arch.AppImage"
assets_dir="${PCL_ASSET_CACHE:-$repo_dir/artifacts/.assets}"
work_dir="${PCL_BUILD_WORKDIR:-$repo_dir/artifacts/.build/appimage}"

# Pinned AppImage type-2 runtime.  Do not change the URL without re-pinning the
# SHA256; the same hash is used for both the cache check and the download.
runtime_version="20251108"
runtime_url="https://github.com/AppImage/type2-runtime/releases/download/$runtime_version/runtime-x86_64"
runtime_sha256="2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"

# Pinned Noto Sans CJK SC fonts (commit of notofonts/noto-cjk, not the moving
# main branch) and their SIL OFL 1.1 license.
noto_commit="165c01b46ea533872e002e0785ff17e44f6d97d8"
noto_url_base="https://raw.githubusercontent.com/notofonts/noto-cjk/$noto_commit/Sans/OTF/SimplifiedChinese"
noto_regular_sha256="2c76254f6fc379fddfce0a7e84fb5385bb135d3e399294f6eeb6680d0365b74b"
noto_bold_sha256="b5f0d1a190a7f9b43c310a8850630af12553df32c4c050543f9059732d9b4c0a"
noto_license_url="https://raw.githubusercontent.com/notofonts/noto-cjk/$noto_commit/LICENSE"
noto_license_sha256="6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2"

# Build-host fallback fonts (Ubuntu/Debian fonts-noto-cjk package).  They are
# used when the pinned download is unavailable; the build log records this.
system_font_regular="/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc"
system_font_bold="/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
log() { printf '[build-appimage] %s\n' "$*"; }
warn() { printf '[build-appimage] 警告：%s\n' "$*" >&2; }
fail() { printf '[build-appimage] 错误：%s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || fail "缺少命令：$1"; }
sha256_of() { sha256sum "$1" | cut -d' ' -f1; }

# Download url -> dest, verifying an explicit SHA256.  Existing cache entries
# are re-verified on every build so a corrupted cache can never be packaged.
fetch_verified() {
    local url="$1" dest="$2" expected="$3" label="$4"
    local actual
    if [[ -f "$dest" ]]; then
        actual="$(sha256_of "$dest")"
        if [[ "$actual" == "$expected" ]]; then
            log "复用缓存 $label（SHA256 已校验）"
            return 0
        fi
        warn "缓存 $label 校验失败，重新下载"
        rm -f -- "$dest"
    fi
    mkdir -p -- "$(dirname -- "$dest")"
    local part="$dest.part"
    rm -f -- "$part"
    local ok=0
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --retry 3 --retry-delay 2 --connect-timeout 20 --max-time 600 \
            -o "$part" "$url" || ok=$?
    elif command -v wget >/dev/null 2>&1; then
        wget -q --tries=3 --timeout=30 -O "$part" "$url" || ok=$?
    else
        fail "需要 curl 或 wget 下载 $label"
    fi
    if (( ok != 0 )) || [[ ! -s "$part" ]]; then
        rm -f -- "$part"
        warn "下载 $label 失败（$url）"
        return 1
    fi
    actual="$(sha256_of "$part")"
    if [[ "$actual" != "$expected" ]]; then
        warn "下载的 $label SHA256 不匹配（期望 $expected，实际 $actual）"
        rm -f -- "$part"
        return 1
    fi
    mv -f -- "$part" "$dest"
    log "已下载 $label（SHA256 已校验）"
}

trap 'status=$?; if (( status != 0 )); then printf "[build-appimage] 构建失败（退出码 %s），工作目录保留：%s\n" "$status" "$work_dir" >&2; fi' EXIT

# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------
[[ -f "$project" ]] || fail "找不到项目文件 $project"
need mksquashfs
need sha256sum
need python3
need file
need install
need sed
need find
need touch
need readlink
need cat
need chmod
need stat
need timeout

mksquashfs -help-comp zstd >/dev/null 2>&1 \
    || fail "mksquashfs 不支持 zstd 压缩（需要 squashfs-tools 4.5+）"
command -v update-mime-database >/dev/null 2>&1 \
    || warn "构建机没有 update-mime-database：AppDir 内不会生成 mime.cache（桌面集成仍会注册 MIME）"

if [[ -n "${DOTNET:-}" ]]; then
    dotnet_cmd="$DOTNET"
elif [[ -x "$repo_dir/.tools/dotnet/dotnet" ]]; then
    dotnet_cmd="$repo_dir/.tools/dotnet/dotnet"
else
    dotnet_cmd="dotnet"
fi
[[ -x "$dotnet_cmd" ]] || command -v "$dotnet_cmd" >/dev/null 2>&1 \
    || fail "找不到 .NET SDK：$dotnet_cmd（可用 DOTNET=/path/to/dotnet 指定）"
sdk_version="$("$dotnet_cmd" --version)"
if (( ${sdk_version%%.*} < 11 )); then
    fail "构建需要 .NET 11 SDK（C# 15）；当前版本：$sdk_version"
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# Fixed timestamps make the SquashFS image reproducible for the same inputs.
if [[ -z "${SOURCE_DATE_EPOCH:-}" ]]; then
    if git -C "$repo_dir" rev-parse --git-dir >/dev/null 2>&1; then
        SOURCE_DATE_EPOCH="$(git -C "$repo_dir" log -1 --pretty=%ct)"
    else
        SOURCE_DATE_EPOCH=1767225600 # 2026-01-01T00:00:00Z
    fi
fi
[[ "$SOURCE_DATE_EPOCH" =~ ^[0-9]+$ ]] || fail "SOURCE_DATE_EPOCH 必须是整数"
export SOURCE_DATE_EPOCH
build_date="$(date -u -d "@$SOURCE_DATE_EPOCH" '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || echo "@$SOURCE_DATE_EPOCH")"
if git -C "$repo_dir" rev-parse --git-dir >/dev/null 2>&1; then
    source_revision="$(git -C "$repo_dir" describe --always --dirty 2>/dev/null || echo unknown)"
else
    source_revision="unknown（导出副本，无 .git）"
fi

mkdir -p -- "$repo_dir/artifacts" "$assets_dir" "$work_dir"
rm -rf -- "$work_dir/AppDir" "$work_dir/publish" "$work_dir/verify" \
    "$work_dir/runtime-check" "$work_dir/fonts-src" "$work_dir/fonts-out" "$work_dir/tmp"
mkdir -p -- "$work_dir/tmp"

log "版本 $version，目标 $rid，SDK $sdk_version"
log "工作目录 $work_dir"
log "资源缓存 $assets_dir"

# ---------------------------------------------------------------------------
# 1. AppImage runtime (pinned, SHA256 verified) + compression self-test
# ---------------------------------------------------------------------------
runtime_file="$assets_dir/runtime-x86_64-$runtime_version"
fetch_verified "$runtime_url" "$runtime_file" "$runtime_sha256" \
    "AppImage type2-runtime $runtime_version" \
    || fail "无法获取 AppImage 运行时（$runtime_url）；可手动下载后放到 $runtime_file"
chmod 0755 "$runtime_file"

log "实测 AppImage 运行时是否支持 zstd（xz 不被该运行时支持）…"
runtime_check="$work_dir/runtime-check"
mkdir -p -- "$runtime_check/AppDir/usr/bin"
cat > "$runtime_check/AppDir/AppRun" <<'EOF'
#!/bin/sh
[ "${1:-}" = "--version" ] || exit 2
echo RUNTIME-CHECK-OK
EOF
chmod 0755 "$runtime_check/AppDir/AppRun"
echo runtime-check > "$runtime_check/AppDir/usr/bin/marker"
mksquashfs "$runtime_check/AppDir" "$runtime_check/test.squashfs" \
    -root-owned -noappend -no-xattrs -no-progress \
    -comp zstd -Xcompression-level 19 >/dev/null 2>&1 \
    || fail "mksquashfs 生成 zstd 测试镜像失败"
cat "$runtime_file" "$runtime_check/test.squashfs" > "$runtime_check/test.AppImage"
chmod 0755 "$runtime_check/test.AppImage"
runtime_out=""
if ! runtime_out="$(APPIMAGE_EXTRACT_AND_RUN=1 TMPDIR="$work_dir/tmp" \
        "$runtime_check/test.AppImage" --version 2>&1)"; then
    printf '%s\n' "$runtime_out" >&2
    fail "AppImage 运行时无法执行 zstd 压缩的镜像（运行时与压缩方式不匹配）"
fi
[[ "$runtime_out" == *"RUNTIME-CHECK-OK"* ]] \
    || fail "AppImage 运行时自检失败：$runtime_out"
runtime_commit="$("$runtime_check/test.AppImage" --appimage-version 2>&1 \
    | sed 's|.*commit/||' || true)"
log "运行时自检通过：type2-runtime $runtime_version（commit ${runtime_commit:-unknown}）"

# ---------------------------------------------------------------------------
# 2. Publish the self-contained application into the work directory
# ---------------------------------------------------------------------------
publish_dir="$work_dir/publish"
log "dotnet publish（$rid，self-contained，单文件打包关闭）…"
"$dotnet_cmd" publish "$project" \
    -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -p:DebugType=None -p:DebugSymbols=false \
    -o "$publish_dir"

[[ -x "$publish_dir/$app_assembly" ]]           || fail "发布输出缺少可执行文件 $app_assembly"
[[ -f "$publish_dir/$app_assembly.dll" ]]       || fail "发布输出缺少 $app_assembly.dll"
[[ -f "$publish_dir/libcoreclr.so" ]]           || fail "发布输出不是自包含发布（缺少 libcoreclr.so）"
[[ -f "$publish_dir/$app_assembly.runtimeconfig.json" ]] || fail "发布输出缺少 runtimeconfig.json"
find "$publish_dir" -type f -name '*.pdb' -delete
rm -f -- "$publish_dir/pcl-ce.desktop"

# ---------------------------------------------------------------------------
# 3. Fonts: pinned Noto Sans CJK SC (+ fallback to build host fonts)
# ---------------------------------------------------------------------------
fonts_cache="$assets_dir/fonts"
mkdir -p -- "$fonts_cache"
downloaded_regular=""
downloaded_bold=""
if fetch_verified "$noto_url_base/NotoSansCJKsc-Regular.otf" \
        "$fonts_cache/NotoSansCJKsc-Regular.otf" "$noto_regular_sha256" \
        "Noto Sans CJK SC Regular"; then
    downloaded_regular="$fonts_cache/NotoSansCJKsc-Regular.otf"
fi
if fetch_verified "$noto_url_base/NotoSansCJKsc-Bold.otf" \
        "$fonts_cache/NotoSansCJKsc-Bold.otf" "$noto_bold_sha256" \
        "Noto Sans CJK SC Bold"; then
    downloaded_bold="$fonts_cache/NotoSansCJKsc-Bold.otf"
fi

font_origin=""
regular_src=""
bold_src=""
if [[ -n "$downloaded_regular" && -n "$downloaded_bold" ]]; then
    font_origin="下载 noto-cjk@$noto_commit（NotoSansCJKsc Regular/Bold）"
    regular_src="$downloaded_regular"
    bold_src="$downloaded_bold"
elif [[ -f "$system_font_regular" && -f "$system_font_bold" ]]; then
    warn "无法下载/校验固定版本的 Noto Sans CJK SC，改用构建机字体包：$system_font_regular"
    font_origin="构建机 /usr/share/fonts/opentype/noto/（fonts-noto-cjk）"
    regular_src="$system_font_regular"
    bold_src="$system_font_bold"
elif [[ -n "$downloaded_regular" ]]; then
    warn "Bold 字体下载失败，Bold 将由 Regular 代替（合成粗体）"
    font_origin="下载 noto-cjk@$noto_commit（仅 Regular）"
    regular_src="$downloaded_regular"
    bold_src="$downloaded_regular"
else
    fail "无法获得中文字体：固定下载失败，且构建机没有 fonts-noto-cjk（$system_font_regular）。请联网或安装 fonts-noto-cjk。"
fi

# Font license (SIL OFL 1.1)
font_license="$work_dir/LICENSE-NotoSansCJK.txt"
mkdir -p -- "$work_dir"
if ! fetch_verified "$noto_license_url" "$fonts_cache/LICENSE" "$noto_license_sha256" \
        "Noto CJK LICENSE"; then
    if [[ -f /usr/share/doc/fonts-noto-cjk/copyright ]]; then
        cp -f -- /usr/share/doc/fonts-noto-cjk/copyright "$font_license"
        warn "字体许可证改用构建机 /usr/share/doc/fonts-noto-cjk/copyright"
    else
        printf 'Noto Sans CJK is licensed under the SIL Open Font License 1.1.\n' > "$font_license"
    fi
else
    cp -f -- "$fonts_cache/LICENSE" "$font_license"
fi

# Subsetting: fonttools keeps the full East Asian coverage (CJK Unified
# Ideographs + Ext A, kana, Hangul, Latin, punctuation, compatibility forms)
# and drops the glyphs a Minecraft launcher never renders.
py_subset=0
fonttools_version=""
if python3 -c 'import fontTools' >/dev/null 2>&1; then
    py_subset=1
    fonttools_version="$(python3 -c 'import fontTools; print(fontTools.version)')"
else
    pip_target="$assets_dir/python"
    if python3 -m pip --version >/dev/null 2>&1; then
        log "fonttools 未安装，尝试 pip install --target $pip_target fonttools"
        if python3 -m pip install --quiet --disable-pip-version-check \
                --target "$pip_target" fonttools >/dev/null 2>&1; then
            export PYTHONPATH="$pip_target${PYTHONPATH:+:$PYTHONPATH}"
            if python3 -c 'import fontTools' >/dev/null 2>&1; then
                py_subset=1
                fonttools_version="$(python3 -c 'import fontTools; print(fontTools.version)')"
            fi
        fi
    fi
fi
(( py_subset == 1 )) || warn "fonttools 不可用：直接打包完整字体（体积更大）"

# Detect the font index of the "Noto Sans CJK SC" face inside a .ttc file.
detect_ttc_face() {
    python3 - "$1" <<'PY'
import sys
from fontTools.ttLib import TTCollection
path = sys.argv[1]
collection = TTCollection(path, lazy=True)
for index, font in enumerate(collection.fonts):
    family = font["name"].getDebugName(1) or ""
    if family.startswith("Noto Sans CJK SC"):
        print(index)
        break
PY
}

# Subset one font (OTF or a face of a TTC) and verify the produced font.
subset_font() {
    python3 - "$1" "$2" "$3" <<'PY'
import os
import sys
from fontTools import subset
from fontTools.ttLib import TTFont

source, destination, face = sys.argv[1], sys.argv[2], sys.argv[3]
# East Asian text coverage: Latin/Greek/Cyrillic, punctuation, symbols,
# kana, Hangul syllables, CJK Unified Ideographs (+ Ext A/B MP), fullwidth
# and compatibility forms.  This is a subset by script, not by UI string, so
# user names, mod names, chat logs and translated text still render.
ranges = [
    (0x0000, 0x024F), (0x0250, 0x02FF), (0x0300, 0x036F), (0x0370, 0x03FF),
    (0x0400, 0x04FF), (0x1E00, 0x1EFF), (0x2000, 0x206F), (0x2070, 0x209F),
    (0x20A0, 0x20CF), (0x2100, 0x214F), (0x2150, 0x218F), (0x2190, 0x21FF),
    (0x2200, 0x22FF), (0x2300, 0x23FF), (0x2460, 0x24FF), (0x2500, 0x257F),
    (0x2580, 0x259F), (0x25A0, 0x25FF), (0x2600, 0x26FF), (0x2700, 0x27BF),
    (0x2E80, 0x2EFF), (0x2F00, 0x2FDF), (0x2FF0, 0x2FFF), (0x3000, 0x303F),
    (0x3040, 0x309F), (0x30A0, 0x30FF), (0x3100, 0x312F), (0x3130, 0x318F),
    (0x3190, 0x319F), (0x31A0, 0x31BF), (0x31C0, 0x31EF), (0x31F0, 0x31FF),
    (0x3200, 0x32FF), (0x3300, 0x33FF), (0x3400, 0x4DBF), (0x4DC0, 0x4DFF),
    (0x4E00, 0x9FFF), (0xA000, 0xA4CF), (0xA960, 0xA97F), (0xAC00, 0xD7A3),
    (0xD7B0, 0xD7FF), (0xE000, 0xF8FF), (0xF900, 0xFAFF), (0xFE00, 0xFE0F),
    (0xFE10, 0xFE1F), (0xFE30, 0xFE4F), (0xFE50, 0xFE6F), (0xFE70, 0xFEFF),
    (0xFF00, 0xFFEF), (0xFFF0, 0xFFFF),
]

options = subset.Options()
if face:
    options.font_number = int(face)
options.name_IDs = ["*"]
options.name_legacy = True
options.name_languages = ["*"]
options.layout_features = ["*"]
options.hinting = True
options.notdef_outline = True
options.recommended_glyphs = True
options.symbol_cmap = True
options.legacy_cmap = True
options.glyph_names = True
options.drop_tables = []

font = subset.load_font(source, options)
subsetter = subset.Subsetter(options=options)
subsetter.populate(unicodes=[code for low, high in ranges for code in range(low, high + 1)])
subsetter.subset(font)
subset.save_font(font, destination, options)
font.close()

# Verify: the file re-opens, keeps the expected family name and CJK coverage.
check = TTFont(destination, lazy=True)
family = check["name"].getDebugName(1) or ""
cmap = check.getBestCmap()
check.close()
if not family.startswith("Noto Sans CJK SC"):
    raise SystemExit(f"subset 后的字体家族名异常：{family!r}")
if len(cmap) < 30000:
    raise SystemExit(f"subset 后的字符覆盖过小：{len(cmap)}")
missing = [c for c in "中文测试我的世界启动器游戏模组整合包下载设置" if ord(c) not in cmap]
if missing:
    raise SystemExit(f"subset 缺少常用中文字形：{''.join(missing)}")
print(f"{os.path.basename(destination)}: family={family!r} cmap={len(cmap)} glyphs")
PY
}

fonts_out="$work_dir/fonts-out"
mkdir -p -- "$fonts_out"
font_mode="full（未使用 fonttools）"
regular_number=""
bold_number=""
if [[ "$regular_src" == *.ttc ]]; then
    regular_number="$(detect_ttc_face "$regular_src" || true)"
    [[ -n "$regular_number" ]] || warn "无法在 $regular_src 中定位 Noto Sans CJK SC face，将打包完整 TTC"
fi
if [[ "$bold_src" == *.ttc ]]; then
    bold_number="$(detect_ttc_face "$bold_src" || true)"
    [[ -n "$bold_number" ]] || warn "无法在 $bold_src 中定位 Noto Sans CJK SC face，将打包完整 TTC"
fi

subset_ok=0
if (( py_subset == 1 )) \
    && [[ "$regular_src" != *.ttc || -n "$regular_number" ]] \
    && [[ "$bold_src" != *.ttc || -n "$bold_number" ]]; then
    log "使用 fonttools $fonttools_version 子集化中文字体（约 1 分钟）…"
    if subset_font "$regular_src" "$fonts_out/NotoSansCJKsc-Regular.otf" "$regular_number"; then
        if subset_font "$bold_src" "$fonts_out/NotoSansCJKsc-Bold.otf" "$bold_number"; then
            subset_ok=1
            font_mode="subset（fonttools $fonttools_version，保留东亚字符全集）"
        fi
    fi
fi
if (( subset_ok == 0 )); then
    warn "字体子集化不可用或失败，打包完整字体"
    rm -f -- "$fonts_out"/*
    case "$regular_src" in
        *.ttc) cp -f -- "$regular_src" "$fonts_out/NotoSansCJKsc-Regular.ttc" ;;
        *)     cp -f -- "$regular_src" "$fonts_out/NotoSansCJKsc-Regular.otf" ;;
    esac
    if [[ "$bold_src" != "$regular_src" ]]; then
        case "$bold_src" in
            *.ttc) cp -f -- "$bold_src" "$fonts_out/NotoSansCJKsc-Bold.ttc" ;;
            *)     cp -f -- "$bold_src" "$fonts_out/NotoSansCJKsc-Bold.otf" ;;
        esac
    else
        warn "Regular 与 Bold 使用同一字体文件，粗体由渲染器合成"
    fi
fi
font_mode="$font_mode；来源：$font_origin"
log "中文字体：$font_mode"

# ---------------------------------------------------------------------------
# 4. Assemble the AppDir
# ---------------------------------------------------------------------------
log "组装 AppDir…"
appdir="$work_dir/AppDir"
mkdir -p -- "$appdir"
install -d "$appdir/usr/lib/pcl-ce" "$appdir/usr/bin" "$appdir/usr/share/applications" \
    "$appdir/usr/share/mime/packages" "$appdir/usr/share/metainfo" \
    "$appdir/usr/share/icons" "$appdir/usr/share/fonts/opentype/noto-cjk" \
    "$appdir/usr/etc/fonts" "$appdir/usr/share/doc/pcl-ce"

# Complete publish output, all runtime files in one directory (apphost lookup).
cp -a -- "$publish_dir/." "$appdir/usr/lib/pcl-ce/"
# Convenience symlink; AppRun execs the real path in usr/lib/pcl-ce.
ln -sfn ../lib/pcl-ce/PCL-CE "$appdir/usr/bin/PCL-CE"

# AppRun / root desktop metadata / root icon
install -m 0755 "$repo_dir/packaging/AppRun" "$appdir/AppRun"
install -m 0644 "$repo_dir/packaging/pcl-ce.desktop" "$appdir/pcl-ce.desktop"
install -m 0644 "$repo_dir/packaging/icons/hicolor/256x256/apps/pcl-ce.png" "$appdir/pcl-ce.png"
ln -sfn pcl-ce.png "$appdir/.DirIcon"

# Desktop file, MIME definitions, AppStream metadata
install -m 0644 "$repo_dir/packaging/pcl-ce.desktop" "$appdir/usr/share/applications/pcl-ce.desktop"
install -m 0644 "$repo_dir/packaging/pcl-ce.xml" "$appdir/usr/share/mime/packages/pcl-ce.xml"
install -m 0644 "$repo_dir/packaging/pcl-ce.metainfo.xml" "$appdir/usr/share/metainfo/pcl-ce.metainfo.xml"

# hicolor icon theme 16..512 (generated and committed under packaging/icons)
for size in 16 24 32 48 64 128 256 512; do
    icon_src="$repo_dir/packaging/icons/hicolor/${size}x${size}/apps/pcl-ce.png"
    [[ -f "$icon_src" ]] || fail "缺少图标 $icon_src（可运行 python3 packaging/icons/generate-icons.py 重新生成）"
    install -Dm644 "$icon_src" "$appdir/usr/share/icons/hicolor/${size}x${size}/apps/pcl-ce.png"
done
{
    echo '[Icon Theme]'
    echo 'Name=Hicolor'
    echo 'Comment=Fallback icon theme bundled with PCL-CE-Linux'
    echo 'Hidden=true'
    echo 'Directories=16x16/apps,24x24/apps,32x32/apps,48x48/apps,64x64/apps,128x128/apps,256x256/apps,512x512/apps'
    for size in 16 24 32 48 64 128 256 512; do
        echo
        echo "[${size}x${size}/apps]"
        echo 'Context=Applications'
        echo "Size=$size"
        echo 'Type=Fixed'
    done
} > "$appdir/usr/share/icons/hicolor/index.theme"

# Optional tiny Adwaita subset for GTK dialog icons.  Avalonia 11.3 uses the
# X11/portal backend, but GTK-based fallbacks (e.g. portal dialogs started from
# the same session) then still find a theme even on minimal hosts.
adwaita_info="未包含（PCL_BUNDLE_ADWAITA=0 或构建机无 Adwaita）"
adwaita_license=""
if [[ "${PCL_BUNDLE_ADWAITA:-1}" != 0 && -d /usr/share/icons/Adwaita ]]; then
    adwaita_src="/usr/share/icons/Adwaita"
    adwaita_dest="$appdir/usr/share/icons/Adwaita"
    install -d "$adwaita_dest/symbolic/status" "$adwaita_dest/symbolic/actions"
    adwaita_count=0
    while IFS= read -r -d '' svg; do
        rel="${svg#"$adwaita_src"/}"
        install -Dm644 "$svg" "$adwaita_dest/$rel"
        adwaita_count=$((adwaita_count + 1))
    done < <(find "$adwaita_src/symbolic/status" -maxdepth 1 -name 'dialog-*-symbolic.svg' -print0 2>/dev/null)
    for name in process-stop-symbolic.svg go-next-symbolic.svg go-previous-symbolic.svg \
                window-close-symbolic.svg window-minimize-symbolic.svg \
                window-maximize-symbolic.svg window-restore-symbolic.svg; do
        for source_dir in "$adwaita_src/symbolic/actions" "$adwaita_src/symbolic/ui"; do
            if [[ -f "$source_dir/$name" ]]; then
                rel="${source_dir#"$adwaita_src"/}"
                install -Dm644 "$source_dir/$name" "$adwaita_dest/$rel/$name"
                adwaita_count=$((adwaita_count + 1))
                break
            fi
        done
    done
    if (( adwaita_count > 0 )); then
        adwaita_dirs=()
        for rel in symbolic/status symbolic/actions symbolic/ui; do
            if [[ -d "$adwaita_dest/$rel" ]] && [[ -n "$(ls -A "$adwaita_dest/$rel" 2>/dev/null)" ]]; then
                adwaita_dirs+=("$rel")
            fi
        done
        saved_ifs="$IFS"; IFS=','; adwaita_directories="${adwaita_dirs[*]}"; IFS="$saved_ifs"
        {
            echo '[Icon Theme]'
            echo 'Name=Adwaita'
            echo 'Comment=Minimal Adwaita subset bundled with PCL-CE-Linux'
            echo 'Inherits=hicolor'
            echo "Directories=$adwaita_directories"
            for rel in "${adwaita_dirs[@]}"; do
                echo
                echo "[$rel]"
                if [[ "$rel" == symbol* ]]; then
                    echo 'Context=Status'
                    echo 'Size=16'
                    echo 'Type=Scalable'
                    echo 'MinSize=8'
                    echo 'MaxSize=512'
                fi
            done
        } > "$adwaita_dest/index.theme"
        adwaita_info="已包含 $adwaita_count 个 symbolic 图标（dialog/窗口/导航，构建机 Adwaita）"
        if [[ -f /usr/share/doc/adwaita-icon-theme/copyright ]]; then
            adwaita_license="$work_dir/LICENSE-adwaita-icon-theme.txt"
            cp -f -- /usr/share/doc/adwaita-icon-theme/copyright "$adwaita_license"
        fi
    else
        rm -rf -- "$adwaita_dest"
    fi
fi

# Fonts + fontconfig
install -m 0644 "$repo_dir/packaging/fonts.conf" "$appdir/usr/etc/fonts/fonts.conf"
for font_file in "$fonts_out"/*; do
    install -m 0644 "$font_file" "$appdir/usr/share/fonts/opentype/noto-cjk/$(basename "$font_file")"
done

# Documentation and licenses
install -m 0644 "$repo_dir/LICENSE" "$appdir/usr/share/doc/pcl-ce/LICENSE"
if [[ -f "$repo_dir/PCL.Launcher.Core/LICENCE" ]]; then
    install -m 0644 "$repo_dir/PCL.Launcher.Core/LICENCE" "$appdir/usr/share/doc/pcl-ce/LICENCE-launcher"
fi
install -m 0644 "$repo_dir/PCL.Linux/README.md" "$appdir/usr/share/doc/pcl-ce/README.md"
install -m 0644 "$font_license" "$appdir/usr/share/doc/pcl-ce/LICENSE-NotoSansCJK.txt"
if [[ -n "$adwaita_license" ]]; then
    install -m 0644 "$adwaita_license" "$appdir/usr/share/doc/pcl-ce/LICENSE-adwaita-icon-theme.txt"
fi

# Build metadata (inside the AppImage, for support and reproducibility checks)
cat > "$appdir/usr/share/doc/pcl-ce/BUILD-INFO.txt" <<EOF
PCL-CE-Linux $version (AppImage)
Target: $rid / $arch, self-contained .NET 10（目标机无需 .NET / Mono / Wine）
Build date: $build_date
SOURCE_DATE_EPOCH: $SOURCE_DATE_EPOCH
Source: $source_revision
SDK: $sdk_version
AppImage runtime: type2-runtime $runtime_version (sha256 $runtime_sha256)
SquashFS: zstd level 19, -repro-time $SOURCE_DATE_EPOCH（xz 不被该运行时支持）
Font: $font_mode
Icon theme: hicolor 16,24,32,48,64,128,256,512（packaging/icons，由 Logo-CE.png 生成）
Adwaita fallback: $adwaita_info
Licenses: LICENSE (Apache-2.0), LICENCE-launcher (PCL.Launcher.Core),
          LICENSE-NotoSansCJK.txt (SIL OFL 1.1)
EOF

# MIME / desktop / icon caches so AppDir works through XDG_DATA_DIRS without
# touching the host.  Timestamps are normalised first so the generated caches
# stay reproducible.
find "$appdir" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
if command -v update-mime-database >/dev/null 2>&1; then
    update-mime-database "$appdir/usr/share/mime" >/dev/null 2>&1 \
        || warn "update-mime-database 生成 mime.cache 失败（忽略）"
fi
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$appdir/usr/share/applications" >/dev/null 2>&1 \
        || warn "update-desktop-database 失败（忽略）"
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -q "$appdir/usr/share/icons/hicolor" >/dev/null 2>&1 \
        || warn "gtk-update-icon-cache 失败（忽略）"
fi

# ---------------------------------------------------------------------------
# 5. Verify the AppDir (run it from a normal directory, without the runtime)
# ---------------------------------------------------------------------------
log "校验 AppDir…"
clean_home="$work_dir/verify/home"
mkdir -p -- "$clean_home/.cache" "$clean_home/.config" "$clean_home/.local/share" \
    "$clean_home/.local/state" "$clean_home/.run"
chmod 700 "$clean_home/.run"

clean_run() {
    env -i \
        HOME="$clean_home" \
        PATH="/usr/local/bin:/usr/bin:/bin" \
        TMPDIR="$work_dir/tmp" \
        XDG_CACHE_HOME="$clean_home/.cache" \
        XDG_CONFIG_HOME="$clean_home/.config" \
        XDG_DATA_HOME="$clean_home/.local/share" \
        XDG_STATE_HOME="$clean_home/.local/state" \
        XDG_RUNTIME_DIR="$clean_home/.run" \
        "$@"
}

required_appdir_files=(
    AppRun
    pcl-ce.desktop
    pcl-ce.png
    usr/bin/PCL-CE
    "usr/lib/pcl-ce/$app_assembly"
    "usr/lib/pcl-ce/$app_assembly.dll"
    "usr/lib/pcl-ce/$app_assembly.runtimeconfig.json"
    usr/lib/pcl-ce/libcoreclr.so
    usr/share/applications/pcl-ce.desktop
    usr/share/mime/packages/pcl-ce.xml
    usr/share/metainfo/pcl-ce.metainfo.xml
    usr/share/icons/hicolor/256x256/apps/pcl-ce.png
    usr/etc/fonts/fonts.conf
    usr/share/doc/pcl-ce/LICENSE
    usr/share/doc/pcl-ce/README.md
)
for rel in "${required_appdir_files[@]}"; do
    [[ -e "$appdir/$rel" ]] || fail "AppDir 缺少 $rel"
done
[[ -x "$appdir/AppRun" ]] || fail "AppDir/AppRun 不可执行"
[[ "$(cat "$appdir/pcl-ce.png" | wc -c)" -gt 0 ]] || fail "AppDir/pcl-ce.png 为空"
LC_ALL=C file "$appdir/usr/lib/pcl-ce/$app_assembly" | grep -q 'ELF 64-bit' \
    || fail "AppDir 中的 $app_assembly 不是 ELF 64 位可执行文件"
grep -q 'FONTCONFIG_FILE' "$appdir/AppRun" \
    || fail "AppRun 未导出 FONTCONFIG_FILE"
compgen -G "$appdir/usr/share/fonts/opentype/noto-cjk/*" >/dev/null \
    || fail "AppDir 未包含中文字体"

appdir_version_output=""
if ! appdir_version_output="$(clean_run "$appdir/AppRun" --version 2>&1)"; then
    printf '%s\n' "$appdir_version_output" >&2
    fail "从解包的 AppDir 运行 AppRun --version 失败"
fi
[[ "$appdir_version_output" == *"PCL-CE-Linux"*"$version"* ]] \
    || fail "AppDir 版本输出异常：$appdir_version_output"
log "解包 AppDir 运行正常：$appdir_version_output"

if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "$appdir/pcl-ce.desktop" \
        || fail "desktop-file-validate 未通过（AppDir/pcl-ce.desktop）"
    desktop-file-validate "$appdir/usr/share/applications/pcl-ce.desktop" \
        || fail "desktop-file-validate 未通过（usr/share/applications/pcl-ce.desktop）"
    log "desktop-file-validate 通过"
else
    warn "构建机没有 desktop-file-validate，跳过桌面文件校验"
fi

if command -v fc-match >/dev/null 2>&1; then
    font_match="$(FONTCONFIG_FILE="$appdir/usr/etc/fonts/fonts.conf" \
        HOME="$clean_home" XDG_CACHE_HOME="$clean_home/.cache" \
        fc-match -f '%{file}' 'Noto Sans CJK SC' 2>/dev/null || true)"
    case "$font_match" in
        "$appdir"/*) log "fontconfig 使用 AppDir 内置字体：${font_match#"$appdir/"}" ;;
        *) fail "fontconfig 未使用 AppDir 内置中文字体（得到：${font_match:-空}）" ;;
    esac
    [[ -d "$clean_home/.cache/pcl-ce/fontconfig" ]] \
        || warn "fontconfig 缓存目录未出现在 XDG_CACHE_HOME/pcl-ce/fontconfig（可能是系统缓存已存在）"
else
    warn "构建机没有 fc-match，跳过后备字体检查"
fi

if command -v update-mime-database >/dev/null 2>&1; then
    [[ -f "$appdir/usr/share/mime/mime.cache" ]] \
        || warn "AppDir 中没有生成 mime.cache（update-mime-database 失败）"
fi

if command -v appstreamcli >/dev/null 2>&1; then
    if appstreamcli validate --no-net "$appdir/usr/share/metainfo/pcl-ce.metainfo.xml" >/dev/null 2>&1; then
        log "AppStream 元数据校验通过"
    else
        warn "appstreamcli validate 未通过（不影响构建；输出如下）"
        appstreamcli validate --no-net "$appdir/usr/share/metainfo/pcl-ce.metainfo.xml" >&2 || true
    fi
fi

# ---------------------------------------------------------------------------
# 6. SquashFS + runtime concatenation
# ---------------------------------------------------------------------------
squashfs="$work_dir/pcl-ce.squashfs"
rm -f -- "$squashfs"
log "生成 SquashFS（zstd level 19，可复现时间戳）…"
# mksquashfs >= 4.7 reads SOURCE_DATE_EPOCH itself and refuses to combine it
# with -repro-time, so the environment variable is unset for this call and the
# timestamp is passed explicitly.  Older versions without -repro-time still
# build (without reproducible timestamps) and are reported as a warning.
if mksquashfs -help-all 2>/dev/null | grep -q -- '-repro-time'; then
    env -u SOURCE_DATE_EPOCH mksquashfs "$appdir" "$squashfs" \
        -root-owned -noappend -no-xattrs -no-progress \
        -comp zstd -Xcompression-level 19 \
        -repro-time "$SOURCE_DATE_EPOCH"
else
    warn "mksquashfs 不支持 -repro-time：镜像时间戳不可复现（输入内容仍一致）"
    env -u SOURCE_DATE_EPOCH mksquashfs "$appdir" "$squashfs" \
        -root-owned -noappend -no-xattrs -no-progress \
        -comp zstd -Xcompression-level 19
fi

rm -f -- "$artifact.tmp"
cat "$runtime_file" "$squashfs" > "$artifact.tmp"
chmod 0755 "$artifact.tmp"
mv -f -- "$artifact.tmp" "$artifact"
log "已生成 AppImage：${artifact#"$repo_dir/"}（$(stat -c '%s' "$artifact") 字节）"

# ---------------------------------------------------------------------------
# 7. Verify the final AppImage
# ---------------------------------------------------------------------------
log "校验 AppImage…"
[[ -x "$artifact" ]] || fail "AppImage 不可执行"
elf_magic="$(head -c 4 "$artifact" | od -An -tx1 | tr -d ' \n')"
[[ "$elf_magic" == "7f454c46" ]] || fail "AppImage 不是 ELF 文件（magic=$elf_magic）"
LC_ALL=C file "$artifact" | grep -q 'ELF 64-bit' || fail "AppImage file(1) 未识别为 ELF 64 位"

offset="$("$artifact" --appimage-offset 2>/dev/null || true)"
[[ "$offset" =~ ^[0-9]+$ ]] || fail "--appimage-offset 未返回有效偏移：${offset:-空}"
(( offset > 0 )) || fail "--appimage-offset 返回 0"
runtime_size="$(stat -c '%s' "$runtime_file")"
(( offset == runtime_size )) || warn "AppImage 偏移 $offset 与运行时大小 $runtime_size 不一致"
log "AppImage type-2 偏移正确：$offset"

# Extract and check the payload without FUSE.
verify_extract="$work_dir/verify/extract"
rm -rf -- "$verify_extract"
mkdir -p -- "$verify_extract"
if ! ( cd "$verify_extract" && "$artifact" --appimage-extract >/dev/null 2>&1 ); then
    fail "--appimage-extract 解包失败"
fi
extracted="$verify_extract/squashfs-root"
[[ -x "$extracted/AppRun" ]] || fail "解包结果缺少可执行 AppRun"
for rel in "${required_appdir_files[@]}"; do
    [[ -e "$extracted/$rel" ]] || fail "解包结果缺少 $rel"
done
[[ -f "$extracted/usr/share/mime/mime.cache" ]] \
    || warn "解包结果中没有 mime.cache"
extracted_output=""
if ! extracted_output="$(clean_run "$extracted/AppRun" --version 2>&1)"; then
    printf '%s\n' "$extracted_output" >&2
    fail "从 --appimage-extract 的 AppDir 运行 AppRun --version 失败"
fi
[[ "$extracted_output" == *"PCL-CE-Linux"*"$version"* ]] \
    || fail "解包 AppDir 版本输出异常：$extracted_output"
log "--appimage-extract 解包并运行通过：$extracted_output"

# The required no-FUSE path: extract to a temp dir, run, clean up.
help_output=""
if ! help_output="$(clean_run env APPIMAGE_EXTRACT_AND_RUN=1 \
        "$artifact" --help 2>&1)"; then
    printf '%s\n' "$help_output" >&2
    fail "APPIMAGE_EXTRACT_AND_RUN=1 $artifact --help 执行失败"
fi
[[ "$help_output" == *"PCL-CE-Linux"* ]] || fail "--help 未输出启动器名称"
[[ "$help_output" == *"--diagnose"* ]]   || fail "--help 未输出启动器用法（缺少 --diagnose）"
[[ "$help_output" == *"--register-desktop"* ]] || fail "--help 未输出桌面集成选项"
if [[ "$help_output" == *"Fontconfig error"* ]]; then
    printf '%s\n' "$help_output" >&2
    fail "字体配置错误（Fontconfig error）"
fi
log "APPIMAGE_EXTRACT_AND_RUN=1 --help 通过（无需 FUSE，无需系统 .NET）"

# Plain FUSE execution (best effort; the AppImage must also work without FUSE).
if [[ -r /dev/fuse ]] && command -v fusermount3 >/dev/null 2>&1; then
    fuse_output=""
    if fuse_output="$(clean_run timeout 90 "$artifact" --version 2>&1)"; then
        [[ "$fuse_output" == *"PCL-CE-Linux"*"$version"* ]] \
            || warn "FUSE 运行输出异常：$fuse_output"
        log "FUSE 直接运行通过：$fuse_output"
    else
        warn "FUSE 直接运行失败（AppImage 仍可通过 APPIMAGE_EXTRACT_AND_RUN=1 运行）"
    fi
else
    log "构建机没有可用的 FUSE，跳过 FUSE 直接运行检查"
fi

artifact_sha256="$(sha256_of "$artifact")"
artifact_size="$(stat -c '%s' "$artifact")"

# ---------------------------------------------------------------------------
# 8. Remove stale build artifacts: only the final AppImage stays
# ---------------------------------------------------------------------------
shopt -s nullglob
for stale in \
    "$repo_dir/artifacts/pcl-ce-linux-x64" \
    "$repo_dir/artifacts"/PCL-CE-linux-x64.tar.gz \
    "$repo_dir/artifacts"/PCL-CE-linux-x64.tar.gz.sha256 \
    "$repo_dir/artifacts"/PCL-CE-linux-*.tar.gz* \
    "$repo_dir/artifacts"/PCL-CE-Linux-*.AppImage; do
    [[ -e "$stale" ]] || continue
    [[ "$(readlink -f -- "$stale")" == "$(readlink -f -- "$artifact")" ]] && continue
    rm -rf -- "$stale"
    log "已删除旧构建产物：${stale#"$repo_dir/"}"
done
shopt -u nullglob

# Keep the pinned assets cache, drop the staging directory on success.
if [[ "${PCL_KEEP_WORKDIR:-0}" == 1 ]]; then
    log "保留工作目录：$work_dir"
else
    rm -rf -- "$work_dir"
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
log "构建完成"
log "  产物：${artifact#"$repo_dir/"}"
log "  大小：$artifact_size 字节（$(awk -v b="$artifact_size" 'BEGIN { printf "%.1f MiB", b/1048576 }')）"
log "  SHA256：$artifact_sha256"
log "  字体：$font_mode"
log "  图标：hicolor 16-512；$adwaita_info"
log "  验证：ELF / --appimage-offset / --appimage-extract / AppRun --version / --help（无 FUSE）"
log "  artifacts/ 目录中只保留该 AppImage（其余旧 tar.gz/AppImage/发布目录已删除）"
