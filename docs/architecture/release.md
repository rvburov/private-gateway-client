# Сборка выпуска

Как из исходников получается то, что ставится пользователю. Подпись кода и артефактов — «Отложено» (README); установщик,
выпуски на GitHub и обновления — следующие пункты этапа 5, этот документ растет вместе с ними.

## Что в выпуске

`scripts/publish.sh [версия]` на машине нужной ОС (в CI — workflow `publish.yml`, три ОС) собирает выпуск
`PrivateGateway-<версия>-<rid>` (`win-x64`, `linux-x64`, `osx-arm64`):

```
PrivateGateway-<версия>-<rid>/
  PrivateGateway(.exe)            окно
  PrivateGateway.Service(.exe)    служба — рядом с окном: окно ищет ее в своей папке (UserServiceLauncher)
  *.dll, *.so, *.dylib            .NET и пакеты — самодостаточная сборка, ставить .NET не нужно
  cores/xray/                     Xray-core, geoip.dat, geosite.dat — служба ищет ядро в cores/ рядом с собой
  cores/sing-box/                 sing-box (TUN режима VPN, GPL-3.0, отдельный процесс) и его LICENSE
  sbom.cdx.json                   SBOM этого выпуска
```

- Окно и служба — `dotnet publish -r <rid> --self-contained` с ReadyToRun (быстрее запуск — окно стартует с системой).
  Отладочные символы и XML-документация своих сборок в выпуск не идут. Версия — `-p:Version`, по умолчанию `<Version>`
  из `Directory.Build.props`; не SemVer — отказ.
- Ядра качаются заново при каждой сборке выпуска: `scripts/get-xray.sh --pinned` и `scripts/get-singbox.sh` берут
  только архивы с суммой, закрепленной в `shared/cores/*.sha256`. Сборка Xray из исходников (`--build`, облако) и что
  угодно, лежащее в `artifacts/cores`, в выпуск не попадают.

## SBOM и контрольные суммы

`scripts/package.cs` (вызывает `publish.sh`) пишет в `artifacts/release/`:

- `<имя>.zip` (Windows) или `<имя>.tar.gz` (Linux, macOS — права на запуск сохраняются);
- `<имя>.cdx.json` — SBOM в формате CycloneDX 1.6, он же в архиве как `sbom.cdx.json`:
  - пакеты NuGet обоих приложений — из их `*.deps.json`, с SHA-512 пакета и лицензией из `.nuspec` в кеше пакетов;
  - среда .NET и ASP.NET Core (runtime pack);
  - Xray-core (MPL-2.0) и sing-box (GPL-3.0-or-later) — версия из `shared/cores/*.version`, SHA-256 файла;
  - геобазы из архива выпуска Xray: `geoip.dat` (данные IPinfo Lite / GeoLite2, CC-BY-SA-4.0), `geosite.dat`
    (v2fly/domain-list-community, MIT) — SHA-256 файла;
  - время — `SOURCE_DATE_EPOCH` (время коммита), чтобы две сборки одного коммита не расходились во времени.

  Неполный SBOM не пишется: пакет без лицензии в `.nuspec`, ядро без версии или файла — ошибка сборки.
- `SHA256SUMS` — суммы всех файлов папки выпуска, проверяются `sha256sum -c SHA256SUMS`.

## Проверка

`scripts/tests/release-smoke.sh <версия>` (шаг `publish.yml` на каждой ОС): суммы сходятся; архив распаковывается; в
нем окно, служба, ядра, геобазы и SBOM, у исполняемых файлов есть право на запуск; служба из архива запускается без
`DOTNET_ROOT` и `dotnet` в `PATH` и называет версию выпуска; ядра запускаются; SBOM в архиве тот же, что рядом с ним, и
называет оба ядра. Готовый выпуск — артефакт `release-<ОС>` запуска workflow.

`publish.yml` идет на тег `v*` (версия — из тега), по кнопке (версия — поле ввода) и на ветках, когда меняется сама
сборка (скрипты выпуска и ядер, `shared/cores`, проекты и версии пакетов).
