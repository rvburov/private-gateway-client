#!/bin/sh
# Меню и значки рабочего стола узнают о новой записи сразу, а не после входа в систему.
command -v update-desktop-database > /dev/null && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache > /dev/null && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
