#!/bin/zsh
# SPDX-License-Identifier: GPL-3.0-only
set -e
cd "$(dirname "$0")"

APP="MiTVSwitch.app"
BIN="MiTVSwitch"

# 产物放到 out.noindex/ —— 以 .noindex 结尾的目录 Spotlight 永不索引。
# 否则编译出来的 .app 会被 Spotlight / Launchpad 索引，跟 /Applications 里那份
# 一起显示成「两个 MiTVSwitch」，让人以为装重了（2026-10-22 冰哥就撞上了）。
OUT="out.noindex"
mkdir -p "$OUT"

rm -rf "$OUT/$APP"
mkdir -p "$OUT/$APP/Contents/MacOS" "$OUT/$APP/Contents/Resources"
cp Info.plist "$OUT/$APP/Contents/Info.plist"

echo "编译中…"
xcrun swiftc -O main.swift Panel.swift Display.swift Usage.swift \
  -o "$OUT/$APP/Contents/MacOS/$BIN" \
  -framework Cocoa -framework Carbon -framework ServiceManagement -framework IOKit

codesign --force --deep --sign - "$OUT/$APP" 2>/dev/null || true

echo "完成：$(pwd)/$OUT/$APP"
echo "安装：cp -R \"$OUT/$APP\" /Applications/"
ls -l "$OUT/$APP/Contents/MacOS/$BIN"
