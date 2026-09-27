# Validação — Aegis v0.5.0 — "Knock Knock"

Branch `feat/v0.5.0-reminders`, criada após atualizar a `main` para `6ffc069`. Execução em 27/09/2026. Modelo preservado: `gpt-5.6-luna`, `reasoning.effort=medium`. Catálogo com **28 tools reais** exportadas do registry de produção, incluindo as quatro novas tools Reminder. Resultados das integrações são simulados; nenhum email, evento Google ou push real é enviado pelo eval.

## Resultado da implementação inicial

**147/147 casos aprovados** em uma execução CLI sequencial, com o catálogo e a identidade finais. As **114/114 regressões anteriores** passaram, assim como **33/33 casos adicionais**. O runtime do eval é fixo: 26/09/2026 às 12h em Brasília (15h UTC), para verificar explicitamente horário absoluto, amanhã e intervalos relativos.

A bateria verifica escolha semântica, argumentos, referências observadas, ambiguidade, limites de intervalo, preservação de texto/horário e resposta honesta quando notificações estão indisponíveis. Menções casuais a lembrar, aviso, agenda, evento, horário e prazos não criaram lembretes. “Me lembra disso amanhã às 18h” depois de aceitar uma sugestão criou somente após autorização suficiente. O caso de cancelamento ordinal usa contexto de referências de uma lista anterior; UUIDs arbitrários são rejeitados pelo stub e pelos testes backend.

Identidade original avaliada, antes do codinome (SHA-256): `4d0671115e69e06fc3c80710e3e2f40e7411e73223d2fc9504d7a2d8aa386348`.

## Casos novos

| Pedido | Tools observadas | Resultado |
| --- | --- | --- |
| Me lembra amanhã às 14h de entregar o trabalho. | reminder_create | PASS |
| Me lembra amanhã às 18h de comprar ração. | reminder_create | PASS |
| Me lembra daqui 20 minutos de tirar a pizza do forno. | reminder_create | PASS |
| Me lembra daqui 15 minutos de olhar o forno. | reminder_create | PASS |
| Me lembra às 18h de comprar leite. | reminder_create | PASS |
| Quais lembretes eu tenho? | reminder_list | PASS |
| Quais lembretes eu tenho essa semana? | reminder_list | PASS |
| Muda o lembrete da ração para 19h. | reminder_list, reminder_update | PASS |
| Troca o texto do lembrete da prova para levar documento e caneta. | reminder_list, reminder_update | PASS |
| Cancela meu lembrete da ração. | reminder_list, reminder_cancel | PASS |
| Cancela o segundo. | reminder_cancel | PASS |
| Cancela o lembrete da ração. | reminder_list | PASS |
| Me lembra de comprar leite. | nenhuma | PASS |
| Me lembra amanhã às 18h de comprar ração. | reminder_create | PASS |
| Me lembra disso amanhã às 18h. | reminder_create | PASS |
| Quais compromissos eu tenho amanhã? | calendar_list_events | PASS |
| Cria uma reunião amanhã às 14h por uma hora. | calendar_create_event | PASS |
| Coloca um alerta de uma hora antes nessa reunião. | calendar_list_events | PASS |
| Coloca um aviso de uma hora antes nessa reunião. | calendar_list_events | PASS |
| Tenho que entregar o trabalho amanhã. | nenhuma | PASS |
| Seria bom eu lembrar de comprar leite. | nenhuma | PASS |
| Meu professor disse que o trabalho vence amanhã. | nenhuma | PASS |
| Meu professor falou que a prova é semana que vem. | nenhuma | PASS |
| Preciso lembrar como resolve essa equação. | nenhuma | PASS |
| A palavra aviso leva acento? | nenhuma | PASS |
| Minha agenda de papel é azul. | nenhuma | PASS |
| Esse evento foi uma bagunça. | nenhuma | PASS |
| Meu horário de sono está péssimo. | nenhuma | PASS |
| Lembrar nomes é difícil para mim. | nenhuma | PASS |
| Ele deixou um aviso na porta. | nenhuma | PASS |
| Comprei uma agenda nova ontem. | nenhuma | PASS |
| O evento do filme me surpreendeu. | nenhuma | PASS |
| Você sabe o significado de horário? | nenhuma | PASS |

