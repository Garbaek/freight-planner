# Freight Planner Android

The Android client supports account registration, sign-in, encrypted token storage through Android Keystore, and sign-out against the account server. After pairing with the Windows app, its immersive landscape MFD shows the current flight plan, active stop, packing order, pickups, and deliveries. It hides Android's system bars while active and never sends input to the game.

Open the `android` directory in Android Studio, let it install Android SDK 36, and sync the project. The build definition uses Android Gradle Plugin 9.0.1 with Gradle 9.1 and JDK 17 or newer. The full Gradle wrapper is included; both its JAR and the downloaded Gradle distribution are pinned to Gradle's published SHA-256 checksums.

```bash
./gradlew assembleDebug
```

The APK is written to `app/build/outputs/apk/debug/`.

The app refuses cleartext HTTP and expects a publicly trusted HTTPS certificate. `https://freightplanner.cipherdispatch.com` is the editable default.

## Signing

Without further setup, builds are signed with the local Android debug key. To sign with your own key, copy `keystore.properties.example` to `keystore.properties` and point it at your keystore; both files stay out of source control. Android only installs an update over an existing app when both are signed with the same key.
