package lan.asta.freightcopilot

import org.json.JSONObject

// One box of a trade run. The key ("stop|kind|item") is where the Windows app expects the tick;
// steps that share an alternative are two ways of doing the same thing.
data class TradeStep(val id: String, val key: String, val label: String, val alternative: String)

// A "what can I haul from here" run sent from the Windows app: what to buy where, where to sell it,
// and the steps of flying it. The relay only knows flight plans, so the run arrives as JSON in the
// plan's summary, and its steps double as the plan's checklist items.
data class TradeRun(
    val runId: String,
    val ship: String,
    val commodity: String,
    val scu: Int,
    val crates: String,
    val buy: Double,
    val sell: Double,
    val from: String,
    val to: String,
    // Which office the terminal is in and where in the city, when the Windows app knows; a city has several terminals.
    val fromDetail: String,
    val toDetail: String,
    val distance: Double?,
    val source: String,
    val confidence: String,
    val steps: List<TradeStep>
) {
    val outlay: Double get() = scu * buy
    val sale: Double get() = scu * sell
    val profit: Double get() = sale - outlay

    // The steps in flying order, with the two ways of loading counted as the one that was used.
    fun milestones(done: Set<String>): List<TradeStep> {
        val usedAlternatives = steps.filter { it.alternative.isNotEmpty() && it.id in done }.map { it.alternative }.toSet()
        val shown = mutableSetOf<String>()
        return steps.filter { step ->
            when {
                step.alternative.isEmpty() -> true
                step.alternative in usedAlternatives -> step.id in done
                else -> shown.add(step.alternative)
            }
        }
    }

    companion object {
        // Null for an ordinary flight plan, whose summary is plain text.
        fun parse(summary: String): TradeRun? {
            if (!summary.trimStart().startsWith("{")) return null
            return runCatching {
                val json = JSONObject(summary)
                if (json.optInt("trade", 0) != 1) return null
                val stepsJson = json.getJSONArray("steps")
                TradeRun(
                    json.optString("run"), json.optString("ship"), json.getString("commodity"), json.getInt("scu"),
                    json.optString("crates"), json.getDouble("buy"), json.getDouble("sell"), json.optString("from"),
                    json.optString("to"), json.optString("fromDetail"), json.optString("toDetail"),
                    if (json.isNull("distance")) null else json.optDouble("distance"),
                    json.optString("source"), json.optString("confidence"),
                    List(stepsJson.length()) { index ->
                        val step = stepsJson.getJSONObject(index)
                        TradeStep(step.getString("id"), step.getString("key"), step.getString("label"), step.optString("alt"))
                    }
                )
            }.getOrNull()
        }
    }
}
