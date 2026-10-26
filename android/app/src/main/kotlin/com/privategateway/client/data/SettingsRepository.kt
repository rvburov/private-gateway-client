package com.privategateway.client.data

import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import javax.inject.Inject
import javax.inject.Singleton

/** Настройки приложения (DataStore): выбранный сервер. Сервер меняется только нажатием пользователя. */
@Singleton
class SettingsRepository @Inject constructor(private val store: DataStore<Preferences>) {
    val selectedServerId: Flow<String?> = store.data.map { it[SELECTED] }

    suspend fun select(id: String?) {
        store.edit { preferences ->
            if (id == null) {
                preferences.remove(SELECTED)
            } else {
                preferences[SELECTED] = id
            }
        }
    }

    private companion object {
        val SELECTED = stringPreferencesKey("selected_server")
    }
}
