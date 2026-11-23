package com.unupunct.fleetmonitor.domain

/**
 * What the app thinks a device is, and why. [evidence] lists every clue used so the guess can be checked, not trusted blindly.
 * [confidence] is 0..1; "Unknown" with confidence 0 means nothing in the advertisement identified it.
 */
class Classification(val category: String, val confidence: Double, val vendor: String?, val evidence: List<String>) {
    val isKnown: Boolean get() = category != UNKNOWN
    val isLovense: Boolean get() = vendor == "Lovense"
    companion object { const val UNKNOWN = "Unknown" }
}

object DeviceClassifier {
    private class Clue(val category: String?, val vendor: String?, val confidence: Double, val evidence: String)

    private val companyVendors = mapOf(
        0x0006 to "Microsoft", 0x004C to "Apple", 0x0059 to "Nordic Semiconductor", 0x0075 to "Samsung", 0x0087 to "Garmin",
        0x00E0 to "Google", 0x0171 to "Amazon", 0x01DA to "Logitech", 0x012D to "Sony", 0x009E to "Bose",
    )

    /** GAP Appearance category (value >> 6) and, for HID, the sub-category (value & 0x3F). */
    private fun fromAppearance(a: Int): String? {
        val cat = a shr 6; val sub = a and 0x3F
        return when (cat) {
            1 -> "Phone"; 2 -> "Computer"; 3 -> "Watch / wearable"; 4 -> "Clock"; 5 -> "Display"; 6 -> "Remote control"
            7 -> "Eye-glasses"; 8 -> "Tag / tracker"; 9 -> "Tag / tracker"; 10 -> "Media player"; 11 -> "Barcode scanner"
            12 -> "Thermometer"; 13 -> "Heart-rate sensor"; 14 -> "Blood-pressure monitor"
            15 -> when (sub) { 1 -> "Keyboard"; 2 -> "Mouse"; 3 -> "Joystick"; 4 -> "Gamepad"; else -> "HID device (keyboard / mouse / controller)" }
            16 -> "Glucose meter"; 17 -> "Running / walking sensor"; 18 -> "Cycling sensor"; 49 -> "Pulse oximeter"; 50 -> "Weight scale"
            0 -> null
            else -> "Sensor / other (appearance 0x%04X)".format(a)
        }
    }

    private val serviceClues = mapOf(
        0x1812 to Clue("HID device (keyboard / mouse / controller)", null, 0.7, "advertises the HID service (0x1812)"),
        0x180D to Clue("Heart-rate sensor", null, 0.8, "advertises the Heart Rate service (0x180D)"),
        0x1816 to Clue("Cycling sensor", null, 0.8, "advertises the Cycling Speed and Cadence service (0x1816)"),
        0x1818 to Clue("Cycling sensor", null, 0.8, "advertises the Cycling Power service (0x1818)"),
        0x1809 to Clue("Thermometer", null, 0.75, "advertises the Health Thermometer service (0x1809)"),
        0x181A to Clue("Environmental sensor", null, 0.7, "advertises the Environmental Sensing service (0x181A)"),
        0x1802 to Clue("Tag / tracker", null, 0.4, "advertises the Immediate Alert service (0x1802), used by find-my-keys tags"),
        0xFEAA to Clue("Beacon", null, 0.7, "advertises Eddystone (0xFEAA)"),
        0xFD5A to Clue("Tag / tracker", "Samsung", 0.75, "advertises the Samsung SmartTag service (0xFD5A)"),
        0xFEED to Clue("Tag / tracker", "Tile", 0.75, "advertises the Tile service (0xFEED)"),
        0xFE2C to Clue("Earbuds / headset / accessory", "Google", 0.4, "advertises Google Fast Pair (0xFE2C)"),
    )

    private class NameRule(val regex: Regex, val category: String, val vendor: String?, val note: String, val confidence: Double = 0.75)

