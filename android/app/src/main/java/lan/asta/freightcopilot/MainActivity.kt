package lan.asta.freightcopilot

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.google.zxing.integration.android.IntentIntegrator

class MainActivity : Activity() {
    private lateinit var accountPanel: ScrollView
    private lateinit var cockpitPanel: LinearLayout
    private lateinit var server: EditText
    private lateinit var displayName: EditText
    private lateinit var email: EditText
    private lateinit var password: EditText
    private lateinit var status: TextView
    private lateinit var submit: Button
    private lateinit var switchMode: Button
    private lateinit var signOut: Button
    private lateinit var resendVerification: Button
    private lateinit var pairingPanel: LinearLayout
    private lateinit var pairingCode: EditText
    private lateinit var pairPcButton: Button
    private lateinit var relinkPcButton: Button
    private lateinit var pairingStatus: TextView
    private lateinit var planTitle: TextView
    private lateinit var planSummary: TextView
    private lateinit var planEmpty: TextView
    private lateinit var planStops: LinearLayout
    private lateinit var vault: SessionVault
    private var registering = false
    private var demoMode = false
    @Volatile private var planPolling = false
    @Volatile private var planPollingGeneration = 0L
    private var lastPlanRevision = -1L
    private var activePlan: MobilePlan? = null
    private var activePlanIdentity = ""
    private val collapsedStops = mutableSetOf<Int>()
    private val collapsedSections = mutableSetOf<String>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        enterImmersiveMode()
        bindViews()
        vault = SessionVault(this)
        server.setText(vault.serverUrl())
        wireActions()
        showSessionOrForm()
    }

    override fun onResume() {
        super.onResume()
        enterImmersiveMode()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) enterImmersiveMode()
    }

    @Suppress("DEPRECATION")
    private fun enterImmersiveMode() {
        window.addFlags(
            WindowManager.LayoutParams.FLAG_FULLSCREEN or
                WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON
        )
        window.decorView.systemUiVisibility =
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
            View.SYSTEM_UI_FLAG_FULLSCREEN or
            View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
            View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
            View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
            View.SYSTEM_UI_FLAG_LAYOUT_STABLE
    }

    private fun bindViews() {
        accountPanel = findViewById(R.id.accountPanel)
        cockpitPanel = findViewById(R.id.cockpitPanel)
        server = findViewById(R.id.serverUrl)
        displayName = findViewById(R.id.displayName)
        email = findViewById(R.id.email)
        password = findViewById(R.id.password)
        status = findViewById(R.id.status)
        submit = findViewById(R.id.submit)
        switchMode = findViewById(R.id.switchMode)
        signOut = findViewById(R.id.signOut)
        resendVerification = findViewById(R.id.resendVerification)
        pairingPanel = findViewById(R.id.pairingPanel)
        pairingCode = findViewById(R.id.pairingCode)
        pairPcButton = findViewById(R.id.pairPc)
        relinkPcButton = findViewById(R.id.relinkPc)
        pairingStatus = findViewById(R.id.pairingStatus)
        planTitle = findViewById(R.id.planTitle)
        planSummary = findViewById(R.id.planSummary)
        planEmpty = findViewById(R.id.planEmpty)
        planStops = findViewById(R.id.planStops)
    }

    private fun wireActions() {
        submit.setOnClickListener { authenticate() }
        switchMode.setOnClickListener { setRegistering(!registering) }
        signOut.setOnClickListener { signOut() }
        findViewById<Button>(R.id.demoMode).setOnClickListener {
            demoMode = true
            openCockpit()
        }
        findViewById<Button>(R.id.leaveCockpit).setOnClickListener {
            if (vault.displayName().isNullOrBlank()) {
                demoMode = false
                stopPlanPolling()
                cockpitPanel.visibility = View.GONE
                accountPanel.visibility = View.VISIBLE
                showSessionOrForm()
            } else {
                AlertDialog.Builder(this)
                    .setTitle("Crew account")
                    .setMessage("Signed in as ${vault.displayName()} (${vault.email()}).")
                    .setPositiveButton("Sign out") { _, _ -> signOut() }
                    .setNegativeButton("Return", null)
                    .show()
            }
        }
        pairPcButton.setOnClickListener { pairWithPc() }
        relinkPcButton.setOnClickListener { relinkPc() }
        resendVerification.setOnClickListener { resendVerificationEmail() }
        findViewById<Button>(R.id.scanQrLogin).setOnClickListener { scanPcQrCode() }
        findViewById<Button>(R.id.scanQrPair).setOnClickListener { scanPcQrCode() }
    }

    private fun scanPcQrCode() {
        IntentIntegrator(this)
            .setDesiredBarcodeFormats(IntentIntegrator.QR_CODE)
            .setPrompt("Point the camera at the QR code shown by Pair phone in the Windows client")
            .setBeepEnabled(false)
            .setOrientationLocked(true)
            .initiateScan()
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        val scan = IntentIntegrator.parseActivityResult(requestCode, resultCode, data)
        if (scan == null) {
            super.onActivityResult(requestCode, resultCode, data)
            return
        }
        scan.contents?.let(::linkFromQr)
    }

    private fun linkFromQr(contents: String) {
        val uri = runCatching { Uri.parse(contents) }.getOrNull()
        val serverUrl = uri?.getQueryParameter("server").orEmpty()
        val token = uri?.getQueryParameter("token").orEmpty()
        if (uri?.scheme != "freightplanner" || uri.host != "link" || serverUrl.isBlank() || token.isBlank()) {
            showLinkMessage("LINK ERROR // THAT IS NOT A FREIGHT PLANNER PAIRING CODE", error = true)
            return
        }
        showLinkMessage("ESTABLISHING ENCRYPTED TELEMETRY LINK…", error = false)
        Thread {
            runCatching { ApiClient(serverUrl).linkWithQr(token) }
                .onSuccess { (session, device) -> runOnUiThread {
                    stopPlanPolling()
                    vault.save(serverUrl, session)
                    vault.savePairedDevice(device.sessionId, device.deviceName)
                    server.setText(serverUrl)
                    password.text.clear()
                    demoMode = false
                    openCockpit()
                } }
                .onFailure { error -> runOnUiThread {
                    showLinkMessage("LINK ERROR // ${(error.message ?: "PAIRING FAILED").uppercase()}", error = true)
                } }
        }.start()
    }

    // The scanner is reachable from both the sign-in form and the cockpit's pairing bar.
    private fun showLinkMessage(message: String, error: Boolean) {
        val color = if (error) Color.rgb(255, 153, 141) else Color.rgb(230, 200, 121)
        val target = if (cockpitPanel.visibility == View.VISIBLE) pairingStatus else status
        target.text = message
        target.setTextColor(color)
    }

    private fun resendVerificationEmail() {
        val mail = email.text.toString().trim()
        if (mail.isBlank()) {
            showError("Enter the account email first.")
            return
        }
        val serverUrl = server.text.toString().trim()
        resendVerification.isEnabled = false
        Thread {
            runCatching { ApiClient(serverUrl).resendVerification(mail) }
                .onSuccess { message -> runOnUiThread { resendVerification.isEnabled = true; showNotice(message) } }
                .onFailure { error -> runOnUiThread {
                    resendVerification.isEnabled = true
                    showError(error.message ?: "Request failed.")
                } }
        }.start()
    }

    private fun showNotice(message: String) {
        status.setTextColor(Color.rgb(101, 216, 222))
        status.text = message.uppercase()
    }

    private fun setRegistering(value: Boolean) {
        registering = value
        displayName.visibility = if (value) View.VISIBLE else View.GONE
        submit.text = if (value) "REGISTER" else "SIGN IN"
        switchMode.text = if (value) "EXISTING ACCOUNT" else "CREATE ACCOUNT"
        status.text = if (value) "PASSWORD REQUIREMENT // 12+ CHARACTERS" else ""
        status.setTextColor(Color.rgb(143, 177, 182))
    }

    private fun authenticate() {
        val serverUrl = server.text.toString().trim()
        val mail = email.text.toString().trim()
        val secret = password.text.toString()
        if (mail.isBlank() || secret.isBlank() || (registering && (displayName.text.isBlank() || secret.length < 12))) {
            showError(if (registering) "Display name, email, and a 12+ character password are required." else "Email and password are required.")
            return
        }
        setBusy(true)
        if (registering) {
            val name = displayName.text.toString().trim()
            Thread {
                runCatching { ApiClient(serverUrl).register(mail, name, secret) }
                    .onSuccess { message -> runOnUiThread {
                        setBusy(false)
                        setRegistering(false)
                        resendVerification.visibility = View.VISIBLE
                        showNotice(message)
                    } }
                    .onFailure { error -> runOnUiThread { setBusy(false); showError(error.message ?: "Account request failed.") } }
            }.start()
            return
        }
        Thread {
            runCatching { ApiClient(serverUrl).login(mail, secret) }
                .onSuccess { session ->
                    vault.save(serverUrl, session)
                    runOnUiThread {
                        password.text.clear()
                        setBusy(false)
                        resendVerification.visibility = View.GONE
                        demoMode = false
                        openCockpit()
                    }
                }.onFailure { error -> runOnUiThread {
                    setBusy(false)
                    // 403 on sign-in means the password was right but the email link has not been opened yet.
                    if (error is ApiException && error.statusCode == 403) resendVerification.visibility = View.VISIBLE
                    showError(error.message ?: "Account request failed.")
                } }
        }.start()
    }

    private fun openCockpit() {
        enterImmersiveMode()
        accountPanel.visibility = View.GONE
        cockpitPanel.visibility = View.VISIBLE
        findViewById<TextView>(R.id.connectionStatus).apply {
            val pairedName = vault.pairedDeviceName()
            text = when {
                demoMode -> "● OFFLINE DISPLAY DEMO"
                pairedName != null -> "● TELEMETRY LINK // $pairedName"
                else -> "● ACCOUNT READY // LINK REQUIRED"
            }
            setTextColor(if (demoMode) Color.rgb(230, 200, 121) else Color.rgb(101, 216, 222))
        }
        pairingPanel.visibility = if (!demoMode && vault.pairedDeviceId() == null) View.VISIBLE else View.GONE
        relinkPcButton.visibility = if (demoMode) View.GONE else View.VISIBLE
        pairingStatus.text = "Choose Pair phone in the Windows client, then scan its QR code or enter the six-digit code."
        pairingStatus.setTextColor(Color.rgb(175, 196, 199))
        when {
            demoMode -> renderPlan(demoPlan())
            vault.pairedDeviceId() != null -> startPlanPolling()
            else -> renderPlan(null)
        }
    }

    private fun pairWithPc() {
        val code = pairingCode.text.toString().trim()
        if (!code.matches(Regex("^[0-9]{6}$"))) {
            pairingStatus.text = "LINK ERROR // ENTER THE SIX-DIGIT PC CODE"
            pairingStatus.setTextColor(Color.rgb(255, 153, 141))
            return
        }
        pairPcButton.isEnabled = false
        pairingStatus.text = "ESTABLISHING ENCRYPTED TELEMETRY LINK…"
        pairingStatus.setTextColor(Color.rgb(230, 200, 121))
        Thread {
            runCatching { authorized { api, token -> api.pairPc(token, code) } }
                .onSuccess { device -> runOnUiThread {
                    vault.savePairedDevice(device.sessionId, device.deviceName)
                    pairingCode.text.clear()
                    pairPcButton.isEnabled = true
                    openCockpit()
                } }
                .onFailure { error -> runOnUiThread {
                    pairPcButton.isEnabled = true
                    pairingStatus.setTextColor(Color.rgb(255, 153, 141))
                    pairingStatus.text = "LINK ERROR // ${error.message ?: "PAIRING FAILED"}"
                } }
        }.start()
    }

    private fun relinkPc() {
        if (demoMode) return
        stopPlanPolling()
        vault.clearPairedDevice()
        lastPlanRevision = -1L
        renderPlan(null)
        pairingPanel.visibility = View.VISIBLE
        pairingCode.text.clear()
        pairingStatus.apply {
            text = "RESYNC READY // CHOOSE PAIR PHONE IN THE WINDOWS CLIENT, THEN SCAN ITS QR OR ENTER ITS CODE"
            setTextColor(Color.rgb(230, 200, 121))
        }
        findViewById<TextView>(R.id.connectionStatus).apply {
            text = "● ACCOUNT READY // NEW PC LINK REQUIRED"
            setTextColor(Color.rgb(101, 216, 222))
        }
        planSummary.text = "ACCOUNT SESSION PRESERVED // ESTABLISH A NEW WINDOWS TELEMETRY LINK"
        pairingCode.requestFocus()
    }

    private fun stopPlanPolling() {
        planPolling = false
        planPollingGeneration++
    }

    private fun signOut() {
        stopPlanPolling()
        val refresh = vault.refreshToken()
        val url = vault.serverUrl()
        vault.clear()
        showSessionOrForm()
        if (refresh != null) Thread { runCatching { ApiClient(url).logout(refresh) } }.start()
    }

    private fun showSessionOrForm() {
        cockpitPanel.visibility = View.GONE
        accountPanel.visibility = View.VISIBLE
        val name = vault.displayName()
        if (!name.isNullOrBlank()) {
            demoMode = false
            openCockpit()
            return
        }
        setRegistering(false)
        signOut.visibility = View.GONE
    }

    private fun startPlanPolling() {
        if (planPolling) return
        planPolling = true
        val generation = ++planPollingGeneration
        lastPlanRevision = -1L
        Thread {
            while (planPolling && generation == planPollingGeneration) {
                val sessionId = vault.pairedDeviceId() ?: break
                runCatching { authorized { api, token -> api.getPlan(token, sessionId) } }
                    .onSuccess { plan ->
                        if (plan == null) {
                            if (lastPlanRevision != 0L) runOnUiThread {
                                renderPlan(null)
                                planSummary.text = "LINK ACTIVE // WAITING FOR THE WINDOWS CLIENT TO PUBLISH A PLAN"
                            }
                            lastPlanRevision = 0L
                        } else if (plan.revision != lastPlanRevision) {
                            lastPlanRevision = plan.revision
                            runOnUiThread {
                                renderPlan(plan)
                                findViewById<TextView>(R.id.connectionStatus).apply {
                                    text = "● TELEMETRY LINK // ${vault.pairedDeviceName() ?: "PC"}"
                                    setTextColor(Color.rgb(101, 216, 222))
                                }
                            }
                        }
                    }
                    .onFailure { error ->
                        Log.e("FreightCopilot", "Plan poll failed", error)
                        if (error is ApiException && error.statusCode == 404) {
                            stopPlanPolling()
                            vault.clearPairedDevice()
                            runOnUiThread {
                                pairingPanel.visibility = View.VISIBLE
                                findViewById<TextView>(R.id.connectionStatus).apply {
                                    text = "● ACCOUNT READY // LINK REQUIRED"
                                    setTextColor(Color.rgb(101, 216, 222))
                                }
                                pairingStatus.text = "LINK EXPIRED // GENERATE A NEW PAIRING CODE"
                                pairingStatus.setTextColor(Color.rgb(255, 153, 141))
                                renderPlan(null)
                            }
                        } else {
                            runOnUiThread {
                                findViewById<TextView>(R.id.connectionStatus).apply {
                                    text = "● TELEMETRY SYNC ERROR"
                                    setTextColor(Color.rgb(255, 153, 141))
                                }
                                planSummary.text = "SYNC ERROR // ${(error.message ?: "PLAN REQUEST FAILED").uppercase()}"
                            }
                        }
                    }
                if (planPolling && generation == planPollingGeneration) runCatching { Thread.sleep(2_500) }
            }
            if (generation == planPollingGeneration) planPolling = false
        }.start()
    }

    private fun renderPlan(plan: MobilePlan?) {
        activePlan = plan
        planStops.removeAllViews()
        if (plan == null || plan.stops.isEmpty()) {
            activePlanIdentity = ""
            planTitle.text = plan?.title ?: "NO ACTIVE FLIGHT PLAN"
            planSummary.text = (plan?.summary ?: "PAIR WITH THE WINDOWS CLIENT TO RECEIVE ROUTE TELEMETRY").uppercase()
            planEmpty.visibility = View.VISIBLE
            return
        }

        val identitySource = buildString {
            append(plan.title).append('|').append(plan.summary)
            plan.stops.forEach { stop ->
                append('|').append(stop.location)
                stop.deliveries.forEach { append("|D:").append(it) }
                stop.pickups.forEach { append("|P:").append(it) }
                stop.packing.forEach { append("|K:").append(it) }
            }
        }
        val planIdentity = Integer.toUnsignedString(identitySource.hashCode(), 16)
        if (planIdentity != activePlanIdentity) {
            activePlanIdentity = planIdentity
            collapsedStops.clear()
            collapsedStops.addAll(plan.stops.indices.filter { it != plan.currentStopIndex })
            collapsedSections.clear()
        }
        val checkedItems = vault.checkedPlanItems(planIdentity)

        planTitle.text = plan.title.uppercase()
        planSummary.text = plan.summary.uppercase()
        planEmpty.visibility = View.GONE

        plan.stops.forEachIndexed { index, stop ->
            val current = index == plan.currentStopIndex
            val stopKeys = checklistKeys(index, stop)
            val stopMarker = checklistKey(index, "S", 0)
            val stopDone = checkedItems.contains(stopMarker) || (stopKeys.isNotEmpty() && stopKeys.all(checkedItems::contains))
            val card = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(16), dp(12), dp(16), dp(16))
                background = GradientDrawable().apply {
                    cornerRadius = dp(2).toFloat()
                    setColor(if (current) Color.rgb(16, 53, 59) else Color.rgb(8, 25, 29))
                    setStroke(dp(if (current) 2 else 1), if (current) Color.rgb(101, 216, 222) else Color.rgb(49, 90, 98))
                }
                layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
                    bottomMargin = dp(8)
                }
            }

            val header = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = android.view.Gravity.CENTER_VERTICAL
                setPadding(dp(4), dp(2), dp(4), dp(if (collapsedStops.contains(index)) 2 else 10))
                layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            }
            val stopCheck = CheckBox(this).apply {
                text = if (stopDone) "STOP ${stop.number.toString().padStart(2, '0')} COMPLETE" else
                    if (current) "CURRENT STOP" else "STOP ${stop.number.toString().padStart(2, '0')}"
                textSize = 11f
                setTextColor(if (stopDone) Color.rgb(124, 211, 177) else if (current) Color.rgb(101, 216, 222) else Color.rgb(143, 177, 182))
                typeface = Typeface.create("monospace", Typeface.BOLD)
                isChecked = stopDone
                layoutParams = LinearLayout.LayoutParams(dp(250), LinearLayout.LayoutParams.WRAP_CONTENT)
                setOnCheckedChangeListener { _, done ->
                    if (done) {
                        checkedItems.add(stopMarker)
                        checkedItems.addAll(stopKeys)
                    } else {
                        checkedItems.remove(stopMarker)
                        stopKeys.forEach(checkedItems::remove)
                    }
                    vault.saveCheckedPlanItems(planIdentity, checkedItems)
                    if (done) collapsedStops.add(index) else collapsedStops.remove(index)
                    syncChecklistUpdate(plan, index, "S", 0, done, "Stop ${stop.number} ${if (done) "complete" else "reopened"}")
                    renderPlan(plan)
                }
            }
            header.addView(stopCheck)
            header.addView(planText(stop.location.uppercase(), 20f, Color.rgb(243, 235, 201), true, 2).apply {
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
                    marginStart = dp(14)
                }
            })
            header.addView(Button(this).apply {
                text = if (collapsedStops.contains(index)) "OPEN  ▾" else "FOLD  ▴"
                textSize = 10f
                setTextColor(Color.rgb(175, 196, 199))
                typeface = Typeface.create("monospace", Typeface.BOLD)
                background = resources.getDrawable(R.drawable.mfd_button_outline, theme)
                stateListAnimator = null
                layoutParams = LinearLayout.LayoutParams(dp(112), dp(40))
                setOnClickListener {
                    if (!collapsedStops.add(index)) collapsedStops.remove(index)
                    renderPlan(plan)
                }
            })
            card.addView(header)

            if (collapsedStops.contains(index)) {
                planStops.addView(card)
                return@forEachIndexed
            }

            card.addView(View(this).apply {
                setBackgroundColor(if (current) Color.rgb(55, 117, 124) else Color.rgb(35, 69, 75))
                layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(1)).apply {
                    bottomMargin = dp(10)
                }
            })

            val operations = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = android.view.Gravity.TOP
                layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
            }
            val actions = listOf(
                ChecklistSection("D", "DROP OFF", stop.deliveries, Color.rgb(230, 200, 121)),
                ChecklistSection("P", "PICK UP", stop.pickups, Color.rgb(101, 216, 222)),
                ChecklistSection("K", "PACKING ORDER", stop.packing, Color.rgb(199, 151, 201))
            ).filter { it.items.isNotEmpty() }
            if (actions.isEmpty()) {
                operations.addView(planText("NO CARGO ACTIONS AT THIS STOP", 13f, Color.rgb(102, 135, 141), false, 8))
            } else {
                actions.forEachIndexed { actionIndex, action ->
                    addChecklistSection(operations, plan, planIdentity, checkedItems, index, action, actionIndex > 0)
                }
            }
            card.addView(operations)
            planStops.addView(card)
        }
    }

    private fun addChecklistSection(container: LinearLayout, plan: MobilePlan, planIdentity: String,
        checkedItems: MutableSet<String>, stopIndex: Int, section: ChecklistSection, addStartMargin: Boolean) {
        val sectionKey = "$stopIndex|${section.kind}"
        val itemKeys = section.items.indices.map { checklistKey(stopIndex, section.kind, it) }
        val completedCount = itemKeys.count(checkedItems::contains)
        val collapsed = sectionKey in collapsedSections
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(8), dp(16), dp(12))
            background = GradientDrawable().apply {
                setColor(Color.rgb(6, 20, 23))
                setStroke(dp(1), Color.rgb(35, 69, 75))
                cornerRadius = dp(1).toFloat()
            }
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
                if (addStartMargin) marginStart = dp(10)
            }
        }
        column.addView(Button(this).apply {
            text = "${section.heading}    $completedCount/${section.items.size}    ${if (collapsed) "▾" else "▴"}"
            textSize = 11f
            gravity = android.view.Gravity.START or android.view.Gravity.CENTER_VERTICAL
            setPadding(dp(8), 0, dp(8), 0)
            setTextColor(section.accent)
            typeface = Typeface.create("monospace", Typeface.BOLD)
            background = resources.getDrawable(R.drawable.mfd_button_outline, theme)
            stateListAnimator = null
            layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(38))
            setOnClickListener {
                if (!collapsedSections.add(sectionKey)) collapsedSections.remove(sectionKey)
                renderPlan(plan)
            }
        })
        if (!collapsed) section.items.forEachIndexed { itemIndex, item ->
            val itemKey = checklistKey(stopIndex, section.kind, itemIndex)
            column.addView(CheckBox(this).apply {
                text = item
                textSize = 14f
                setTextColor(if (checkedItems.contains(itemKey)) Color.rgb(124, 211, 177) else Color.rgb(216, 231, 231))
                typeface = Typeface.create("sans-serif-condensed", Typeface.NORMAL)
                setLineSpacing(0f, 1.16f)
                setPadding(0, dp(7), 0, dp(3))
                isChecked = checkedItems.contains(itemKey)
                if (isChecked) paintFlags = paintFlags or Paint.STRIKE_THRU_TEXT_FLAG
                setOnCheckedChangeListener { _, done ->
                    if (done) checkedItems.add(itemKey) else checkedItems.remove(itemKey)
                    if (!done) checkedItems.remove(checklistKey(stopIndex, "S", 0))
                    if (done && itemKeys.all(checkedItems::contains)) collapsedSections.add(sectionKey)
                    else if (!done) collapsedSections.remove(sectionKey)
                    vault.saveCheckedPlanItems(planIdentity, checkedItems)
                    syncChecklistUpdate(plan, stopIndex, section.kind, itemIndex, done, item)
                    renderPlan(plan)
                }
            })
        }
        container.addView(column)
    }

    private fun checklistKey(stopIndex: Int, kind: String, itemIndex: Int) = "$stopIndex|$kind|$itemIndex"

    private fun checklistKeys(stopIndex: Int, stop: MobilePlanStop): List<String> = buildList {
        stop.deliveries.indices.forEach { add(checklistKey(stopIndex, "D", it)) }
        stop.pickups.indices.forEach { add(checklistKey(stopIndex, "P", it)) }
        stop.packing.indices.forEach { add(checklistKey(stopIndex, "K", it)) }
    }

    private fun syncChecklistUpdate(plan: MobilePlan, stopIndex: Int, kind: String, itemIndex: Int, done: Boolean, label: String) {
        val sessionId = vault.pairedDeviceId() ?: return
        val payload = "FC1|${plan.revision}|$stopIndex|$kind|$itemIndex|${if (done) 1 else 0}"
        Thread {
            runCatching { authorized { api, token -> api.sendChecklistUpdate(token, sessionId, label, payload) } }
                .onSuccess { runOnUiThread {
                    findViewById<TextView>(R.id.connectionStatus).apply {
                        text = "● TELEMETRY LINK // ${vault.pairedDeviceName() ?: "PC"} // CHECKLIST SYNCED"
                        setTextColor(Color.rgb(101, 216, 222))
                    }
                } }
                .onFailure { error -> runOnUiThread {
                    findViewById<TextView>(R.id.connectionStatus).apply {
                        text = "● CHECKLIST SYNC ERROR"
                        setTextColor(Color.rgb(255, 153, 141))
                    }
                    planSummary.text = "CHECKLIST NOT SYNCED // ${(error.message ?: "REQUEST FAILED").uppercase()}"
                } }
        }.start()
    }

    private data class ChecklistSection(val kind: String, val heading: String, val items: List<String>, val accent: Int)

    private fun planText(textValue: String, size: Float, color: Int, bold: Boolean = false, topMargin: Int = 2): TextView =
        TextView(this).apply {
            text = textValue
            textSize = size
            setTextColor(color)
            typeface = Typeface.create(if (size <= 12f) "monospace" else "sans-serif-condensed", if (bold) Typeface.BOLD else Typeface.NORMAL)
            setLineSpacing(0f, if (size >= 14f) 1.16f else 1.08f)
            layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
                this.topMargin = dp(topMargin)
            }
        }

    private fun demoPlan(): MobilePlan = MobilePlan(
        revision = 1,
        title = "CURRENT FLIGHT PLAN",
        summary = "C2 HERCULES  //  72 / 696 SCU  //  3 STOPS  //  ~18 MIN",
        currentStopIndex = 1,
        currentStopCompleted = false,
        stops = listOf(
            MobilePlanStop(1, "Everus Harbor", 0, emptyList(), listOf("Medical supplies — 12 SCU"), listOf("Rear grid: medical supplies")),
            MobilePlanStop(2, "Area18 / Riker Memorial", 7, listOf("Medical supplies — 12 SCU"), listOf("Titanium — 60 SCU"), listOf("Center grid: titanium", "Keep forward lane clear")),
            MobilePlanStop(3, "Baijini Point", 5, listOf("Titanium — 60 SCU"), emptyList(), emptyList())
        )
    )

    private fun <T> authorized(action: (ApiClient, String) -> T): T {
        val api = ApiClient(vault.serverUrl())
        val access = vault.accessToken() ?: throw ApiException("Sign in again to continue.", 401)
        try {
            return action(api, access)
        } catch (error: ApiException) {
            if (error.statusCode != 401) throw error
            val refresh = vault.refreshToken() ?: throw error
            val session = api.refresh(refresh)
            vault.save(vault.serverUrl(), session)
            return action(api, session.accessToken)
        }
    }

    private fun showError(message: String) {
        status.setTextColor(Color.rgb(255, 153, 141))
        status.text = "ACCESS ERROR // $message"
    }

    private fun setBusy(busy: Boolean) {
        submit.isEnabled = !busy
        switchMode.isEnabled = !busy
        if (busy) status.text = if (registering) "CREATING CREW ACCOUNT…" else "AUTHENTICATING…"
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()
}
