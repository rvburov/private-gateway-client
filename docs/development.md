# Разработка и сборка

Один путь для каждой ОС: как запустить программу из исходников и как собрать установщик. Все команды — из корня
репозитория: на Windows — в **Git Bash** (в PowerShell и cmd скрипты `scripts/…` не запустятся), на macOS и Linux — в
Терминале. Обычному пользователю это не нужно: ему — установщик ([docs/user/guide.md](user/guide.md), «Установка»).

## 1. Один раз: что поставить

.NET 10 SDK — **для всей машины**: служба работает от имени системы и .NET, поставленный только для вашей учетной
записи (скрипт dotnet-install, расширение VS Code), не видит.

| ОС | Команда |
| --- | --- |
| Windows | в PowerShell: `winget install Git.Git Microsoft.DotNet.SDK.10` — дальше только Git Bash |
| Debian, Ubuntu | `sudo apt-get install -y git curl unzip dotnet-sdk-10.0` |
| Fedora | `sudo dnf install -y git curl unzip dotnet-sdk-10.0` |
| macOS | `xcode-select --install`, затем `brew install --cask dotnet-sdk` (нужен Homebrew) |

Для установщиков нужен еще Go: `scripts/publish.sh` собирает им геобазу сайтов (`geosite.dat`) из исходников
v2fly/domain-list-community. Без него сборка выпуска остановится на «Нет go — геобазу сайтов не собрать».

| ОС | Команда |
| --- | --- |
| Windows | в PowerShell: `winget install GoLang.Go`, затем открыть Git Bash заново |
| Debian, Ubuntu | `sudo apt-get install -y golang-go` |
| Fedora | `sudo dnf install -y golang` |
| macOS | `brew install go` |

Проверка — `go version`. И еще по одному инструменту под установщик своей ОС (раздел 4).

## 2. Получить исходники

Первый раз:

```bash
git clone https://github.com/rvburov/private-gateway-client.git
cd private-gateway-client
```

Папка уже есть — обновить:

```bash
cd путь/к/private-gateway-client
git status                       # «On branch master», «nothing to commit, working tree clean»
git checkout master && git pull
```

`git status` перечисляет измененные файлы или `git pull` отказывает — остановитесь и ничего не удаляйте: это ваши
правки, их сначала нужно сохранить.

## 3. Запуск при разработке

```bash
scripts/get-xray.sh && scripts/get-singbox.sh   # ядра со сверенной суммой; после git pull повторить не вредно
dotnet build PrivateGateway.slnx                 # окно и служба, в первый раз — пара минут
dotnet run --project desktop/src/Client.UI       # окно
```

- `dotnet run --project desktop/src/Client.UI -- --demo` — окно на имитации службы, с демо-серверами: ничего не
  ставит и сеть машины не трогает. Для работы над интерфейсом.
- Служба: окно само предложит «Установка службы» → «Установить» (права администратора спросит система). После
  `git pull` и сборки «Настройки» → «О программе» покажет, что служба из других исходников, — «Обновить службу».
  Служба не отвечает — там же «Переустановить службу».
- Перед `dotnet build` закройте запущенную программу (значок в трее → «Выход»): иначе сборка упадет на занятых файлах
  («being used by another process»).
- Тесты: `dotnet test --project desktop/tests/Unit` — быстрые, `dotnet test --solution PrivateGateway.slnx` — все.
  Стиль: `dotnet format PrivateGateway.slnx`. Подробнее — AGENTS.md, «Тесты».

## 4. Установщик

Установщик собирается на машине той ОС, для которой он: сначала сборка выпуска (`scripts/publish.sh`, ставить .NET
пользователю не нужно — он внутри), затем пакет. Версия — SemVer, по умолчанию из `Directory.Build.props`. Каждый
скрипт в конце печатает, где лежит готовый файл.

### Windows — `.msi`

Один раз: `dotnet tool install --global wix --version 5.0.2` (расширения WiX скрипт ставит сам). Затем в Git Bash:

```bash
scripts/publish.sh 0.1.0
scripts/package-windows.sh 0.1.0
```

Готово: `artifacts\release\PrivateGateway-0.1.0-win-x64.msi` (полный путь — последняя строка вывода). Установка —
двойной щелчок: приветствие → папка установки → «Установить» (Windows спросит права администратора) → «Готово» с
флажком «Запустить Private Gateway». Служба ставится установщиком. Удаление — «Параметры» → «Приложения» →
«Установленные приложения» → Private Gateway → «…» → «Удалить».

Программа из исходников и из MSI — одна и та же служба: перед установкой MSI окно из исходников закройте. Новая
сборка MSI той же версии ставится поверх прежней.

### macOS — `.dmg`

Только на macOS (нужны `codesign` и `hdiutil` — они есть в системе):

```bash
scripts/publish.sh 0.1.0
scripts/package-macos.sh 0.1.0
```

Готово: `artifacts/release/PrivateGateway-0.1.0-osx-arm64.dmg` (на Intel — `osx-x64`). Установка — открыть образ и
перетащить Private Gateway в «Программы»; первый запуск — «Системные настройки» → «Конфиденциальность и
безопасность» → «Все равно открыть» (подписи Developer ID пока нет).

### Linux — `.deb` и `.rpm`

Один раз — nfpm (нужен go): `go install github.com/goreleaser/nfpm/v2/cmd/nfpm@v2.47.0`, и `~/go/bin` в `PATH`.

```bash
scripts/publish.sh 0.1.0
scripts/package-linux.sh 0.1.0
```

Готово: `artifacts/release/private-gateway_0.1.0_amd64.deb` и `private-gateway-0.1.0.x86_64.rpm`. Установка —
`sudo apt install ./artifacts/release/private-gateway_0.1.0_amd64.deb` или
`sudo dnf install ./artifacts/release/private-gateway-0.1.0.x86_64.rpm`; удаление — `sudo apt remove private-gateway`
или `sudo dnf remove private-gateway`.

### Без сборки у себя

CI собирает все три установщика при каждом изменении `master`: GitHub → «Actions» → «Сборка выпуска» → верхний
запуск с зеленой галочкой → «Artifacts» → `release-Windows` (`release-Linux`, `release-macOS`). Выпуск с тегом —
[docs/architecture/release.md](architecture/release.md).

## 5. Частые проблемы

- **Сборка падает на занятых файлах** — запущена программа или ее служба из папки репозитория: закройте окно (трей →
  «Выход») и соберите снова.
- **«wix» не найден** или **«Нет go — геобазу сайтов не собрать»** — wix или Go не поставлены (раздел 1) или терминал
  открыт до их установки: откройте Git Bash заново.
- **Подписка не загружается, а сайт в браузере открывается** — в системе остался прокси другой программы (например,
  выключенного VPN). Программа сама повторит загрузку напрямую; «Проверка» → «Другие VPN и прокси» покажет такой
  прокси.
- **Служба «работает», но окно пишет «Не отвечает»** — «Настройки» → «О программе» → «Переустановить службу»;
  причина — в журнале окна («Экспорт отчета»).
