#!/usr/bin/env bash
#
# APK выпуска Android, подписанный ключом пользователя (DECISIONS.md, п. 11; docs/operations/android-signing.md).
#   scripts/android-release-apk.sh <версия> <папка>
#
# Ключ — из окружения (в release.yml — секреты GitHub): ANDROID_KEYSTORE_BASE64, ANDROID_KEYSTORE_PASSWORD,
# ANDROID_KEY_ALIAS, ANDROID_KEY_PASSWORD. Нет ключа — остановка с объяснением: отладочный APK в выпуск не попадает.
# Файл ключа живет только во временной папке на время сборки. В папку кладутся PrivateGateway-<версия>-android.apk и
# его .sha256; подпись проверяется apksigner из Android SDK.
set -euo pipefail

version="${1:?версия: 0.1.0 или 0.1.0-beta.1}"
out="${2:?папка для APK}"
cd "$(dirname "${BASH_SOURCE[0]}")/.."

missing=()
for name in ANDROID_KEYSTORE_BASE64 ANDROID_KEYSTORE_PASSWORD ANDROID_KEY_ALIAS ANDROID_KEY_PASSWORD; do
  [[ -n "${!name:-}" ]] || missing+=("$name")
done
if (( ${#missing[@]} )); then
  echo "Нет ключа подписи Android: не заданы ${missing[*]}." >&2
  echo "Ключ создается и кладется в секреты GitHub по docs/operations/android-signing.md." >&2
  exit 1
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
printf '%s' "$ANDROID_KEYSTORE_BASE64" | base64 -d > "$work/release.p12"

PG_KEYSTORE_FILE="$work/release.p12" \
PG_KEYSTORE_PASSWORD="$ANDROID_KEYSTORE_PASSWORD" \
PG_KEY_ALIAS="$ANDROID_KEY_ALIAS" \
PG_KEY_PASSWORD="$ANDROID_KEY_PASSWORD" \
  android/gradlew -p android :app:assembleRelease -PpgVersion="$version"

apk=android/app/build/outputs/apk/release/app-release.apk
if [[ ! -f "$apk" ]]; then
  echo "Сборка не дала подписанный APK ($apk): проверьте пароль и имя ключа в секретах." >&2
  exit 1
fi

sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
apksigner=$(ls -d "$sdk"/build-tools/*/apksigner 2>/dev/null | sort -V | tail -1 || true)
if [[ -z "$apksigner" ]]; then
  echo "Нет apksigner в Android SDK ($sdk): подпись APK не проверить." >&2
  exit 1
fi
"$apksigner" verify --print-certs "$apk" | grep -v '^WARNING' | sed -n '1,4p'

mkdir -p "$out"
name="PrivateGateway-$version-android.apk"
cp "$apk" "$out/$name"
(cd "$out" && sha256sum "$name" > "$name.sha256")
echo "APK выпуска: $out/$name"
