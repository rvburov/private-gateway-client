package com.privategateway.client.ui.settings

import android.os.Build
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.privategateway.client.BuildConfig
import com.privategateway.client.R
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPageHeader
import com.privategateway.client.ui.theme.PgSheet
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.ui.theme.pgTopDivider

/** Страница выпусков: APK для Android выкладывается туда (решение 554); магазина у приложения пока нет. */
internal const val RELEASES_URL = "https://github.com/rvburov/private-gateway-client/releases"

/** Поля карточек строк (.card с padding 4px 16px): строки сами без боковых полей, полосы — внутри полей карточки. */
private val RowsPadding = PaddingValues(horizontal = PgSpace.Card, vertical = PgSpace.Xxs)

/**
 * «О программе» (доска 15, решение 555): карточка программы (знак, название, версия · Android · архитектура), «Компоненты»
 * (клиент, Xray-core, TUN-драйвер Android), «Приватность» тремя строками, ссылки — лицензии, политика конфиденциальности
 * листом, «Сообщить о проблеме» (вкладка «Проверка» с отчетом) и «Новые версии» (страница выпусков GitHub). Карточки
 * «Обновления» нет: APK не обновляет себя сам, а встроенной установки обновлений у телефона нет (решение 555); строки «Служба»
 * и «Hysteria2, TUIC» — только у десктопа (службы и второго ядра на телефоне нет).
 */
@Composable
fun AboutScreen(
    core: String?,
    loadCore: () -> Unit,
    onLicenses: () -> Unit,
    onReport: () -> Unit,
    onBack: () -> Unit,
) {
    LaunchedEffect(Unit) { loadCore() }
    var privacy by rememberSaveable { mutableStateOf(false) }
    val links = LocalUriHandler.current
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(bottom = PgLayout.ScrollBottomPadding)
            .testTag("about"),
    ) {
        PgPageHeader(stringResource(R.string.settings_adv_about_title), onBack)
        Spacer(Modifier.size(PgSpace.L))
        AppCard()

        GroupTitle(stringResource(R.string.about_components), top = 18.dp)
        PgCard(Modifier.fillMaxWidth(), contentPadding = RowsPadding) {
            ValueRow(stringResource(R.string.about_client), BuildConfig.VERSION_NAME, "about.client", divider = false)
            ValueRow(stringResource(R.string.core_xray_title), core ?: "—", "about.core", divider = true)
            ValueRow(stringResource(R.string.about_tun), stringResource(R.string.about_tun_system), "about.tun", divider = true)
        }

        GroupTitle(stringResource(R.string.about_privacy_group), top = 18.dp)
        PgCard(Modifier.fillMaxWidth(), contentPadding = RowsPadding) {
            PrivacyRow(stringResource(R.string.about_privacy_telemetry), divider = false)
            PrivacyRow(stringResource(R.string.about_privacy_logs_mobile), divider = true)
            PrivacyRow(stringResource(R.string.about_privacy_report), divider = true)
        }

        Spacer(Modifier.size(18.dp))
        PgCard(Modifier.fillMaxWidth(), contentPadding = RowsPadding) {
            LinkLine(stringResource(R.string.about_licenses), null, "about.licenses", divider = false, onLicenses)
            LinkLine(stringResource(R.string.about_privacy), null, "about.privacy", divider = true) { privacy = true }
            LinkLine(stringResource(R.string.about_report), null, "about.report", divider = true, onReport)
            LinkLine(
                stringResource(R.string.android_about_releases),
                stringResource(R.string.android_about_releases_text),
                "about.releases",
                divider = true,
            ) {
                runCatching { links.openUri(RELEASES_URL) }
            }
        }
    }

    if (privacy) {
        BackHandler { privacy = false }
        PrivacySheet { privacy = false }
    }
}

