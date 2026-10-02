# Aegis native client

Tauri 2 + Vue client for Android (priority) and Windows, sharing the v0.6.1 implementation with the Web Client in `packages/aegis-client`. Web Push/service worker remain web-only.

Install dependencies from the repository root:

```sh
npm ci --prefix packages/aegis-client
npm ci --prefix apps/aegis-node
cd apps/aegis-node
```

- Windows development: `npm run dev:desktop`
- Windows installer: `npm run build:desktop` (requires the updater signing key; CI uses an explicit unsigned updater-artifacts override)
- Android development: `npm run dev:android`
- Android debug APK: `npm run build:android`
- Signed Android release APK: `npm run build:android:release`
- Checks: `npm run check`, `npm run build`, `npm test`, `npm run test:rust`, `npm run test:release`

Official backend default: `https://aegis.phcastello.com`. Optional `AEGIS_NODE_API_BASE_URL` in the shell or ignored `.env.local` is compiled by Rust and supplied once to the shared client. Release builds require HTTPS. LAN development needs a reachable origin (Android localhost is the phone), a matching explicit `AEGIS_CORS_ORIGINS` entry for the Vite frontend origin, and a secure development origin for microphone capture. Use a trusted HTTPS development origin or test capture in the bundled debug APK; HTTP LAN Vite pages are not secure contexts.

Before changing versions: edit package.json, then `npm run version:sync`. Run `npm run version:check` to detect drift.

See [Stage 02 setup, signing and acceptance](../../docs/v0.7.0-stage02-client-parity.md) and [Stage 01 toolchain setup](../../docs/v0.7.0-stage01-tauri-foundation.md).
