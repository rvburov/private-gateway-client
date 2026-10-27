package com.privategateway.client.ui.settings

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
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
import com.privategateway.client.R
import com.privategateway.client.licenses.OpenComponent
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPageHeader
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgType

/**
 * «Лицензии открытых компонентов» (решение 449): компоненты приложения из assets (сборка сверяет их с зависимостями
 * выпуска) — имя и лицензия, по нажатию — авторство, ссылки, версия, модули и полный текст лицензии. [text] — текст
 * лицензии по SPDX.
 */
@Composable
fun LicensesScreen(components: List<OpenComponent>, text: (String) -> String, onBack: () -> Unit) {
    var open by rememberSaveable { mutableStateOf<String?>(null) }
    val shown = components.firstOrNull { it.name == open }
    if (shown != null) {
        BackHandler { open = null }
        LicenseDetail(shown, text) { open = null }
        return
    }

    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(bottom = PgLayout.ScrollBottomPadding)
            .testTag("licenses"),
        verticalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        PgPageHeader(stringResource(R.string.about_licenses), onBack)
        Text(stringResource(R.string.about_licenses_text), color = PgColor.Text.Secondary, style = PgType.Small)
        PgCard(Modifier.fillMaxWidth()) {
            for (component in components) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .heightIn(min = PgLayout.TouchTarget)
                        .clip(RoundedCornerShape(PgRadius.Button))
                        .clickable(role = Role.Button) { open = component.name }
                        .padding(vertical = PgSpace.Xs)
                        .testTag("licenses.${component.name}"),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(component.name, style = PgType.BodyStrong, modifier = Modifier.weight(1f))
                    Spacer(Modifier.width(PgSpace.M))
                    Text(component.label, color = PgColor.Text.Secondary, style = PgType.Caption)
                    Spacer(Modifier.width(PgSpace.S))
                    PgIcon(PgIcons.ChevronRight, null, color = PgColor.Text.Muted, size = 20.dp)
                }
            }
        }

        Text(stringResource(R.string.android_licenses_notices), color = PgColor.Text.Muted, style = PgType.Caption)
    }
}

/** Один компонент: подпись лицензии, версия, авторство, ссылки (открываются в браузере), модули и текст лицензии. */
@Composable
private fun LicenseDetail(component: OpenComponent, text: (String) -> String, onBack: () -> Unit) {
    val links = LocalUriHandler.current
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .statusBarsPadding()
            .padding(horizontal = PgSpace.WindowSideMobile)
            .padding(bottom = PgLayout.ScrollBottomPadding)
            .testTag("license"),
        verticalArrangement = Arrangement.spacedBy(PgSpace.L),
    ) {
        PgPageHeader(component.name, onBack)
        PgCard(Modifier.fillMaxWidth()) {
            Text(component.label, style = PgType.Title, modifier = Modifier.testTag("license.label"))
            if (component.version.isNotEmpty()) {
                Text(stringResource(R.string.android_licenses_version, component.version), color = PgColor.Text.Secondary, style = PgType.Small)
            }

            if (component.inside.isNotEmpty()) {
                Text(stringResource(R.string.android_licenses_inside, component.inside), color = PgColor.Text.Secondary, style = PgType.Small)
            }

            Spacer(Modifier.size(PgSpace.S))
            Text(component.copyright, style = PgType.Body, modifier = Modifier.testTag("license.copyright"))
            if (component.notice.isNotEmpty()) {
                Text(component.notice, color = PgColor.Text.Secondary, style = PgType.Small)
            }

            Spacer(Modifier.size(PgSpace.S))
            Link(component.url, "license.url") { links.openUri(component.url) }
            if (component.source.isNotEmpty()) {
                Text(stringResource(R.string.android_licenses_source), color = PgColor.Text.Secondary, style = PgType.Caption)
                Link(component.source, "license.source") { links.openUri(component.source) }
            }

            if (component.modules.isNotEmpty()) {
                Spacer(Modifier.size(PgSpace.S))
                Overline(stringResource(R.string.android_licenses_modules))
                Spacer(Modifier.size(PgSpace.Xs))
                Text(component.modules.joinToString("\n"), color = PgColor.Text.Secondary, style = PgType.Caption, modifier = Modifier.testTag("license.modules"))
            }
        }

        for (id in component.texts) {
            PgCard(Modifier.fillMaxWidth()) {
                Overline(stringResource(R.string.android_licenses_text) + " · " + id)
                Spacer(Modifier.size(PgSpace.S))
                val body = remember(id) { text(id) }
                SelectionContainer {
                    Text(body, style = PgType.Mono.copy(fontSize = PgType.Caption.fontSize, lineHeight = PgType.Caption.lineHeight), modifier = Modifier.testTag("license.text.$id"))
                }
            }
        }
    }
}

@Composable
private fun Link(url: String, tag: String, onClick: () -> Unit) {
    Text(
        url,
        color = PgColor.Accent.Text,
        style = PgType.Small,
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(role = Role.Button, onClick = onClick)
            .padding(vertical = PgSpace.Xs)
            .testTag(tag),
    )
}