/** Карточка программы: плитка 56 со знаком акцентным цветом, название полужирным и «Версия 0.1.0 · Android 15 · arm64». */
@Composable
private fun AppCard() {
    PgCard(Modifier.fillMaxWidth().testTag("about.app")) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier.size(56.dp).clip(RoundedCornerShape(16.dp)).background(PgColor.Accent.Tint),
                contentAlignment = Alignment.Center,
            ) {
                PgIcon(PgIcons.Shield, null, color = PgColor.Accent.Base, size = 32.dp)
                PgIcon(PgIcons.MarkOk, null, color = PgColor.Accent.Base, size = 32.dp)
            }

            Spacer(Modifier.width(14.dp))
            Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(stringResource(R.string.about_name), color = PgColor.Text.Primary, style = PgType.Status)
                Text(
                    stringResource(R.string.about_version, BuildConfig.VERSION_NAME, "Android " + Build.VERSION.RELEASE, abi()),
                    Modifier.testTag("about.version"),
                    color = PgColor.Text.Secondary,
                    style = PgType.Small,
                )
            }
        }
    }
}

/** Архитектура словами макета: arm64-v8a → arm64, x86_64 → x64, armeabi-v7a → arm. */
internal fun abiName(abi: String?): String = when (abi) {
    null, "" -> "—"
    "arm64-v8a" -> "arm64"
    "x86_64" -> "x64"
    "armeabi-v7a" -> "arm"
    else -> abi
}

private fun abi(): String = abiName(Build.SUPPORTED_ABIS.firstOrNull())

/** Строка «Компонентов»: название слева, значение моноширинным справа. */
@Composable
private fun ValueRow(title: String, value: String, tag: String, divider: Boolean) {
    Row(
        Modifier.fillMaxWidth().pgTopDivider(divider).padding(vertical = 13.dp).testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(title, Modifier.weight(1f), color = PgColor.Text.Primary, style = PgType.BodyStrong)
        Spacer(Modifier.width(PgSpace.M))
        Text(value, color = PgColor.Text.Secondary, style = PgType.Mono)
    }
}

/** Строка «Приватности»: зеленый кружок с галочкой и текст. */
@Composable
private fun PrivacyRow(text: String, divider: Boolean) {
    Row(Modifier.fillMaxWidth().pgTopDivider(divider).padding(vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(22.dp).clip(CircleShape).background(PgColor.Status.Success.Tint), contentAlignment = Alignment.Center) {
            PgIcon(PgIcons.ChipOk, null, color = PgColor.Status.Success.Base, size = 14.dp, strokeWidth = 2.6f)
        }

        Spacer(Modifier.width(PgSpace.M))
        Text(text, color = PgColor.Text.Primary, style = PgType.Body)
    }
}

/** Строка-ссылка (.link-row): текст и стрелка справа, без плитки значка; [caption] — подпись под текстом. */
@Composable
private fun LinkLine(title: String, caption: String?, tag: String, divider: Boolean, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .pgTopDivider(divider)
            .heightIn(min = PgLayout.TouchTarget)
            .clickable(role = Role.Button, onClick = onClick)
            .padding(vertical = 12.dp)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, color = PgColor.Text.Primary, style = PgType.BodyStrong)
            if (caption != null) {
                Text(caption, color = PgColor.Text.Muted, style = PgType.Caption)
            }
        }

        PgIcon(PgIcons.ChevronRight, null, color = PgColor.Text.Muted, size = 20.dp)
    }
}

/** Лист «Политика конфиденциальности»: заголовок, крестик и текст для телефона (журнал — на этом устройстве). */
@Composable
private fun PrivacySheet(onClose: () -> Unit) {
    PgSheet(onClose, "about.privacy.sheet") {
        SheetHeader(stringResource(R.string.about_privacy), onClose, "about.privacy.close")
        Spacer(Modifier.size(PgSpace.M))
        Text(stringResource(R.string.about_privacy_text_mobile), color = PgColor.Text.Secondary, style = PgType.Body)
    }
}
