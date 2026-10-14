# Freight Planner Android

The Android client supports account registration, sign-in, encrypted token storage through Android Keystore, and sign-out against the account server. After pairing with the Windows app, its immersive landscape MFD shows the current flight plan, active stop, packing order, pickups, and deliveries. It hides Android's system bars while active and never sends input to the game.

Since 0.8 it also shows a trade run sent from the Windows app's Trade ideas tab: what to buy and where, where to sell it, and the steps of the run with a box each. Ticking a step records its time on the tablet and sends it to the PC, which writes the run into its trading journal. Above the steps the screen shows the time spent and what the planned profit comes to per hour and per minute. The run arrives as JSON in the plan's summary (`TradeRun.kt` reads it), so the relay server needs no change. In the offline demo, tap the title to switch between the flight plan and a trade run.

Open the `android` directory in Android Studio, let it install Android SDK 36, and sync the project. The build definition uses Android Gradle Plugin 9.0.1 with Gradle 9.1 and JDK 17 or newer. The full Gradle wrapper is included; both its JAR and the downloaded Gradle distribution are pinned to Gradle's published SHA-256 checksums.

```bash
./gradlew assembleDebug
```

The APK is written to `app/build/outputs/apk/debug/`.

The app refuses cleartext HTTP and expects a publicly trusted HTTPS certificate. `https://freightplanner.cipherdispatch.com` is the editable default.

## Signing

Without further setup, builds are signed with the local Android debug key. To sign with your own key, copy `keystore.properties.example` to `keystore.properties` and point it at your keystore; both files stay out of source control. Android only installs an update over an existing app when both are signed with the same key.
