package com.privategateway.client.notify

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import javax.inject.Inject
import javax.inject.Singleton

/** Окно приложения на экране (между onStart и onStop окна): тогда в шторку ничего не идет — окно показывает то же само. */
@Singleton
class AppWindow @Inject constructor() {
    private val shown = MutableStateFlow(false)

    val visible: StateFlow<Boolean> = shown.asStateFlow()

    fun set(visible: Boolean) {
        shown.value = visible
    }
}
