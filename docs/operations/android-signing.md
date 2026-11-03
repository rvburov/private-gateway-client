# Ключ подписи Android (APK выпуска)

Решение пользователя 2026-11-03 (DECISIONS.md, п. 11): APK выпуска подписывается своим ключом, а не отладочным. Ключ создает
пользователь на своем компьютере один раз и кладет в секреты GitHub; сборка выпуска (`release.yml`) берет его оттуда.

Зачем это:

- Android ставит обновление только поверх приложения с тем же ключом: с постоянным ключом новая версия встает поверх старой,
  серверы и настройки на телефоне остаются.
- Подделку видно: чужой APK под видом Private Gateway Android не поставит поверх вашего — без ключа так не подписать.
- Сменить ключ потом нельзя без удаления приложения у всех. Потерянный ключ — конец обновлений.

Правила: файл ключа `release.p12` и его пароль храните в двух местах вне репозитория (менеджер паролей и флешка); ключ,
пароль и текст `.base64` не класть в репозиторий и не присылать в чат.

Все команды ниже — в терминале: на Windows — **Git Bash**, на macOS — **Терминал**, на Linux — свой терминал. Каждую
команду вводите **одной строкой**, как она написана.

## 1. Поставить `keytool` (один раз)

`keytool` — программа из Java (JDK). Сначала проверьте, нет ли ее уже:

| ОС | Команда проверки |
| --- | --- |
| Windows | `find "/c/Program Files" ~/AppData/Local ~/scoop -iname keytool.exe 2>/dev/null` |
| macOS | `/usr/libexec/java_home -V 2>&1; which keytool` |
| Linux | `which keytool` |

Показался путь к `keytool` — переходите к шагу 2 с этим путем. Ничего — поставьте JDK 21:

| ОС | Установка | Путь к `keytool` после установки |
| --- | --- | --- |
| Windows (Scoop) | `scoop bucket add java`, затем `scoop install java/temurin21-jdk` | `$HOME/scoop/apps/temurin21-jdk/current/bin/keytool.exe` |
| macOS (Homebrew) | `brew install --cask temurin@21` | `/Library/Java/JavaVirtualMachines/temurin-21.jdk/Contents/Home/bin/keytool` |
| Linux (Ubuntu, Debian) | `sudo apt install openjdk-21-jdk-headless` | `keytool` |
| Linux (Fedora) | `sudo dnf install java-21-openjdk-devel` | `keytool` |

Предупреждения Scoop про `aria2` и «прежнюю неудачную установку» не мешают: важна последняя строка «was installed
successfully».

## 2. Создать ключ

Запомните путь к `keytool` (подставьте свой из шага 1):

| ОС | Команда |
| --- | --- |
| Windows | `KEYTOOL="$HOME/scoop/apps/temurin21-jdk/current/bin/keytool.exe"` |
| macOS | `KEYTOOL="/Library/Java/JavaVirtualMachines/temurin-21.jdk/Contents/Home/bin/keytool"` |
| Linux | `KEYTOOL=keytool` |

Дальше одинаково на всех ОС:

```bash
mkdir -p ~/private-gateway-keys
```

```bash
"$KEYTOOL" -genkeypair -v -keystore ~/private-gateway-keys/release.p12 -storetype PKCS12 -alias private-gateway -keyalg RSA -keysize 4096 -validity 10000
```

`keytool` спросит:

1. **Пароль хранилища** (`Enter keystore password`) — дважды. Придумайте длинный и сразу сохраните в менеджер паролей.
2. **Имя и фамилию** (`What is your first and last name?`) — ваше имя латиницей, например `Roman Burov`.
3. **Подразделение, организацию, город, область** — нажмите Enter на каждом: их никто не проверяет и не видит.
4. **Код страны** (`two-letter country code`) — `RU` или Enter.
5. **Подтверждение** (`Is CN=… correct?`) — `yes` и Enter.

Отдельного пароля ключа у формата PKCS12 нет: он тот же, что пароль хранилища. Проверка, что ключ создан:

```bash
ls -l ~/private-gateway-keys/release.p12
```

Файл есть, размер около 4–5 КБ — готово.

## 3. Превратить ключ в текст и скопировать

| ОС | Перевести в текст | Скопировать текст в буфер |
| --- | --- | --- |
| Windows | `base64 -w0 ~/private-gateway-keys/release.p12 > ~/private-gateway-keys/release.p12.base64` | `clip < ~/private-gateway-keys/release.p12.base64` |
| macOS | `base64 -i ~/private-gateway-keys/release.p12 -o ~/private-gateway-keys/release.p12.base64` | `pbcopy < ~/private-gateway-keys/release.p12.base64` |
| Linux | `base64 -w0 ~/private-gateway-keys/release.p12 > ~/private-gateway-keys/release.p12.base64` | `xclip -selection clipboard < ~/private-gateway-keys/release.p12.base64` |

Текст — одна длинная строка без переносов, так и должно быть.

## 4. Положить в секреты GitHub

Откройте <https://github.com/rvburov/private-gateway-client/settings/secrets/actions> (репозиторий → **Settings** →
**Secrets and variables** → **Actions**). Для каждой строки таблицы: **New repository secret** → **Name** и **Secret** →
**Add secret**.

| Name | Secret |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | текст из буфера после шага 3 (Ctrl+V, на macOS — Cmd+V) |
| `ANDROID_KEYSTORE_PASSWORD` | пароль хранилища из шага 2 |
| `ANDROID_KEY_ALIAS` | `private-gateway` |
| `ANDROID_KEY_PASSWORD` | тот же пароль хранилища |

В списке **Repository secrets** должны быть все четыре имени (значения GitHub не показывает — так и задумано).

## 5. Убрать лишнее и сохранить ключ

Текстовая копия ключа больше не нужна — удалите ее (на всех ОС):

```bash
rm ~/private-gateway-keys/release.p12.base64
```

Сам `release.p12` скопируйте в два места вне репозитория (менеджер паролей и флешка) вместе с паролем.

## 6. Что дальше

Подпись подключена (`android/app/build.gradle.kts`, `scripts/android-release-apk.sh`, `release.yml`): есть секреты — APK
выпуска подписан этим ключом и проверен `apksigner`; нет — выпуск останавливается с объяснением, отладочный APK не
выкладывается. CI на каждой проверке собирает APK выпуска на временном ключе, чтобы этот путь не сломался незаметно.
Неверный пароль в секрете выпуск тоже остановит — с ошибкой `apksigner` или Gradle в журнале задания «Android (APK)».

Проверка на телефоне: новая версия ставится поверх прежней без удаления. Если раньше на телефоне стоял APK с отладочной
подписью (бета до ключа выпуска), его один раз нужно удалить перед установкой подписанного: Android не ставит приложение
поверх версии с другим ключом.