Os casos “coloca um alerta/aviso de uma hora antes nessa reunião” usam uma reunião que já contém popup de 60 minutos e email de 15 minutos. Consultar Calendar e informar que o alerta já existe é correto; o checker permite evitar uma mutação redundante e exige preservar os alertas existentes caso uma proposta seja preparada. Isso é diferente de pedir *somente* um alerta de uma hora, cujos testes anteriores continuam exigindo a alteração explícita.

## Rodada inicial e correções

A primeira rodada ficou em **143/146**. Um caso legado de Calendar (“me lembra uma semana antes também”) pediu outro horário porque a antecedência já tinha passado, confundindo a configuração relativa do evento com um Reminder absoluto. As descrições e a identidade foram esclarecidas: alertas relativos a evento observado continuam Calendar. Um caso novo de reunião pediu um título mais elaborado; a descrição Calendar passou a esclarecer que o nome simples da atividade já pode ser o título. O terceiro resultado era um erro do checker novo: a reunião já tinha o alerta solicitado, mas o checker exigia modificar e remover outro alerta. Esse caso foi corrigido conforme a semântica acima e ganhou a variação “aviso”. Todos os critérios anteriores foram preservados. A rodada final foi executada integralmente após esses ajustes.

Relatórios completos de trabalho: `/tmp/aegis-v050-eval.json` (inicial) e `/tmp/aegis-v050-final-eval.json` (final). Logs: `/tmp/aegis-v050-eval.log` e `/tmp/aegis-v050-final-eval.log`. Catálogo final: `/tmp/aegis-v050-final-tools.json`. Esses arquivos contêm fixtures e respostas sintéticas, sem credenciais. A avaliação é probabilística; uma execução aprovada não garante a mesma resposta em todas as chamadas futuras.

```bash
dotnet run --project scripts/Aegis.ToolCatalogExport --configuration Release > /tmp/aegis-v050-tools.json
python3 scripts/eval_tool_intent.py --tools-json /tmp/aegis-v050-tools.json --report-json /tmp/aegis-v050-eval.json
```

## Builds, testes e verificações

O host não tem `dotnet`; os comandos .NET usaram `mcr.microsoft.com/dotnet/sdk:8.0` com o repositório montado. O PostgreSQL de validação era um container descartável, independente de `aegis-postgres`, com schema isolado por execução. Os serviços existentes não foram atualizados.

- Backend: **289/289 testes passaram**, sem skips na execução com PostgreSQL. O build passou; permanece um warning xUnit2031 anterior em `CalendarTests.cs`.
- EF Core: migration `20260927130234_AddRemindersAndWebPush` aplicada no PostgreSQL descartável; `migrations has-pending-model-changes` confirmou ausência de mudanças pendentes.
- Testes novos cobrem criação, futuro/offset, timezone, texto/horário, cancelamento, TTL/referências/ordem de listagem, acknowledgement/opening, tokens vinculados à ação/reminder/expiração, configuração e subscriptions, criptografia/VAPID com HTTP simulado, payload limitado, retry/backoff, invalidação, múltiplos dispositivos, outcome desconhecido, restart/downtime e métricas de atraso.
- PostgreSQL real: duas conexões concorrentes produzem um único claim; lease ativa não é duplicada, expirada é recuperada; row lock impede reclaim durante envio mesmo após a expiração nominal; excluir fisicamente a conversa mantém o reminder com origem nula.
- Frontend: **16/16 testes passaram**. Typecheck Vue/TypeScript, typecheck do service worker e build Vite passaram, com `injectManifest` e 12 entradas de precache. O `/api` continua excluído do fallback de navegação.
- Chromium: PWA construída e service worker registrados; evento push sintético gerou notificação Aegis com texto e action OK; OK registrou acknowledgement, fechou e não abriu janela; clique no corpo registrou opening e focou a janela existente. Desktop e viewport mobile 390×844 sem erros de página ou overflow. Transporte/API foram simulados nesta verificação, não FCM real.
- `docker compose config --quiet` e `git diff --check` passaram.

