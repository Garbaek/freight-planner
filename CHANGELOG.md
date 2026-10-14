# Changelog

## Unreleased

- Trade ideas now separate the reported maximum from a conservative **Likely SCU** and **Expected profit**. The estimate uses UEX user-trade averages plus the pilot's own actual purchase fills and buyer acceptance from earlier runs.
- Starting a run uses the likely load and its valid crate breakdown by default; untick **Start with likely SCU** to deliberately try the reported maximum.
- Routes have a Low / Medium / High / Unknown risk explanation using UEX sample counts, price volatility and route score. Hover the risk cell to see the evidence instead of trusting a single opaque ranking.
- Trade ideas rank by expected profit by default; **Maximum profit** remains available for comparing the theoretical upside.
- Windows and Android now ask for the actual SCU and unit price at purchase instead of silently treating the route estimate as fact. The plan and the real cargo lot are kept separately.
- **Record sale** can be used repeatedly: a terminal may buy only part of the cargo, the remaining SCU stays visible, and the next transaction can use the planned buyer or another UEX-priced buyer. The run completes only when all bought SCU are accounted for.
- Optional personal UEX trade-history sync adds the actual purchase and every actual sale transaction separately, retaining each returned UEX ID so retries do not duplicate a transaction.
- The trading journal stores sale legs, shows partial cargo lots and calculates completed revenue from the exact transactions. Existing completed rows migrate to a single sale leg automatically.
- A separate opt-in community tracker sends completed runs to the Docker-hosted account server. The website shows total sales volume, SCU, completed runs, participating pilots and a top-ten leaderboard; retries are idempotent and opting out hides earlier contributions from public totals.
- The website now has ALTCHA-protected account creation followed by email verification. Windows and Android open that page for new accounts, while sign-in remains in the apps.
- Both apps have an off-by-default anonymous launch counter. The server keeps daily platform/version totals only, with no installation or account identifier and no per-launch event rows.

## 0.8.2 — 2026-10-11

Windows app only; the Android app stays at 0.8.0. If you sent reports to UEX with 0.8.0, check them: see the first and the last point.

### Market reporter

- **Sell prices are no longer reported as buy prices.** On a terminal seen from the side, the lit shop tab was looked for in the wrong place, so prices from LOCAL MARKET VALUE could land under Buy (and went to UEX that way, where they were declined). The same fault made crate sizes and the re-reading of prices miss on such screenshots. All three now look at the right spot.
- The side of a price is never guessed. It comes from the lit tab and from the headings in the list; when those disagree or cannot be read, the price goes in neither column and you type it where it belongs. Before, such a price started under Buy.
- A price moved to the column the screenshot contradicts is refused, and a price on a side UEX does not have for that terminal gets a remark before you send it. That is the check UEX itself reports as `price_swapped`.
- A price that lost its first digit (`,800` for 5,800) is no longer taken as a price, and prices whose unit was misread (`/5CU`) are read.
- Crate sizes follow a sloping row of boxes instead of cutting off its first box, which gave `2,4,8,16,24,32` for a full row. Where the boxes cannot be told apart the sizes are left empty.
- **Prices from your own cargo stay on your PC.** The left side of the terminal shows what you would be paid for what you carry, which is not always the price the terminal lists (39,258 against 39,000 for the same commodity on one visit), and shows nothing of the terminal's stock. Such a row's stock, status and crate cells are now shut, so the amount you carry cannot be typed in as the terminal's stock and sent to UEX. Report sell prices from the LOCAL MARKET VALUE tab.

### Good to know

- Nothing changed in `market.db` or in the settings: extract the ZIP into a new folder and copy your files across as before, `market.db` included.

## 0.8.0 — 2026-10-10

Everything since 0.6.0; there was no public 0.7.

### Trading (new)

- **Trade ideas.** Pick the station you are at and get what your ship can buy there and where to sell it, best profit first, with the outlay, the crates to ask for, and the source, age and confidence of each price. Illegal goods are marked.
- **Market reporter.** Reads a commodity-terminal screenshot: buy and sell prices, shop quantity, stock status and crate sizes, shown next to UEX's price. Each figure is read four times and counts when most readings agree. Every cell can be edited, and a row is used only once you have ticked it as checked.
- **Your own prices** are kept in `market.db` and can be used together with UEX's, instead of them, or not at all.
- **Submit to UEX** (optional, for UEX data runners). Shows exactly what will be sent and waits for your approval; nothing is sent on its own. Rows that break UEX's Data Runner guide are left out and named. The app token and secret key are stored in Windows Credential Manager.
- **Trading journal.** The runs you actually flew, with gross and net profit, return and profit per hour.
- **Market history.** Every saved observation, with the change against the one before it.

### Tablet

- **Trade runs on the tablet.** Send to tablet on the Trade ideas tab. The tablet shows what to buy and where to sell it, and a box for each step: purchased with auto loading or loaded manually, undocked, quantum travel done, jumped to the other system when the run crosses one, docked, unloaded, sold. It shows the time of each tick and how long the step took, the time spent, and aUEC per hour and per minute. The ticks go back to the PC and complete a row in the trading journal.
- A plan reaches the tablet only when you press **Send to tablet**, not while you are still choosing contracts.

### Planner

- A ship in `ships.json` can list its cargo grids. Crates then have to fit on the grid, so a 24 SCU job in 8 SCU crates is no longer offered for the Nomad. A load for a ship without grids is marked theoretical.
- Consolidated Outland Nomad added, with its grid.
- A commodity-terminal screenshot is no longer read as a contract.

### Android app 0.8.0

- Trade-run screen with the clock. Needs the Windows app 0.8.0 to send a run. Installs over 0.6.0 and stays signed in.

### Good to know

- The account server's protocol is unchanged, so 0.6.0 and 0.8.0 apps can be mixed. An older tablet app shows a trade run as a plain checklist without the clock.
- New local file: `market.db`. To update, extract the ZIP into a new folder and copy `settings.json`, `travel-times.json`, `ignored-images.json`, `active-session.json` and `plan-history/` across from the old one.
- The repository now has tests: `dotnet test tests\FreightPlanner.Tests`.

## 0.6.0 — 2026-10-04

First public alpha: the Windows planner, which turns hauling-contract screenshots into one packed route, and the Android checklist app.
