package com.unupunct.fleetmonitor.domain

class AdStructure(val type: Int, val data: ByteArray)

/** Reads the raw advertising payload (a sequence of [length][type][data] structures) so identification can use what the device actually says. */
object AdParser {
    const val TYPE_UUID16_INCOMPLETE = 0x02
    const val TYPE_UUID16_COMPLETE = 0x03
    const val TYPE_SHORT_NAME = 0x08
    const val TYPE_COMPLETE_NAME = 0x09
    const val TYPE_SERVICE_DATA_16 = 0x16
    const val TYPE_APPEARANCE = 0x19

    fun hexToBytes(hex: String): ByteArray {
        val clean = hex.trim()
        if (clean.length % 2 != 0) return ByteArray(0)
        return ByteArray(clean.length / 2) { i -> clean.substring(i * 2, i * 2 + 2).toInt(16).toByte() }
    }

    /** Tolerant of truncated or padded payloads: a bad structure ends parsing, it never throws. */
    fun parse(payload: ByteArray): List<AdStructure> {
        val out = ArrayList<AdStructure>()
        var i = 0
        while (i < payload.size) {
            val len = payload[i].toInt() and 0xFF
            if (len == 0) break
            if (i + 1 + len > payload.size) break
            val type = payload[i + 1].toInt() and 0xFF
            out.add(AdStructure(type, payload.copyOfRange(i + 2, i + 1 + len)))
            i += len + 1
        }
        return out
    }

    /** GAP Appearance (little-endian 16 bit): says what kind of thing the device is, when it bothers to advertise it. */
    fun appearance(payload: ByteArray): Int? =
        parse(payload).firstOrNull { it.type == TYPE_APPEARANCE && it.data.size >= 2 }
            ?.let { (it.data[0].toInt() and 0xFF) or ((it.data[1].toInt() and 0xFF) shl 8) }

    /** 16-bit service UUIDs from the UUID lists and from service data, as integers (e.g. 0x1812). */
    fun services16(payload: ByteArray): Set<Int> {
        val set = LinkedHashSet<Int>()
        for (s in parse(payload)) when (s.type) {
            TYPE_UUID16_INCOMPLETE, TYPE_UUID16_COMPLETE ->
                for (i in 0 until s.data.size / 2) set.add((s.data[i * 2].toInt() and 0xFF) or ((s.data[i * 2 + 1].toInt() and 0xFF) shl 8))
            TYPE_SERVICE_DATA_16 ->
                if (s.data.size >= 2) set.add((s.data[0].toInt() and 0xFF) or ((s.data[1].toInt() and 0xFF) shl 8))
        }
        return set
    }

    /** 16-bit form of a standard Bluetooth SIG UUID (0000xxxx-0000-1000-8000-00805f9b34fb), or null for vendor UUIDs. */
    fun uuid128To16(uuid: String): Int? {
        val u = uuid.lowercase()
        return if (u.length == 36 && u.startsWith("0000") && u.endsWith("-0000-1000-8000-00805f9b34fb")) u.substring(4, 8).toIntOrNull(16) else null
    }

    fun localName(payload: ByteArray): String? =
        parse(payload).firstOrNull { it.type == TYPE_COMPLETE_NAME || it.type == TYPE_SHORT_NAME }
            ?.data?.toString(Charsets.UTF_8)?.takeIf { it.isNotBlank() }
}