    private val nameRules = listOf(
        NameRule(Regex("^LVS[-_ ]", RegexOption.IGNORE_CASE), "Toy (Lovense)", "Lovense", "name follows the Lovense pattern \"LVS-...\"", 0.8),
        NameRule(Regex("^Govee", RegexOption.IGNORE_CASE), "LED light / controller", "Govee", "name starts with \"Govee\""),
        NameRule(Regex("YNLUX|YONGNUO|^YN\\d", RegexOption.IGNORE_CASE), "LED light / controller", "Yongnuo", "name contains the Yongnuo / YNLUX pattern"),
        NameRule(Regex("^(ILCE|DSC)-", RegexOption.IGNORE_CASE), "Camera", "Sony", "name follows the Sony camera pattern (ILCE- / DSC-)"),
        NameRule(Regex("MX (Master|Anywhere|Vertical)|Pebble Mouse|M\\d{3} ", RegexOption.IGNORE_CASE), "Mouse", "Logitech", "name matches a Logitech mouse"),
        NameRule(Regex("MX Keys|K\\d{3}|Craft", RegexOption.IGNORE_CASE), "Keyboard", "Logitech", "name matches a Logitech keyboard"),
        NameRule(Regex("^(JBL|Flip |Charge |Clip )", RegexOption.IGNORE_CASE), "Speaker", "JBL", "name matches a JBL speaker", 0.7),
        NameRule(Regex("AirPods|Galaxy Buds|WH-1000|WF-1000|Jabra|Buds", RegexOption.IGNORE_CASE), "Earbuds / headset / accessory", null, "name matches earbuds or a headset", 0.7),
        NameRule(Regex("Galaxy Watch|Mi (Smart )?Band|Amazfit|Fitbit|Forerunner|Apple Watch", RegexOption.IGNORE_CASE), "Watch / wearable", null, "name matches a watch or fitness band", 0.75),
        NameRule(Regex("keyboard", RegexOption.IGNORE_CASE), "Keyboard", null, "name says \"keyboard\"", 0.6),
        NameRule(Regex("mouse", RegexOption.IGNORE_CASE), "Mouse", null, "name says \"mouse\"", 0.6),
    )

    fun classify(name: String?, companyIds: Collection<Int>, services16: Collection<Int>, appearance: Int?): Classification {
        val clues = ArrayList<Clue>()
        if (appearance != null) fromAppearance(appearance)?.let {
            clues.add(Clue(it, null, 0.9, "advertises GAP appearance 0x%04X".format(appearance)))
        }
        for (s in services16) serviceClues[s]?.let { clues.add(it) }
        if (!name.isNullOrBlank()) nameRules.firstOrNull { it.regex.containsMatchIn(name) }?.let {
            clues.add(Clue(it.category, it.vendor, it.confidence, "\"$name\": ${it.note}"))
        }
        for (c in companyIds) companyVendors[c]?.let { v ->
            val note = if (v == "Apple") "manufacturer data is Apple's: could be a phone, watch, earbuds or tag, the data alone cannot say" else "manufacturer ID is $v"
            clues.add(Clue(null, v, 0.0, note))
        }

        // Combine: the most confident category wins; several clues agreeing raise confidence (noisy-OR).
        val genericHid = "HID device (keyboard / mouse / controller)"
        val specificHid = setOf("Keyboard", "Mouse", "Joystick", "Gamepad")
        val withCategory = clues.filter { it.category != null }
        val hidClues = withCategory.filter { it.category == genericHid }
        // "Keyboard" is a kind of HID device, so a generic HID clue supports a specific one instead of competing with it.
        val byCategory = withCategory.groupBy { it.category!! }.toMutableMap().also { m ->
            for (s in specificHid) if (m.containsKey(s)) m[s] = m.getValue(s) + hidClues
            if (specificHid.any { m.containsKey(it) }) m.remove(genericHid)
        }
        val best = byCategory.maxByOrNull { (_, v) -> v.maxOf { it.confidence } }
        val vendor = clues.firstOrNull { it.vendor != null && it.confidence > 0 }?.vendor ?: clues.firstOrNull { it.vendor != null }?.vendor
        val evidence = clues.map { it.evidence }
        if (best == null) return Classification(Classification.UNKNOWN, 0.0, vendor, evidence)
        val confidence = 1.0 - best.value.fold(1.0) { acc, c -> acc * (1.0 - c.confidence) }
        return Classification(best.key, minOf(confidence, 0.97), vendor ?: best.value.firstNotNullOfOrNull { it.vendor }, evidence)
    }
}
