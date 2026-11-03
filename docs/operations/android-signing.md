# Ключ подписи Android (APK выпуска)

Решение пользователя 2026-11-03 (DECISIONS.md, п. 11): APK выпуска подписывается своим ключом, а не отладочным. Ключ создает
пользователь на своем компьютере и кладет в секреты GitHub; сборка выпуска (`release.yml`) берет его оттуда.

Почему это важно:

- Android ставит обновление только поверх приложения с тем же ключом. Сменить ключ потом нельзя без удаления приложения
  (вместе с серверами и настройками на телефоне).
- Потерянный ключ — конец обновлений: новую версию придется ставить начисто. Храните файл ключа и пароль в двух местах
  вне репозитория (например, менеджер паролей и флешка).
- Ключ и пароли — секреты: не класть в репозиторий, не присылать в чат.

## 1. Создать ключ (один раз)

Нужна программа `keytool` из JDK. Если стоит Android Studio — она уже есть внутри. Все команды — в Git Bash.

Сначала найдите `keytool` (в Android Studio он лежит в папке `jbr`, в JDK — в `bin`; путь зависит от версии и места
установки):

```bash
find "/c/Program Files" "/c/Program Files (x86)" ~/AppData/Local ~/scoop -iname keytool.exe 2>/dev/null
```

Ничего не нашлось — `keytool` нет: поставьте JDK. В Git Bash через Scoop:

```bash
scoop bucket add java
scoop install java/temurin21-jdk
find ~/scoop/apps/temurin21-jdk -iname keytool.exe
```

На macOS — `find /Applications ~/Library -name keytool 2>/dev/null`, на Linux — `which keytool`. Возьмите путь из ответа
(например, `/c/Users/<вы>/scoop/apps/temurin21-jdk/current/bin/keytool.exe`) и выполните — каждую команду одной строкой:

```bash
KEYTOOL="$HOME/scoop/apps/temurin21-jdk/current/bin/keytool.exe"
mkdir -p ~/private-gateway-keys
"$KEYTOOL" -genkeypair -v -keystore ~/private-gateway-keys/release.p12 -storetype PKCS12 -alias private-gateway -keyalg RSA -keysize 4096 -validity 10000
```

`keytool` спросит:

1. **Пароль хранилища** — дважды. Придумайте длинный и запишите в менеджер паролей.
2. **Имя, организация, город, страна** — можно ваше имя и код страны, остальное пропустить (Enter).
3. **Подтверждение** — `yes` (или `да`).

Пароль ключа у формата PKCS12 тот же, что пароль хранилища.

## 2. Превратить ключ в текст для GitHub

```bash
base64 -w0 ~/private-gateway-keys/release.p12 > ~/private-gateway-keys/release.p12.base64
```

На macOS вместо `-w0` — `base64 -i ~/private-gateway-keys/release.p12 -o ~/private-gateway-keys/release.p12.base64`.

## 3. Положить в секреты GitHub

На github.com откройте репозиторий → **Settings** → **Secrets and variables** → **Actions** → **New repository secret** и
создайте четыре секрета:

| Name | Secret |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | все содержимое файла `release.p12.base64` (откройте его в Блокноте, скопируйте целиком) |
| `ANDROID_KEYSTORE_PASSWORD` | пароль хранилища из шага 1 |
| `ANDROID_KEY_ALIAS` | `private-gateway` |
| `ANDROID_KEY_PASSWORD` | тот же пароль хранилища |

После этого файл `release.p12.base64` можно удалить — сам `release.p12` оставьте в надежном месте.

## 4. Что дальше

Подпись подключена (`android/app/build.gradle.kts`, `scripts/android-release-apk.sh`, `release.yml`): есть секреты — APK
выпуска подписан этим ключом и проверен `apksigner`; нет — выпуск останавливается с объяснением, отладочный APK не
выкладывается. CI на каждой проверке собирает APK выпуска на временном ключе, чтобы этот путь не сломался незаметно.
Проверка на телефоне: новая версия ставится поверх прежней без удаления.

Если раньше на телефоне стоял APK с отладочной подписью (бета до ключа выпуска), его один раз нужно удалить перед установкой
подписанного: Android не ставит приложение поверх версии с другим ключом.
