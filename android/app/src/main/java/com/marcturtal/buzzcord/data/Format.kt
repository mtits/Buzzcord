package com.marcturtal.buzzcord.data

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.withStyle
import com.marcturtal.buzzcord.net.objs
import com.marcturtal.buzzcord.net.str
import org.json.JSONObject

object Format {
    const val BUZZ = "BUZZ!!!"

    private val token = Regex("<(@!?|@&|#)(\\d+)>|<a?:(\\w+):(\\d+)>|(https?://[^\\s<]+[^\\s<.,:;\"')\\]])")

    private fun resolve(s: Session, m: JSONObject, kind: String, id: String): String = when (kind) {
        "@", "@!" -> "@" + s.name(id)
        "@&" -> "@" + (s.guilds[m.str("guild_id") ?: ""]?.roles?.get(id)?.name ?: "role")
        "#" -> "#" + (s.findChannel(id)?.name ?: "channel")
        else -> ""
    }

    // One line of plain text for notifications and previews.
    fun plain(s: Session, m: JSONObject): String {
        val content = m.str("content") ?: ""
        val text = token.replace(content) { r ->
            val g = r.groupValues
            when {
                g[1].isNotEmpty() -> resolve(s, m, g[1], g[2])
                g[3].isNotEmpty() -> ":${g[3]}:"
                else -> g[0]
            }
        }.trim()
        if (text.isNotEmpty()) return text
        val atts = m.objs("attachments")
        if (atts.isNotEmpty()) {
            val pics = atts.count { (it.str("content_type") ?: "").startsWith("image/") }
            return if (pics == atts.size) (if (pics == 1) "sent a picture" else "sent $pics pictures") else "sent a file: " + (atts[0].str("filename") ?: "file")
        }
        m.objs("sticker_items").firstOrNull()?.let { return "sent a sticker: " + (it.str("name") ?: "") }
        m.objs("embeds").firstOrNull()?.let { return it.str("title") ?: it.str("url") ?: "sent a link" }
        return ""
    }

    // Message text with clickable links and highlighted mentions.
    fun rich(s: Session, m: JSONObject, link: Color, mention: Color): AnnotatedString {
        val content = m.str("content") ?: ""
        return buildAnnotatedString {
            var pos = 0
            for (r in token.findAll(content)) {
                append(content.substring(pos, r.range.first))
                val g = r.groupValues
                when {
                    g[1].isNotEmpty() -> withStyle(SpanStyle(color = mention, fontWeight = FontWeight.SemiBold)) { append(resolve(s, m, g[1], g[2])) }
                    g[3].isNotEmpty() -> append(":${g[3]}:")
                    else -> withLink(LinkAnnotation.Url(g[5], TextLinkStyles(SpanStyle(color = link)))) { append(g[5]) }
                }
                pos = r.range.last + 1
            }
            append(content.substring(pos))
        }
    }
}
