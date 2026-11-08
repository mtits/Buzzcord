package com.marcturtal.buzzcord.net

import org.json.JSONArray
import org.json.JSONObject
import org.json.JSONTokener

// Small helpers over org.json (built into Android, so no JSON library in the APK).

fun JSONObject.str(k: String): String? = if (has(k) && !isNull(k)) opt(k)?.toString() else null

fun JSONObject.long(k: String, def: Long = 0L): Long {
    if (!has(k) || isNull(k)) return def
    return when (val v = opt(k)) {
        is Number -> v.toLong()
        is String -> v.toLongOrNull() ?: def
        else -> def
    }
}

fun JSONObject.bool(k: String): Boolean = optBoolean(k, false)

fun JSONObject.obj(k: String): JSONObject? = optJSONObject(k)

fun JSONObject.arr(k: String): JSONArray? = optJSONArray(k)

fun JSONObject.objs(k: String): List<JSONObject> = optJSONArray(k)?.objs() ?: emptyList()

fun JSONArray.objs(): List<JSONObject> {
    val out = ArrayList<JSONObject>(length())
    for (i in 0 until length()) optJSONObject(i)?.let { out.add(it) }
    return out
}

fun JSONObject.strs(k: String): List<String> {
    val a = optJSONArray(k) ?: return emptyList()
    val out = ArrayList<String>(a.length())
    for (i in 0 until a.length()) if (!a.isNull(i)) out.add(a.opt(i).toString())
    return out
}

fun jobj(vararg pairs: Pair<String, Any?>): JSONObject {
    val o = JSONObject()
    for ((k, v) in pairs) o.put(k, v ?: JSONObject.NULL)
    return o
}

fun jarr(vararg items: Any?): JSONArray {
    val a = JSONArray()
    for (v in items) a.put(v ?: JSONObject.NULL)
    return a
}

fun parseJson(text: String): Any? = try {
    if (text.isBlank()) null else JSONTokener(text).nextValue()
} catch (e: Exception) {
    null
}
