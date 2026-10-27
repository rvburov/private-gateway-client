# Загрузка файла с повторами и докачкой — подключается скриптами (". scripts/download.sh"), сама не запускается.
# Обрыв посреди файла (curl 18 «end of response with … bytes missing», сброс соединения, тайм-аут) на медленной или
# нестабильной связи с GitHub — обычное дело: каждая следующая попытка продолжает с места обрыва (-C -). Сервер без
# докачки (curl 33) — файл начинается заново. Целостность проверяет тот, кто скачал: сумма SHA-256 после загрузки.
#   download <адрес> <файл>
# DOWNLOAD_PAUSE — пауза перед повтором в секундах, умножается на номер попытки (по умолчанию 2; тест ставит 0).
download() {
  local url=$1 file=$2 attempt code
  for attempt in 1 2 3 4 5 6; do
    code=0
    curl -fsSL --connect-timeout 20 -C - -o "$file" "$url" || code=$?
    if [ "$code" -eq 0 ]; then
      return 0
    fi

    # 33 — докачка не поддерживается: начать файл заново.
    [ "$code" -eq 33 ] && rm -f "$file"
    [ "$attempt" -eq 6 ] && break
    echo "Загрузка ${file##*/} оборвалась (curl $code, попытка $attempt из 6) — продолжаю с места обрыва" >&2
    sleep $((attempt * ${DOWNLOAD_PAUSE:-2}))
  done

  echo "Не удалось скачать $url (curl $code) — проверьте связь с github.com и запустите скрипт снова" >&2
  return 1
}
