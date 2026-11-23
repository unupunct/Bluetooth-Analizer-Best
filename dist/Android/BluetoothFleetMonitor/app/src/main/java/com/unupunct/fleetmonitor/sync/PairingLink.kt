package com.unupunct.fleetmonitor.sync

import java.net.URLDecoder

/**
 * What the Windows app puts in its pairing QR code: bfm://pair?h=host&p=port&f=certificate-fingerprint&c=code&n=server-name
 * The fingerprint is what makes the connection safe: the phone trusts exactly that certificate and nothing else.
 */
data class PairingLink(val host: String, val port: Int, val fingerprint: String, val code: String, val serverName: String) {
    /** The fingerprint in groups of four, for showing to the user. */
    val fingerprintDisplay: String get() = fingerprint.chunked(4).joinToString(" ")

    companion object {
        fun parse(text: String?): PairingLink? {
            val s = text?.trim() ?: return null
            if (!s.startsWith("bfm://pair?", ignoreCase = true)) return null
            val q = s.substringAfter('?').split('&').mapNotNull {
                val i = it.indexOf('=')
                if (i <= 0) null else it.substring(0, i) to runCatching { URLDecoder.decode(it.substring(i + 1), "UTF-8") }.getOrDefault("")
            }.toMap()
            val host = q["h"]?.trim().orEmpty()
            val port = q["p"]?.toIntOrNull() ?: return null
            val fp = q["f"]?.trim()?.uppercase().orEmpty()
            val code = q["c"]?.trim().orEmpty()
            if (host.isEmpty() || host.length > 253 || port !in 1..65535) return null
            if (!Regex("^[0-9A-F]{64}$").matches(fp)) return null
            if (!Regex("^[0-9]{8}$").matches(code)) return null
            return PairingLink(host, port, fp, code, (q["n"] ?: "").take(60).ifBlank { host })
        }
    }
}
