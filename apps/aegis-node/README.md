# Cliente nativo da Aegis

Aplicativo Tauri 2 para Android ARM64 e Windows x86_64, com a interface Vue de
`packages/aegis-client`. Cada instalação pareada é um Node; notificações nativas
usam Windows/FCM. O cliente web/PWA tem outro host.

## Preparar o ambiente

Use Node 22/24, npm, Rust estável e os pré-requisitos Tauri 2 da plataforma.
Windows exige Visual Studio Build Tools (C++) e WebView2. Android exige JDK 17,
SDK/NDK Android e o target Rust `aarch64-linux-android` (as versões exatas usadas
no build ficam no workflow `.github/workflows/aegis-node-ci.yml`).

Na raiz do repositório:

```sh
npm ci --prefix packages/aegis-client
npm ci --prefix apps/aegis-node
cd apps/aegis-node
```

Os comandos abaixo são executados em `apps/aegis-node`:

| Finalidade | Comando |
| --- | --- |
| Interface no navegador (IPC simulado somente nos testes) | `npm run dev` |
| Desenvolvimento Windows | `npm run dev:desktop` |
| Instalador Windows | `npm run build:desktop` |
| Desenvolvimento Android | `npm run dev:android` |
| APK Android de depuração | `npm run build:android` |
| APK Android de release | `npm run build:android:release` |
| Tipos e identidade de versão | `npm run check` |
| Bundle da interface | `npm run build` |
| Testes JavaScript/TypeScript | `npm test` |
| Contratos de distribuição | `npm run test:release` |
| Rust sem recursos desktop | `npm run test:rust` |
| Rust desktop na plataforma suportada | `npm run test:rust:desktop` |

Builds de distribuição exigem as chaves existentes de assinatura do updater/APK;
a CI usa identidade isolada para os testes. Não substitua chaves nem publique
binários de teste como instaladores oficiais. Fixtures devem ficar fora da distribuição.

## Backend e captura

O endereço padrão do backend é compilado pelo Rust. Para desenvolvimento, configure
`AEGIS_NODE_API_BASE_URL` no shell ou `.env.local` ignorado. Release exige HTTPS.
O localhost do Android é o telefone; use origem alcançável e inclua a origem Vite
em `AEGIS_CORS_ORIGINS`. A captura no navegador exige contexto seguro; para LAN,
use HTTPS confiável ou teste no APK de depuração. Configurar uma URL não pareia o Node.

## Versão, testes e histórico

Edite a versão de `package.json` e execute `npm run version:sync`; confirme com
`npm run version:check`. Esta manutenção preserva a versão publicada 0.7.1.

Veja [testes e alcance das evidências](../../docs/testes.md). Os relatos de
[fundação](../../docs/v0.7.0-stage01-tauri-foundation.md) e
[paridade](../../docs/v0.7.0-stage02-client-parity.md) preservam decisões antigas;
o [registro integrado](../../docs/v0.7.0-completion.md) reúne as etapas posteriores.
