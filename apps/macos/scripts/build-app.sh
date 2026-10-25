#!/usr/bin/env bash
# Builds SlothBoss.app (release, universal arm64 + x86_64 when possible), ad-hoc signs it and
# zips it to apps/macos/build/SlothBoss-macOS.zip.
#
# Environment:
#   SLOTHBOSS_VERSION  marketing version (default 0.1.0)
#   SLOTHBOSS_BUILD    build number (default 1)
#   UNIVERSAL=0        build for the host architecture only
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
macos="$(cd "$here/.." && pwd)"
cd "$macos"

version="${SLOTHBOSS_VERSION:-0.1.0}"
build_number="${SLOTHBOSS_BUILD:-1}"
out="$macos/build"
app="$out/SlothBoss.app"

arch_args=""
if [ "${UNIVERSAL:-1}" = "1" ]; then
  arch_args="--arch arm64 --arch x86_64"
fi

echo "==> swift build -c release $arch_args"
# shellcheck disable=SC2086
if ! swift build -c release --product SlothBoss $arch_args; then
  if [ -n "$arch_args" ]; then
    echo "warning: universal build failed; building for the host architecture only" >&2
    arch_args=""
    swift build -c release --product SlothBoss
  else
    exit 1
  fi
fi
# shellcheck disable=SC2086
bin_dir="$(swift build -c release --product SlothBoss $arch_args --show-bin-path)"
echo "==> products in $bin_dir"

executable="$bin_dir/SlothBoss"
resource_bundle="$bin_dir/SlothBoss_SlothBossCore.bundle"
[ -f "$executable" ] || { echo "error: $executable not found" >&2; exit 1; }
[ -d "$resource_bundle" ] || { echo "error: $resource_bundle not found" >&2; exit 1; }

rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$executable" "$app/Contents/MacOS/SlothBoss"
# Resources go in Contents/Resources (not the bundle root) so the code signature stays valid;
# SlothBossCore looks there first (see ContractResources.swift).
cp -R "$resource_bundle" "$app/Contents/Resources/"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleDisplayName</key>
    <string>SlothBoss</string>
    <key>CFBundleExecutable</key>
    <string>SlothBoss</string>
    <key>CFBundleIdentifier</key>
    <string>dev.slothboss.SlothBoss</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>SlothBoss</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>${version}</string>
    <key>CFBundleVersion</key>
    <string>${build_number}</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.productivity</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSSupportsAutomaticTermination</key>
    <false/>
    <key>NSAppTransportSecurity</key>
    <dict>
        <!-- Plain HTTP is only needed for the local Ollama server; the app rejects non-loopback endpoints. -->
        <key>NSAllowsLocalNetworking</key>
        <true/>
    </dict>
</dict>
</plist>
PLIST
printf 'APPL????' > "$app/Contents/PkgInfo"

plutil -lint "$app/Contents/Info.plist"

echo "==> architectures"
lipo -info "$app/Contents/MacOS/SlothBoss" || true

echo "==> ad-hoc signing"
xattr -cr "$app"
codesign --force --sign - --timestamp=none "$app"
codesign --verify --strict --verbose=2 "$app"

echo "==> zipping"
(cd "$out" && ditto -c -k --keepParent SlothBoss.app SlothBoss-macOS.zip)
echo "Built $out/SlothBoss-macOS.zip"
