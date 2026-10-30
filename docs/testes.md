# Desenvolvimento e testes

Execute os comandos a seguir na raiz do repositório com Node 22 ou 24 e npm.
Use os lockfiles, sem atualização geral de dependências.

```sh
npm ci --prefix packages/aegis-client
npm ci --prefix frontend/aegis-pwa
npm ci --prefix apps/aegis-node
node scripts/check-docs.mjs
node scripts/check-compose.mjs
npm run test:email --prefix packages/aegis-client
npm test --prefix frontend/aegis-pwa
npm test --prefix apps/aegis-node
npm run build --prefix frontend/aegis-pwa
npm run build --prefix apps/aegis-node
npm run test:release --prefix apps/aegis-node
dotnet test backend/Aegis.sln
```

O alias `npm run test:email --prefix frontend/aegis-pwa` delega ao pacote
compartilhado. Os três arquivos são explícitos para não depender do glob do shell
nem aceitar silenciosamente zero testes. `check-docs.mjs` confere scripts npm
documentados, alvos dos runners e links locais; não executa builds.

A API requer .NET SDK 8. Os testes PostgreSQL só são exercitados quando
`AEGIS_NODE_TEST_DATABASE`, `AEGIS_REMINDER_TEST_DATABASE` e
`AEGIS_MEMORY_TEST_DATABASE` apontam para banco **descartável**. Sem essas variáveis,
os testes de integração são ignorados; isso não comprova migrations nem persistência.
`.env` é lido pelo Compose, não automaticamente por `dotnet run`.

O teste de operação/migrations usa ainda `AEGIS_OPERATION_TEST_DATABASE`, aceitando
somente o banco descartável `aegis_operation_test`; veja [operação privada](operacao-privada.md).

Para paridade browser, instale Chromium com `npx playwright-core install chromium`
em `packages/aegis-client`. Em dois terminais na raiz, mantenha os servidores locais:

```sh
npm run preview --prefix frontend/aegis-pwa -- --host 127.0.0.1 --port 18092
npm run dev --prefix apps/aegis-node -- --host 127.0.0.1
```

Após os builds, em um terceiro terminal:

```sh
npm run test:browser --prefix packages/aegis-client
node apps/aegis-node/tests/update-notice-visual.mjs --output artifacts/maintenance/update-notice
node apps/aegis-node/tests/places-browser.mjs
node apps/aegis-node/tests/reminders-browser.mjs
node packages/aegis-client/tests/transcription-browser.mjs
node packages/aegis-client/tests/ux-stream-browser.mjs
node packages/aegis-client/tests/markdown-stream-browser.mjs
node frontend/aegis-pwa/tests/worker-upgrade.mjs
```

São navegadores reais com API privada em memória e IPC Tauri simulado. Não atestam
WebView2, Android físico, captura real, notificações do SO ou consumo do binário.
Os scripts Windows/Android em `packages/aegis-client/tests/` e os workflows nativos
exigem a plataforma correspondente. Aceite físico do microfone do proprietário
permanece pendente até um teste no computador afetado.

Os testes `ux-stream-browser.mjs`, `transcription-browser.mjs` e
`markdown-stream-browser.mjs` iniciam seus próprios servidores Vite e fixtures
isoladas. O primeiro controla chunks HTTP e respostas de histórico atrasadas para
verificar navegação, turnos, rascunhos e intenção de rolagem. O segundo usa áudio
sintético em MediaRecorder/WebAudio reais para revisar, enviar e cancelar STT;
não usa um provedor externo. O terceiro mantém as scrollbars do Chromium visíveis
e mede o layout durante o fade de Markdown para detectar overflow transitório.
Também podem ser executados por `test:ux-stream`, `test:transcription-browser` e
`test:markdown-stream` no pacote compartilhado.
