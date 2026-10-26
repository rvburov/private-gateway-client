package com.privategateway.client.core.servers

/** Свое название сервера — правило карточки десктопа (`ServerCardViewModel.CanSaveName`, `Rename` каталога). */
object ServerNames {
    /** Длина своего названия, без пробелов по краям. */
    const val MAX_LENGTH = 64

    /** Название к сохранению: без пробелов по краям; пустое или длиннее [MAX_LENGTH] — null, кнопка «Сохранить» гаснет. */
    fun validate(name: String): String? = name.trim().takeIf { it.length in 1..MAX_LENGTH }
}
