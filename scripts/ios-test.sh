#!/usr/bin/env bash
# Приложение iOS: проект (scripts/ios-project.sh), сборка приложения и расширения туннеля, тесты на симуляторе — первый
# доступный iPhone самой новой iOS. Нужны macOS и Xcode; подписи нет. Итог тестов — artifacts/ios-tests.xcresult.
# Запуск: scripts/ios-test.sh
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
"$root/scripts/ios-project.sh"

device=$(xcrun simctl list devices available --json | python3 -c '
import json, sys
devices = json.load(sys.stdin)["devices"]
def version(runtime):
    return [int(part) for part in runtime.rsplit("iOS-", 1)[1].split("-") if part.isdigit()]
for runtime in sorted((r for r in devices if ".iOS-" in r), key=version, reverse=True):
    for device in devices[runtime]:
        if device.get("isAvailable", True) and device["name"].startswith("iPhone"):
            print(device["udid"])
            sys.exit(0)
sys.exit("Нет симулятора iPhone: установите среду iOS в Xcode")
')

result="$root/artifacts/ios-tests.xcresult"
rm -rf "$result"
mkdir -p "$root/artifacts"
# Плагин сборки пакета (тема и тексты) — свой, из этого репозитория: доверие к нему не спрашивается.
xcodebuild -quiet test \
  -project "$root/ios/PrivateGateway.xcodeproj" \
  -scheme PrivateGateway \
  -destination "id=$device" \
  -skipPackagePluginValidation \
  -resultBundlePath "$result" \
  CODE_SIGNING_ALLOWED=NO

# Числа прогона — из итога xcodebuild (-quiet их не печатает); ни одного теста — тоже сбой, а не «прошло».
xcrun xcresulttool get test-results summary --path "$result" --compact | python3 -c '
import json, sys
summary = json.load(sys.stdin)
total = summary.get("totalTestCount", 0)
counts = (total, summary.get("passedTests"), summary.get("failedTests"), summary.get("skippedTests"))
print("Тесты iOS: всего %s, прошло %s, упало %s, пропущено %s" % counts)
sys.exit(0 if total > 0 else "Тесты iOS не запускались")
'
echo "Итог: $result"
