# Pinned upstream Android OS credential backend

Source: crates.io `android-native-keyring-store` **1.0.0**, archive SHA-256
`48c6349ddff23194f8fdce2ea8849380f5a4868c1648965b70e801e104cba9b3`.
Upstream: https://github.com/open-source-cooperative/android-native-keyring-store
MIT/Apache licenses and source are retained. No cryptographic/storage layout change.

The upstream JNI wrapper returns `SharedPreferences.Editor.commit()`'s boolean,
but its store/credential callers ignore `false`. Android documents `false` as a
failed persistent write. Reporting that write as successful could finalize an
identity without a durable credential. This is a dependency error-propagation
bug, not a Tauri/Keystore limitation.

Changes relative to the published crate:

- `src/commit_result.rs`: pure check and two portable regression tests.
- `src/lib.rs`: register that check.
- `src/error.rs`: propagate `CommitFailure` as a native platform failure.
- `src/shared_preferences.rs`: apply the check centrally for **every** commit,
  including configuration writes, credential writes and deletion.

The Aegis native manager also re-commits its checkpoint before **every** pairing
finalization. In-memory preferences can reflect a failed write, so readback alone
is not proof of durability. A failure-injection test covers this case.

The existing Keystore AES/GCM implementation, alias names, encrypted preference
format and public store API remain intact. No plaintext fallback, new vault
password, duplicate storage or JNI workaround is introduced. Android builds
must compile this pinned patch; the exact pure result check runs on desktop CI.
Physical Android write/restart/remove and failed-storage behavior still need
owner acceptance. Remove the Cargo patch only after an upstream release provides
verified equivalent failure propagation and preserves the store mapping.

Reference: https://developer.android.com/reference/android/content/SharedPreferences.Editor#commit()