```bash
dotnet test backend/Aegis.sln
# Para a integração PostgreSQL, configure um banco descartável:
AEGIS_REMINDER_TEST_DATABASE='Host=localhost;Database=reminders_test;Username=postgres;Password=...' dotnet test backend/Aegis.sln
npm test --prefix frontend/aegis-pwa
npm run build --prefix frontend/aegis-pwa
docker compose config --quiet
```

## Limites de validação

A entrega física por FCM com a aplicação fechada e Android/Chrome real ainda exige configurar VAPID, HTTPS e subscriptions reais. Não foi afirmada entrega ponta a ponta em dispositivo físico. O README contém o procedimento para validar desktop + Android, OK/body click e downtime nesse ambiente.

Push aceito pelo serviço não prova entrega, leitura ou reconhecimento. Uma aceitação externa seguida de crash antes de gravar o resultado permanece ambígua; o processamento preserva a tentativa, limita recovery/retries e usa tag estável com `renotify: false`. Não há promessa de exactly-once nessa janela. Nenhum LLM integra o caminho de disparo.

Recorrência, prioridade, reincidência, escalada, snooze, automações genéricas, monitoramento autônomo e decisões adaptativas foram deliberadamente excluídos da implementação.

## Hardening final — “Knock Knock”

Mantida a branch `feat/v0.5.0-reminders`; nenhuma branch nova ou alteração de schema foi necessária. O codinome oficial é **Aegis v0.5.0 — "Knock Knock"**. No identity prompt, apenas o título mudou; as regras e as 28 tools foram preservadas. O catálogo reexportado tem SHA-256 `1b57ae0fdbca7c3de4244979b802a09a995f70a2ffbc03afcb7c9a6506f41356`, idêntico ao catálogo da rodada inicial. A identidade com codinome tem SHA-256 `05ca5d849b95368d39d2a04018f4e3e7c11421221b19d219d260b7d81e39714c`.

### Diagnóstico e correções

O ambiente existente foi inspecionado sem alterar seus containers: `/api/notifications/configuration` retornou `enabled: false` tanto pela API quanto pelo proxy da PWA. As três variáveis VAPID estavam ausentes no container API e vazias/ausentes no `.env` ignorado; o volume Data Protection estava montado. Uma reprodução automatizada sobre a PWA publicada, com permissão pré-concedida pelo Chromium, confirmou `permission = granted`, worker registrado, nenhuma PushSubscription criada, backend desabilitado, UI inativa e a mensagem de configuração indisponível. A permissão foi simulada nessa reprodução; o estado/configuração da API era real. O dispositivo físico que apresentou o problema não foi inspecionado.

A causa encontrada nesse ambiente é a falta de configuração VAPID, combinada com a solicitação nativa antes da consulta de configuração. Conceder permissão não configurava o backend nem criava uma subscription. Também havia lacunas no código: ausência de confirmação de status após POST, ausência de reconciliação de revogação/subscription desaparecida e classificação genérica de `NotAllowedError` como permissão negada, mesmo após concessão.

A UI agora confirma toda a cadeia antes de ativar: configuração, permissão, worker pronto, subscription, POST e status ativo para o endpoint atual do navegador, com uma última checagem da permissão/subscription. A configuração é pré-carregada ao abrir; não há prompt automático. O estado inicial é reconstruído do browser e do backend. Credenciais locais permitem desabilitar um registro anterior quando a permissão foi revogada ou a subscription desapareceu; falha offline mantém essas credenciais para reconciliação posterior. Um endpoint desabilitado é substituído na próxima ativação explícita. O requisito de canal funcional de `reminder_create` permanece intacto.

