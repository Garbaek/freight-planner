# Freight Planner

Freight Planner is a free fan-made helper for cargo hauling in Star Citizen. You take screenshots of hauling contracts, and the Windows app reads them with local OCR, combines contracts into one run that fits your ship and the tightest contract timer, and tells you which crates to pick up and where to put them. An optional Android app shows the run as a checklist on a tablet next to your screen.

- Download: https://freightplanner.cipherdispatch.com/ or the [Releases](../../releases) page
- HOTAS mapper: https://freightplanner.cipherdispatch.com/mapper/
- Demo video: https://youtu.be/xVkjCtxEPEQ
- What changed: [CHANGELOG.md](CHANGELOG.md)

This repository holds the source of the Windows app (repository root) and the Android app (`android/`). The small account server that links the two is run by the author and is not part of this repository.

It is an early alpha. Most of the code was written with AI coding assistants under the author's direction; the idea, the design, the in-game testing and the server are the author's.

## What it does not do

Freight Planner sends no keyboard, mouse or controller input, does not hook into the game and does not read game memory. It only reads the screenshot files you create yourself.

## Privacy and architecture

- Screenshots are cropped and processed only on your Windows PC.
- Windows' built-in `Windows.Media.Ocr` API performs OCR. No external OCR program or third-party OCR package is required.
- The app prefers the Windows English (United States) OCR language because the game UI is English, then falls back to a user-profile OCR language.
- All screenshots, OCR text, contract data, route calculations, and saved plans remain local.
- No AI server or language model is used by the app. It goes online for two things only, both optional: the account, and the **Trade ideas** window.
- **Trade ideas** reads from the public [UEX](https://uexcorp.space/) API: the list of terminals and commodities, every reported price (one request that names no station), and the trade routes out of the station you pick (that request holds the station's UEX id). Nothing is sent before you open the window. Answers are kept in `uex-cache/`, so nothing is asked for more than once per half hour, and requests are spaced to stay well under UEX's limit of 120 a minute.
- Your own prices, screenshots and trading journal stay in `market.db` next to the app. A price goes to UEX only when you press **Submit to UEX**, see exactly what will be sent, and approve it. Personal trade-history sync is separately opt-in: when enabled under Settings, ticking **Bought commodities** or **Sold** is the approval to add that transaction to your UEX account.
- The Freight Planner community trade tracker is another separate opt-in. If enabled while signed in, completed journal runs are uploaded to your Docker-hosted Freight Planner server and contribute to the website's total sales volume, SCU, completed runs and top-ten pilots. The server stores the route, ship, SCU, planned buy and sell prices, costs and timestamps; the public page shows only aggregate figures and participating display names. Turning it off hides that account's earlier runs from public totals.
- Account creation happens on the website, protected by self-hosted ALTCHA proof of work and email verification. The Windows planner still works locally without an account; one is needed for Windows-to-Android pairing and optional account-based community features.
- Anonymous launch counting is a separate, off-by-default switch in both apps. When enabled, an app opening sends only `windows` or `android` and the app version. The server immediately adds this to a daily aggregate: it stores no launch event, account, installation ID, IP, user agent, or exact time. The web server's ordinary access log may still record the request IP, as it does for other site requests.
- The UEX app token and secret key needed for submitting are stored in Windows Credential Manager, encrypted by Windows for your account. They are never written to `settings.json` or a log, and are sent to `api.uexcorp.uk` only.
- The Windows planner works without an account. An account is only needed to link a tablet. The server stores the account's email, display name and a salted password hash, and relays the route checklist between the two devices in memory.

## Using it

1. Run `FreightPlanner.exe`. On first start it searches the fixed drives for `StarCitizen\LIVE\screenshots` and fills in the screenshot folder. Use **Find** next to the folder box to search again.
2. In the game, open one contract with its reward and all primary objectives visible and press Print Screen. Repeat for the contracts you are considering.
3. In watch mode, OCR runs as new screenshots arrive and route planning starts after a short quiet period. **Scan recent screenshots** reads up to 20 images from the newest uninterrupted capture session.
4. Check the jobs you want to fly together and press **Plan checked jobs**, or leave all jobs unchecked and use **Plan suggested run**.
5. With no jobs checked, **Compare plans** compares **Highest reward**, **Safest deadlines** and **Shortest flight**.
6. **Open route view** shows the numbered loading plan. **Execute route** is a persistent accepted/collected/loaded/delivered checklist.
7. In execution mode, **Start next leg** and **Arrived** teach the app your door-to-door time for the current ship.
8. **Travel map & times** lets you inspect or edit manual and learned route times.
9. Double-click a contract, or use **Edit / inspect contract**, to compare the screenshot, the raw OCR text and the parsed fields, and to correct anything that was misread.
10. **Trade ideas** opens the trading window. Its first tab answers "I am at this station, what can I haul and earn from?": pick the station and it lists what the selected ship can buy there and where to sell it, best profit first, with the outlay and the crates to ask for. Illegal goods are marked in red. The other tabs are described under [Trading](#trading).

If Windows has no English OCR language, add one under **Settings > Time & language > Language & region**.

### Trading

The trading window turns the planner from a reader of UEX's community prices into something that also gives prices back.

**Trade ideas.** Prices come from UEX reports and from your own confirmed observations. The **Prices** box chooses between both together, UEX only, and your own only. With both, the newer of the two prices wins for each end of a run, and an own price older than 48 hours (changeable under Settings) no longer replaces UEX's. Cells that hold your own price are green; hover over a price to see where it came from, how old it is and what UEX has. **Source** names the origin of the buy and the sell price, **Age** is the age of the older one, and **Confidence** rates the prices. **Max SCU / Max profit** remain the theoretical result from the reported stock and demand; **Likely SCU / Expected** use UEX user-trade averages and your own actual outcomes. **Route risk** explains its sample counts, volatility, route score, how much you previously managed to buy, and how much that buyer accepted. Runs are ranked by expected profit and start with the likely load by default; untick **Start with likely SCU** to try the maximum. These are statistical aids, never live inventory. A run UEX does not list appears when you have seen both ends yourself.

**Start run** opens the Windows trade monitor and also puts the run on a linked tablet, where either checklist can drive the same progress; see [Tablet](#tablet).

**Market reporter.** Pick the terminal and either type prices in with **Add row**, or read a screenshot: **Newest screenshot** takes the latest one from the game's screenshot folder, **Import screenshot** any file. The rows read from the picture appear next to UEX's price, its age and the difference. Selecting a row zooms the picture in on the part it was read from. Every cell can be edited. Tick **Checked** on each row that matches the terminal, or use **Confirm all**, which leaves out rows where something was guessed (figures that did not read the same every time, a commodity name it did not recognize, a shop tab it could not tell). **Save** stores the rows; only checked rows are used by Trade ideas and can go to UEX, so Save asks before storing rows nobody has ticked. A saved row can still be ticked or unticked afterwards, here or on the Market history tab. **Submit to UEX** shows the exact data and waits for your approval.

- One OCR pass can misread a digit and look sure of it, so each row's price and quantity are read four times: once in the pass over the whole screenshot and three times from enlarged crops of that row. A figure counts when most readings agree; a row where they do not gets a remark naming the readings and has to be ticked by hand.
- The **BUY** tab gives what the terminal sells; **LOCAL MARKET VALUE** gives what it pays, with its shop quantity and stock status. Which of the two a row is comes from the lit tab and from the headings in the list (IN STOCK, IN DEMAND, NO DEMAND), and decides whether its price goes under Buy or Sell. When the two disagree or neither can be read, the price goes in neither column and you type it where it belongs. A price under the column the screenshot contradicts is refused, and one on a side UEX does not have for that terminal gets a remark: UEX's staff compare every report with its screenshot.
- Keep the two shop tabs in the picture: they show which side the prices are on, to the app and to UEX's staff alike. A terminal seen from the side can be read, but head-on reads best.
- A row that is cut off at the edge of the list is shown without a price, in red, and is left out when saving.
- A row is a price, not a transaction. The shop quantity is the terminal's stock; how much you bought or sold is never read into it. Your own goods on the left of the terminal give sell prices only, and those stay on your PC: they are what you would be paid, which is not always the price the terminal lists, and they come without the terminal's stock. UEX gets sell prices from the LOCAL MARKET VALUE tab.
- A screenshot of the purchase or sale confirmation is recognized as a transaction. **Log trade** puts it in the journal; it adds one price row, without stock.
- If the screenshot does not name the station, the commodities and prices are compared with UEX's price list. When no terminal is selected and exactly one fits best, it is selected for you to check; when the selected one fits badly, you are warned.
- **Quick report** on the Trade ideas tab does the first steps in one click: it takes the station you have selected and reads the newest screenshot.
- Reading the same screenshot a second time does not store its rows twice; ticks given the second time are added to the rows already stored.
- With **watching** switched on under Settings, screenshots taken while the window is open are read in the background. Commodity terminals wait behind a button on the reporter tab; nothing pops up and the game keeps the focus. Each file is read once.

**Trading journal.** The runs you actually flew: ship, route, commodity, actual SCU, buy price, one or more sale transactions, loading, travel and unloading time, operating costs, and from those the gross and net profit, the return on the money put into cargo, and the profit per hour. A partial sale keeps the unsold SCU open; the run completes only when every bought SCU has been sold. The statistics count completed cargo only, and profit per hour counts only runs with a recorded time. Route estimates never end up here. **Use my observed prices** fills the prices from your confirmed observations.

**Market history.** Every saved observation, newest first, with the change against the observation before it. What UEX had at the moment you saved is stored alongside, so the two can be compared later. An observation is never overwritten by a newer one.

**Settings.** UEX credentials, the screenshot folder, watching, how own prices are used, optional personal trade-history sync, and the separately optional Freight Planner website tracker. Enabling the website tracker uploads existing completed local runs and retries any failed uploads. To submit to UEX you need an app token from https://uexcorp.space/api/apps and your secret key from uexcorp.space > Account > Secret Key; price reports additionally require a UEX data-runner account. **Check with UEX** tells you the account state. Price reports are sent as tests (`is_production = 0`: a test of the connection, not a report) until you untick that box. A real report is not public at once: under UEX's Datarunner Program Agreement its staff review reports, usually within a day, and may decline or edit them, and repeated improper reports can get an account locked. Every report is sent with its screenshot. Personal trade-history sync always writes a real entry to your UEX account; it is off until you explicitly enable it.

UEX takes one list of crate sizes per report, so rows with different crate sizes go out as separate reports.

The app follows UEX's Data Runner Quick Guide (version 1.1), which says its rules hold for third-party apps too. Rows that break them are left out of a report, named with the reason, and stay on this PC:

- "Enter all required values for each commodity: inventory status, SCU amount, price per SCU." A row needs all three. The sell prices of your own goods and the price on a confirmation have no shop quantity or status, so they are for your own route planning only.
- "No screenshot means no valid report." A row typed in without a screenshot, or whose screenshot file is gone, is not sent.
- "Do not guess container sizes." Crate sizes go out only as read from the boxes on the terminal; UEX's own sizes, which Trade ideas borrows when you have none, are never sent back.
- A row that is not ticked as checked, a commodity already reported for the same terminal in the last five minutes, and an observation older than 30 days are not sent either.

The guide also asks for the screenshot to show the whole terminal and the correct location. The app cannot see to that for you: take the screenshot with the entire terminal in view and with the station picked under YOUR INVENTORIES. It tells you when the picture does not name the station.

### Tablet

Use **Account** to register or sign in. Registering sends a verification link to the email address, and the account can sign in only after that link is opened. Then choose **Pair phone**: Windows shows a QR code and a six-digit code. In the Android app, tap **SCAN PC QR CODE** on the sign-in screen; the scan signs the tablet in and links it. Both codes expire after 10 minutes and work once.

Nothing reaches the tablet until you press **Send to tablet**, so you can first check, uncheck or correct contracts in the Windows app. The tablet then keeps that plan while you go on changing things on the PC; only the current-stop marker follows along. The button reads **Sent to tablet ✓** while the tablet has the same checklist as the PC, and pressing it again re-sends it. The link has to be made again each time the Windows app is started.

**A monitored trade run.** In the trading window, select a row on the Trade ideas tab and press **Start run**. Windows opens a live trade monitor with what to buy and where, the price, outlay and crates; where to haul it and sell it, the sale and planned profit; a running clock; and the steps below. If a tablet is linked, it shows the same monitor. Tick on either screen and Windows acknowledges the shared state back to the other:

- **Bought commodities**
- **Auto loaded** or **Warehouse pickup** (one of the two; ticking one clears the other)
- **Packed**
- **Undocked / left planetary pad**
- **Quantum travel done**
- **Jumped to** the other system, only when the run ends in another system
- **Docked**
- **Unloaded**
- **Sold**

The clock starts when the run starts. Next to each ticked step both monitors show the time of the tick and how long the step took since the one before it; above the list they show the time spent and what the planned profit comes to per hour and per minute. The clock stops at **Sold**. A tick can be taken back. Tablet times are kept locally while Windows is unavailable and are acknowledged back into the shared run when its link is active.

At **Bought commodities**, Windows or Android asks what the terminal actually supplied and charged; that actual cargo lot starts an open journal row while the route estimate remains available for comparison. **Record sale** asks for the buyer, actual SCU and price. If demand only takes part of the load, the monitor shows what remains and lets the next sale use the same buyer or one of the suggested alternatives. The run completes after the final SCU is sold, with loading, travel and unloading time taken from the ticks; loading runs until **Packed**. Optional UEX history sync records the purchase and every sale separately and remembers each returned ID. A run that never gets as far as a confirmed purchase leaves nothing in the journal.

An unfinished run survives a Windows restart. Freight Planner reopens the monitor with its actual purchase, partial sales and checkpoint times, reconciles it with the local journal, and assigns a fresh tablet revision so an old command cannot be applied twice. Link the tablet again after Windows starts; the restored run is then published back to it. Clearing the hauling-contract batch does not clear a commodity run.

A trade run and a contract plan share the tablet: sending one replaces the other. An older tablet app shows the run as two stops with the steps as checklist items, without the clock, and with the run's raw data in the line under the title.

## How the planner decides

- Local code parses title, reward, SCU, cargo, container size, pickups, and destinations.
- Contract availability countdowns are read locally. Elapsed time since the screenshot is subtracted before every recommendation.
- Already accepted contracts showing **Contract Deadline N/A** are labelled **Accepted** and remain plannable.
- Common OCR character and numeric-separator errors are normalized.
- When several screenshots contain the exact same cargo manifest and route, a blank reward or title may be recovered from the matching readable copy. Conflicting values are never guessed.
- Repeated screenshots of the same contract are merged instead of creating duplicate freight.
- Missing or suspicious data (for example reward 200 or SCU 0) is visibly warned about and excluded from automatic selection.
- For a manually checked warning row, **Use contract anyway** is available when cargo quantity, pickup, and destination are readable. The resulting route is marked as a manual override.
- The selected ship capacity is enforced and the combined reward is calculated locally.
- The recommendation is one time-feasible next run, not every valid contract. It reserves a 10-minute margin and lists other valid contracts separately.
- Estimated run time combines route legs, stop handling, and the expected number of cargo containers. Countdowns refresh every 30 seconds.
- Route legs use `travel-times.json`. Recorded times influence both stop ordering and deadline estimates; unrecorded legs use clearly labelled defaults.
- Routes enforce every pickup before its delivery and require hub revisits for return cargo such as A→B plus B→A.
- Cargo instructions include exact crate decomposition, for example `7 SCU → 4 SCU + 3 SCU` when containers are limited to 4 SCU.
- The same inputs and travel-time table produce the same plan.
- The ship selector is loaded from `ships.json`. Edit that file to add ships without recompiling.
- `ships.json` lists the cargo `Grids` of every ship that has any: width, height and length in SCU cells, with `Count` for several grids of one size. They are the grids in the game files of the patch named at the top of the file. Next to **Capacity** the app says in a few words what the selected ship has; click it for every grid and for how many crates of each size the ship takes. The Nomad's 24 SCU bed is three cells wide, so it takes one 16 SCU crate or two 8 SCU crates, not three.
- The crates of the planned contracts must fit on the grids together, so a 24 SCU job in 8 SCU crates is not offered for the Nomad.
- A grid's `MaxCrateScu` keeps larger crates off it although its shape has room for them. The game files carry such a limit for every grid, but it is wrong for a number of ships, so `ships.json` has it only where UEX reports the same largest crate for the ship. Where UEX and the shape of the grid disagree, the ship's `Note` says so, and the click on the capacity line shows it.
- Crates are placed whole on grid cells. A crate may be turned onto any side, and must rest on the deck or on other crates over its whole base. Trade ideas says when a crate has to go on its side, for example `1 × 16 SCU + 2 × 4 SCU on their side` for the Nomad, and only offers a load it found a placement for.
- A ship without `Grids` is limited by its nominal SCU capacity only. Trade ideas marks such a load **theoretical**: nobody has checked that those crates physically fit. Where neither UEX nor you know the crate sizes a terminal offers, the load is marked **sizes unknown**.
- A commodity-terminal screenshot is not a contract and is left out of the contract list.

Local files written next to the app: `plan-history/` (every completed plan as JSON), `active-session.json` (the current batch and checklist), `ignored-images.json` (screenshots already read), `travel-times.json`, `uex-cache/` (the last UEX answers) and `market.db` (your observations and trading journal, SQLite).

## Building

Windows app, with the .NET 8 SDK (the feature band in `global.json`):

```powershell
dotnet build -c Release
dotnet publish FreightCopilot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

The app depends on two NuGet packages: QRCoder (the pairing code) and Microsoft.Data.Sqlite (`market.db`).

The tests cover the trade maths, the choice between UEX and own prices, the cargo grid, the market database, the UEX submission and the terminal-screenshot reader:

```powershell
dotnet test tests\FreightPlanner.Tests
```

An OCR and parser check can be run without opening the UI, for a contract screenshot and for a commodity-terminal screenshot:

```powershell
dotnet .\bin\Release\net8.0-windows10.0.19041.0\FreightPlanner.dll --self-test "path-to-screenshot.jpg" "ocr-report.json"
dotnet .\bin\Release\net8.0-windows10.0.19041.0\FreightPlanner.dll --market-self-test "path-to-screenshot.jpg" "terminal-report.json"
```

Android app: see [`android/README.md`](android/README.md).

## Limitations

- The contract panel is located by fixed screen proportions measured on a 16:9 monitor at 2560x1440. Other 16:9 resolutions read the same in tests with resized screenshots. Ultrawide and other aspect ratios probably do not work yet.
- OCR can only use text actually visible in a screenshot. If the game does not render a reward or quantity, the record is marked incomplete unless the same batch contains an exact manifest-and-route match with one unambiguous readable value.
- Scroll long objective lists into view and take another screenshot when necessary. Screenshots are treated as individual contract records.
- The route planner has no live planetary-distance database. It uses the editable travel-time table and always enforces pickup-before-delivery dependencies.
- Trip time is an estimate. Recording your own door-to-door times makes it progressively more accurate for your ship and flying style.
- Location spelling is normalized for common observed OCR errors, but unusual new locations may still need manual correction.
- The commodity terminal is read as it looks in Star Citizen 4.x and was measured on 2560x1440 screenshots of two terminal styles (Levski's and Pyro Gateway's): the BUY tab, the LOCAL MARKET VALUE tab, the player's goods, and a purchase confirmation. A sale confirmation was not available to test against. Where the app cannot tell whether a price is a buy or a sell price it says so and leaves the row unconfirmed.
- The loading time on the confirmation dialog is in a font Windows OCR does not read. It stays empty; type it into the journal.
- Crate sizes are read from the row of boxes under AVAILABLE CARGO SIZE. When a row is scrolled half out of view they stay empty.
- The trading window watches for new terminal screenshots only while it is open.
- The tablet still has to be linked again each time Windows starts. The active trade run is restored and republished after linking, but ticks made against the tablet's expired link while Windows was unavailable cannot reach the journal. During an active link, the shared checkpoint times come from the device on which each box was ticked.
- There is no password reset yet, and the Windows app does not stay signed in between starts.
- Release builds are unsigned, so Windows SmartScreen and Android warn before installing.

## Licence

Copyright (C) 2026 Garbæk IT

Freight Planner is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, version 3. It is distributed in the hope that it will be useful, but without any warranty. See [LICENSE](LICENSE) for the full text.

## Disclaimer

Freight Planner is an unofficial fan-made tool and is not affiliated with the Cloud Imperium group of companies. Star Citizen® is a registered trademark of Cloud Imperium Rights LLC.
