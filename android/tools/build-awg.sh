#!/usr/bin/env bash
# Сборка движка AmneziaWG (модуль tunnel из amneziawg-android) в AAR для WgSwitch.
set -euo pipefail
W=$HOME/awg-build
mkdir -p "$W" && cd "$W"
for t in flock patch; do command -v $t >/dev/null || { echo "нет $t"; exit 1; }; done

# JDK 17 — без sudo, в домашнюю папку
if [ ! -x "$W/jdk/bin/java" ]; then
  echo "== JDK 17"
  curl -sSL -o jdk.tgz "https://api.adoptium.net/v3/binary/latest/17/ga/linux/x64/jdk/hotspot/normal/eclipse"
  mkdir -p jdk && tar -xzf jdk.tgz -C jdk --strip-components=1 && rm jdk.tgz
fi
export JAVA_HOME=$W/jdk PATH=$W/jdk/bin:$PATH

# Android SDK: только командная строка; NDK, CMake и платформы Gradle докачает сам
export ANDROID_HOME=$W/sdk ANDROID_SDK_ROOT=$W/sdk
if [ ! -x "$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager" ]; then
  echo "== Android cmdline-tools"
  curl -sSL -o tools.zip "https://dl.google.com/android/repository/commandlinetools-linux-13114758_latest.zip"
  mkdir -p "$ANDROID_HOME/cmdline-tools" && unzip -q tools.zip -d "$ANDROID_HOME/cmdline-tools" && rm tools.zip
  mv "$ANDROID_HOME/cmdline-tools/cmdline-tools" "$ANDROID_HOME/cmdline-tools/latest"
fi
yes | "$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager" --licenses >/dev/null 2>&1 || true

# исходники — тот же релиз, что у официального приложения
if [ ! -d src ]; then
  echo "== clone"
  git clone -q --depth 1 --branch v3.1.20260814 --recurse-submodules --shallow-submodules https://github.com/amnezia-vpn/amneziawg-android src
fi
cd src
echo "sdk.dir=$ANDROID_HOME" > local.properties

echo "== build tunnel"
ANDROID_PACKAGE_NAME=ru.lkxa.wgswitch ./gradlew --no-daemon -q :tunnel:assembleRelease 2>&1 | tail -30
ls -la tunnel/build/outputs/aar/
