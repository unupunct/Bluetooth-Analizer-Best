package com.unupunct.fleetmonitor.sync

import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

class SyncException(val kind: Kind, message: String) : IOException(message) {
    enum class Kind { Unreachable, CertificateMismatch, Refused, Revoked, Server }
}

data class PairResult(val token: String, val serverName: String)
data class UploadResult(val received: Int, val stored: Int, val duplicates: Int, val rejected: Int, val skewSeconds: Double)

/**
 * Talks to the Windows app over HTTPS. There is no certificate authority involved: the connection is accepted only if the server's
 * certificate has exactly the SHA-256 fingerprint that came from the pairing QR code, so nobody else on the Wi-Fi can pose as the PC.
 */
object SyncClient {
    fun fingerprintOf(cert: X509Certificate): String = MessageDigest.getInstance("SHA-256").digest(cert.encoded).joinToString("") { "%02X".format(it) }

    private fun open(host: String, port: Int, path: String, fingerprint: String, method: String, token: String?): HttpsURLConnection {
        val ctx = SSLContext.getInstance("TLS")
        ctx.init(null, arrayOf(object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = throw CertificateException("not a server")
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                val leaf = chain?.firstOrNull() ?: throw CertificateException("no certificate")
                if (!MessageDigest.isEqual(fingerprintOf(leaf).toByteArray(), fingerprint.uppercase().toByteArray()))
                    throw CertificateException("The PC's certificate is not the one that was paired")
            }
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }), null)
        val c = URL("https://$host:$port$path").openConnection() as HttpsURLConnection
        c.sslSocketFactory = ctx.socketFactory
        c.hostnameVerifier = HostnameVerifier { _, _ -> true }   // safe only because the certificate itself is pinned above
        c.connectTimeout = 8_000; c.readTimeout = 30_000
        c.requestMethod = method
        c.setRequestProperty("Accept", "application/json")
        if (token != null) c.setRequestProperty("Authorization", "Bearer $token")
        return c
    }

    private fun call(c: HttpsURLConnection, body: JSONObject?): Pair<Int, JSONObject?> {
        try {
            if (body != null) {
                c.doOutput = true
                c.setRequestProperty("Content-Type", "application/json")
                c.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            }
            val code = c.responseCode
            val text = (if (code in 200..299) c.inputStream else c.errorStream)?.bufferedReader()?.use { it.readText() }.orEmpty()
            return code to runCatching { JSONObject(text) }.getOrNull()
        } catch (e: javax.net.ssl.SSLException) {
            throw SyncException(SyncException.Kind.CertificateMismatch, e.cause?.message ?: e.message ?: "TLS error")
        } catch (e: IOException) {
            throw SyncException(SyncException.Kind.Unreachable, e.message ?: "cannot reach the PC")
        } finally { c.disconnect() }
    }

    fun hello(host: String, port: Int, fingerprint: String): String {
        val (code, json) = call(open(host, port, "/api/v1/hello", fingerprint, "GET", null), null)
        if (code != 200) throw SyncException(SyncException.Kind.Server, "HTTP $code")
        return json?.optString("server").orEmpty()
    }

    fun pair(link: PairingLink, phoneName: String, phoneId: String): PairResult {
        val body = JSONObject().put("code", link.code).put("phoneName", phoneName).put("phoneId", phoneId)
        val (code, json) = call(open(link.host, link.port, "/api/v1/pair", link.fingerprint, "POST", null), body)
        if (code == 403) throw SyncException(SyncException.Kind.Refused, json?.optString("error") ?: "pairing refused")
        if (code != 200 || json == null) throw SyncException(SyncException.Kind.Server, "HTTP $code")
        return PairResult(json.getString("token"), json.optString("serverName", link.serverName))
    }

    fun upload(host: String, port: Int, fingerprint: String, token: String, body: JSONObject): UploadResult {
        val (code, json) = call(open(host, port, "/api/v1/upload", fingerprint, "POST", token), body)
        if (code == 401) throw SyncException(SyncException.Kind.Revoked, "This phone is no longer paired with the PC (access revoked)")
        if (code != 200 || json == null) throw SyncException(SyncException.Kind.Server, json?.optString("error")?.ifBlank { null } ?: "HTTP $code")
        return UploadResult(json.optInt("received"), json.optInt("stored"), json.optInt("duplicates"), json.optInt("rejected"), json.optDouble("clockSkewSeconds", 0.0))
    }
}