As etapas têm os códigos internos `permission_denied`, `push_not_supported`, `service_worker_unavailable`, `push_subscription_failed`, `backend_push_not_configured`, `backend_registration_failed` e `subscription_inactive`. O console registra apenas o código, nunca exceções brutas, endpoints ou tokens. A espera por worker e as requisições de setup têm limites de 10 segundos: [`serviceWorker.ready` pode esperar indefinidamente](https://developer.mozilla.org/en-US/docs/Web/API/ServiceWorkerContainer/ready) quando não existe worker ativo. A UI recebe mensagens simples.

ACK é global. `AcknowledgedAt` encerra o processamento como `Triggered`, libera a lease e impede novos claims/listagem ativa. O processor consulta o estado sob row lock antes de criar uma tentativa e novamente antes de enviar. ACK na janela entre persistência da tentativa e envio registra `push_skipped_acknowledged` sem enviar. Histórico já concluído, falhas, `AcceptedAt` e `RetryAt` anteriores são preservados. Nenhum ACK vira cancelamento. Linhas antigas já reconhecidas também são excluídas de claims/listas, mesmo se ainda tiverem status de retry; um replay de ACK normaliza esse status sem mudar `AcknowledgedAt`.

### Validação automatizada do hardening

- `dotnet test backend/Aegis.sln`: **295/295**, zero falhas e zero skips, com PostgreSQL descartável habilitado. O SDK .NET 8 rodou em Docker porque não está instalado no host.
- `dotnet build backend/Aegis.sln --configuration Release`: passou, zero erros; permanece apenas o warning xUnit2031 anterior em `CalendarTests.cs`.
- EF `migrations has-pending-model-changes`: nenhuma mudança pendente. **Nenhuma migration nova**; a migration original da v0.5.0 permanece intacta.
- `npm test --prefix frontend/aegis-pwa`: **33/33**, zero falhas e zero skips.
- `npm run build --prefix frontend/aegis-pwa`: typecheck Vue/TS, typecheck do SW e build Vite passaram; precache de 12 entradas.
- Chromium da PWA construída: sete cenários de UI passaram (sucesso, denied, subscribe failure, POST failure, status inactive, permission revogada e subscription ausente). API/PushManager foram simulados. O caso feliz mostra o texto de sucesso somente após status confirmado; uma falha de subscribe com `NotAllowedError` após concessão não aparece como “Permissão negada”.
- Service worker real da build, com eventos/transporte simulados: push mostra Aegis + OK; OK registra ACK/fecha sem abrir; corpo registra abertura e foca a janela sem ACK. Viewports desktop e 390×844 sem overflow ou erros de página.
- `docker compose config --quiet` e `git diff --check`: passaram.

Testes novos cobrem registro real pelo controller → status → criação liberada; bloqueio sem configuração/subscription ativa; associação de status ao endpoint e token; ACK após aceite em A e 503/retry em B preservando todo o histórico; ACK enquanto `Processing`; linhas reconhecidas anteriores ao hardening; ACK idempotente sem alterar o timestamp/auditoria em replay; reload do key ring em um novo provedor mantendo tokens de ACK e de management. O teste PostgreSQL registra ACK por uma segunda conexão logo após o commit de aceite do primeiro dispositivo, enquanto o processor original ainda está ativo, e prova que o segundo envio não começa. Os testes anteriores de concorrência, lease, restart/downtime, atraso e outcome desconhecido continuam passando.

Logs de trabalho: `/tmp/aegis-v050-hardening-backend.log`, `/tmp/aegis-v050-hardening-backend-build.log`, `/tmp/aegis-v050-hardening-frontend-tests.log`, `/tmp/aegis-v050-hardening-frontend-build.log`, `/tmp/aegis-v050-hardening-model.log`. Reprodução e verificações de browser: `/tmp/aegis-v050-hardening-reproduction.json`, `/tmp/aegis-v050-hardening-browser-activation.json`, `/tmp/aegis-v050-hardening-browser-sw.json`. São evidências locais de validação; não fazem parte da aplicação.

### Arquivos alterados

- Backend: `NotificationsController.cs`, `Reminder.cs`, `ReminderStore.cs`, `ReminderProcessor.cs`.
- Testes backend: `NotificationApiTests.cs`, `ReminderTests.cs`, `ReminderPostgresTests.cs`.
- Frontend: `NotificationControl.vue`, `pushNotifications.ts`, `aegisApi.ts`; codinome em `AegisIdentityCard.vue` e `vite.config.ts`.
- Testes frontend: nova bateria `pushNotifications.test.mjs`; casos de setup foram movidos/expandidos de `reminderNotifications.test.mjs`, que mantém os testes de payload/click.
- Documentação: `README.md`, este relatório e o título de `aegis_identity.md`. `package.json` continua `0.5.0`; `name`/`short_name` continuam Aegis.

### Evals executados no hardening

Foram executadas três rodadas completas, com **147 casos, 28 tools e os mesmos critérios**. Nenhuma descrição, schema, regra de identidade ou checker foi alterado para tentar obter aprovação. Não houve uma rodada integral sem falhas; o critério de 147/147 em uma única execução permanece pendente.

| Execução | Total | Casos legados | Casos novos | Falhas |
| --- | --- | --- | --- | --- |
| Primeira rodada | 146/147 | 113/114 | 33/33 | Me lembra uma semana antes também. |
| Segunda rodada | 145/147 | 112/114 | 33/33 | Resume esse email da loja sobre as promoções de setembro.; Manda bala. |
| Terceira rodada | 146/147 | 114/114 | 32/33 | Cria uma reunião amanhã às 14h por uma hora. |

Na primeira rodada, o modelo leu Calendar mas pediu outro horário para “uma semana antes”, tratando a antecedência como passada. Na segunda, ofereceu um lembrete para promoções sem chamar nenhuma tool de criação e chamou confirmação Calendar para “Manda bala” sem proposta pendente; o stub não executou nenhuma ação. Na terceira, pediu um título para uma reunião de uma hora, em vez de usar o nome simples da atividade e preparar a criação.

Rechecagens isoladas, sem alterações: `calendar_reminder_add` **1/1**, `proactive_promotional` **1/1** e `clarify` **4/4**. A reunião da terceira rodada passou nas duas rodadas anteriores. Todos os 147 casos distintos tiveram uma execução aprovada, mas isso não equivale a uma rodada completa de 147/147 e não prova ausência estável de regressões. A última rodada passou nos **114/114 casos legados**; as falhas observadas são variações do modelo em esclarecimento/proatividade e não foram ocultadas ou toleradas pelo checker.

Relatórios completos: `/tmp/aegis-v050-hardening-eval.json`, `/tmp/aegis-v050-hardening-eval-final.json`, `/tmp/aegis-v050-hardening-eval-validation.json`; logs com os mesmos nomes e extensão `.log`. Rechecagens: `/tmp/aegis-v050-hardening-eval-calendar-recheck.json`, `/tmp/aegis-v050-hardening-eval-promotional-recheck.json`, `/tmp/aegis-v050-hardening-eval-clarify-recheck.json`. Nenhum Google/push real foi executado nessas avaliações.

### Aceitação física e limites preservados

**Web Push físico ainda NÃO validado.** Nenhum teste acima prova entrega pelo FCM com desktop ou Android fechados. A configuração VAPID do ambiente publicado continua pendente; nenhum segredo/default foi inventado, e os serviços publicados não foram atualizados neste trabalho. O README contém o roteiro completo para ativação HTTPS, entrega real, OK/body click, múltiplos dispositivos, downtime e recriação do container mantendo Data Protection. A versão só terá aceite físico completo depois dessa execução real.

Não há variáveis de ambiente novas. É necessário configurar as existentes `AEGIS_WEB_PUSH_SUBJECT`, `AEGIS_WEB_PUSH_PUBLIC_KEY` e `AEGIS_WEB_PUSH_PRIVATE_KEY`, manter o par e o volume de keys persistentes e recriar o container para receber a configuração. Permissão concedida não substitui esse setup.

Uma requisição push já iniciada pode terminar antes de o ACK obter o row lock; após o commit do ACK, nenhum novo envio começa. Uma aceitação externa seguida de crash antes do commit continua sujeita a duplicação ambígua: `push_outcome_unknown`, tag estável, `renotify: false`, tentativas persistidas e retries limitados foram preservados. Sem storage de management não é possível identificar com segurança um registro antigo; revogação enquanto a PWA está fechada só pode ser reconciliada quando ela reabre ou o serviço rejeita o endpoint. Notificações já visíveis em outros dispositivos não são removidas remotamente.

Recorrência, cron/RRULE, snooze, prioridade, reincidência, escalada, adaptação, scheduler genérico, automations, monitoramento autônomo/triggers externos e plataformas fora de Chromium/Android continuam excluídos. Não foi introduzido LLM no disparo nem infraestrutura de exactly-once.
