package com.privategateway.client.ui.theme

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.privategateway.client.R

/** Кнопка макета: акцентная заливка, скругление radius.button, высота не меньше layout.touchTarget. */
@Composable
fun PgButton(label: String, enabled: Boolean, tag: String, onClick: () -> Unit) {
    val shape = RoundedCornerShape(PgRadius.Button)
    Box(
        Modifier
            .fillMaxWidth()
            .heightIn(min = PgLayout.TouchTarget)
            .clip(shape)
            .background(if (enabled) PgColor.Accent.Base else PgColor.Control.TrackOff)
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick)
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = PgColor.Surface.Solid, style = PgType.BodyStrong)
    }
}

/** Вариант выбора (один из нескольких): название, пояснение и кружок выбора справа. */
@Composable
fun PgChoiceRow(title: String, text: String?, selected: Boolean, tag: String, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = PgLayout.TouchTarget)
            .clip(RoundedCornerShape(PgRadius.Button))
            .selectable(selected = selected, role = Role.RadioButton, onClick = onClick)
            .padding(vertical = PgSpace.Xs)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, style = PgType.Body)
            if (!text.isNullOrEmpty()) {
                Text(text, color = PgColor.Text.Secondary, style = PgType.Caption)
            }
        }

        Spacer(Modifier.width(PgSpace.M))
        Box(
            Modifier
                .size(20.dp)
                .clip(CircleShape)
                .border(2.dp, if (selected) PgColor.Accent.Base else PgColor.Control.CheckboxBorder, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            if (selected) {
                Box(Modifier.size(10.dp).clip(CircleShape).background(PgColor.Accent.Base))
            }
        }
    }
}

/** Переключатель с названием и пояснением; дорожка — accent.base или control.trackOff, бегунок — surface.solid. */
@Composable
fun PgToggleRow(title: String, text: String?, checked: Boolean, tag: String, onChange: (Boolean) -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = PgLayout.TouchTarget)
            .toggleable(value = checked, role = Role.Switch, onValueChange = onChange)
            .padding(vertical = PgSpace.Xs)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, style = PgType.Body)
            if (!text.isNullOrEmpty()) {
                Text(text, color = PgColor.Text.Secondary, style = PgType.Caption)
            }
        }

        Spacer(Modifier.width(PgSpace.M))
        Box(
            Modifier
                .width(44.dp)
                .heightIn(min = 24.dp, max = 24.dp)
                .clip(CircleShape)
                .background(if (checked) PgColor.Accent.Base else PgColor.Control.TrackOff)
                .padding(2.dp),
            contentAlignment = if (checked) Alignment.CenterEnd else Alignment.CenterStart,
        ) {
            Box(Modifier.size(20.dp).clip(CircleShape).background(PgColor.Surface.Solid))
        }
    }
}

/** Заголовок вложенной страницы: «Назад» и название. */
@Composable
fun PgPageHeader(title: String, onBack: () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(top = PgSpace.L), verticalAlignment = Alignment.CenterVertically) {
        val back = stringResource(R.string.common_back)
        Box(
            Modifier.size(PgLayout.TouchTarget).clip(CircleShape).clickable(onClickLabel = back, role = Role.Button, onClick = onBack),
            contentAlignment = Alignment.Center,
        ) {
            PgIcon(PgIcons.Back, back, color = PgColor.Text.Primary, size = 22.dp, strokeWidth = 2f)
        }

        Spacer(Modifier.width(PgSpace.S))
        Text(title, style = PgType.H1)
    }
}
