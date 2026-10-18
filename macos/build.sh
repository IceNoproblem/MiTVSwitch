#!/bin/zsh
# SPDX-License-Identifier: GPL-3.0-only
set -e
cd "$(dirname "$0")"

APP="MiTVSwitch.app"
BIN="MiTVSwitch"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp Info.plist "$APP/Contents/Info.plist"

echo "编译中…"
xcrun swiftc -O main.swift -o "$APP/Contents/MacOS/$BIN" \
  -framework Cocoa -framework Carbon -framework ServiceManagement

codesign --force --deep --sign - "$APP" 2>/dev/null || true

echo "完成：$(pwd)/$APP"
ls -l "$APP/Contents/MacOS/$BIN"
