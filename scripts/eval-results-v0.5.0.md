# Validação — Aegis v0.5.0 — "Knock Knock"

Branch `feat/v0.5.0-reminders`, criada após atualizar a `main` para `6ffc069`. Execução em 27/09/2026. Modelo preservado: `gpt-5.6-luna`, `reasoning.effort=medium`. Catálogo com **28 tools reais** exportadas do registry de produção, incluindo as quatro novas tools Reminder. Resultados das integrações são simulados; nenhum email, evento Google ou push real é enviado pelo eval.

Este relatório preserva as rodadas históricas. O passe documental antes do merge registra abaixo a validação automatizada atual e a aceitação física já demonstrada, separada dos subcenários ainda pendentes.

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

## Builds, testes e verificações da implementação inicial (histórico)

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

Os checks da implementação inicial desta seção usaram transporte simulado e não provaram entrega física. Posteriormente, VAPID foi configurado no ambiente HTTPS e houve entrega Web Push real no Android e acknowledgement persistido; a seção de aceitação física abaixo registra essas evidências separadamente dos cenários ainda não comprovados.

Push aceito pelo serviço não prova entrega, leitura ou reconhecimento. Uma aceitação externa seguida de crash antes de gravar o resultado permanece ambígua; o processamento preserva a tentativa, limita recovery/retries e usa tag estável com `renotify: false`. Não há promessa de exactly-once nessa janela. Nenhum LLM integra o caminho de disparo.

Recorrência, prioridade, reincidência, escalada, snooze, automações genéricas, monitoramento autônomo e decisões adaptativas foram deliberadamente excluídos da implementação.

## Hardening final — “Knock Knock”

Mantida a branch `feat/v0.5.0-reminders`; nenhuma branch nova ou alteração de schema foi necessária. O codinome oficial é **Aegis v0.5.0 — "Knock Knock"**. No identity prompt, apenas o título mudou; as regras e as 28 tools foram preservadas. O catálogo reexportado tem SHA-256 `1b57ae0fdbca7c3de4244979b802a09a995f70a2ffbc03afcb7c9a6506f41356`, idêntico ao catálogo da rodada inicial. A identidade com codinome tem SHA-256 `05ca5d849b95368d39d2a04018f4e3e7c11421221b19d219d260b7d81e39714c`.

### Diagnóstico e correções

No início do hardening, o ambiente foi inspecionado sem alterar seus containers: `/api/notifications/configuration` retornou `enabled: false` tanto pela API quanto pelo proxy da PWA. As três variáveis VAPID estavam ausentes no container API e vazias/ausentes no `.env` ignorado; o volume Data Protection estava montado. Uma reprodução automatizada sobre a PWA publicada, com permissão pré-concedida pelo Chromium, confirmou `permission = granted`, worker registrado, nenhuma PushSubscription criada, backend desabilitado, UI inativa e a mensagem de configuração indisponível. A permissão foi simulada nessa reprodução; o estado/configuração da API era real. O dispositivo físico que apresentou o problema não foi inspecionado naquela reprodução. O setup e a validação manual posteriores estão registrados na seção de aceitação física.

A causa encontrada nesse ambiente é a falta de configuração VAPID, combinada com a solicitação nativa antes da consulta de configuração. Conceder permissão não configurava o backend nem criava uma subscription. Também havia lacunas no código: ausência de confirmação de status após POST, ausência de reconciliação de revogação/subscription desaparecida e classificação genérica de `NotAllowedError` como permissão negada, mesmo após concessão.

A UI confirma toda a cadeia antes de considerar o canal ativo: configuração, permissão, worker pronto, subscription, POST e status ativo para o endpoint atual do navegador, com uma última checagem da permissão/subscription. No fluxo atual, autorização já concedida permite reconciliação e registro automático ao abrir a PWA; apenas o aviso contextual discreto acima do composer solicita permissão nativa após um clique explícito em **Permitir**. Não existe botão permanente de ativar/desativar notificações. Credenciais locais permitem desabilitar um registro anterior quando a permissão foi revogada ou a subscription desapareceu; falha offline mantém essas credenciais para reconciliação posterior. Registros desabilitados podem ser reparados pela reconciliação com permissão já concedida. O requisito de canal funcional de `reminder_create` permanece intacto.

As etapas têm os códigos internos `permission_denied`, `push_not_supported`, `service_worker_unavailable`, `push_subscription_failed`, `backend_push_not_configured`, `backend_registration_failed` e `subscription_inactive`. O console registra apenas o código, nunca exceções brutas, endpoints ou tokens. A espera por worker e as requisições de setup têm limites de 10 segundos: [`serviceWorker.ready` pode esperar indefinidamente](https://developer.mozilla.org/en-US/docs/Web/API/ServiceWorkerContainer/ready) quando não existe worker ativo. A UI recebe mensagens simples.

ACK é global. `AcknowledgedAt` encerra o processamento como `Triggered`, libera a lease e impede novos claims/listagem ativa. O processor consulta o estado sob row lock antes de criar uma tentativa e novamente antes de enviar. ACK na janela entre persistência da tentativa e envio registra `push_skipped_acknowledged` sem enviar. Histórico já concluído, falhas, `AcceptedAt` e `RetryAt` anteriores são preservados. Nenhum ACK vira cancelamento. Linhas antigas já reconhecidas também são excluídas de claims/listas, mesmo se ainda tiverem status de retry; um replay de ACK normaliza esse status sem mudar `AcknowledgedAt`.

### Validação automatizada do hardening (histórico)

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

Foram executadas três rodadas completas, com **147 casos, 28 tools e os mesmos critérios**. Nenhuma descrição, schema, regra de identidade ou checker foi alterado para tentar obter aprovação. Nessas três rodadas não houve uma execução integral sem falhas; os resultados permanecem registrados como evidência de variabilidade probabilística.

| Execução | Total | Casos legados | Casos novos | Falhas |
| --- | --- | --- | --- | --- |
| Primeira rodada | 146/147 | 113/114 | 33/33 | Me lembra uma semana antes também. |
| Segunda rodada | 145/147 | 112/114 | 33/33 | Resume esse email da loja sobre as promoções de setembro.; Manda bala. |
| Terceira rodada | 146/147 | 114/114 | 32/33 | Cria uma reunião amanhã às 14h por uma hora. |

Na primeira rodada, o modelo leu Calendar mas pediu outro horário para “uma semana antes”, tratando a antecedência como passada. Na segunda, ofereceu um lembrete para promoções sem chamar nenhuma tool de criação e chamou confirmação Calendar para “Manda bala” sem proposta pendente; o stub não executou nenhuma ação. Na terceira, pediu um título para uma reunião de uma hora, em vez de usar o nome simples da atividade e preparar a criação.

Rechecagens isoladas, sem alterações: `calendar_reminder_add` **1/1**, `proactive_promotional` **1/1** e `clarify` **4/4**. A reunião da terceira rodada passou nas duas rodadas anteriores. Todos os 147 casos distintos tiveram uma execução aprovada, mas isso não equivale a uma rodada completa de 147/147 e não prova ausência estável de regressões. A terceira rodada de hardening passou nos **114/114 casos legados**; as falhas observadas são variações do modelo em esclarecimento/proatividade e não foram ocultadas ou toleradas pelo checker.

Relatórios completos: `/tmp/aegis-v050-hardening-eval.json`, `/tmp/aegis-v050-hardening-eval-final.json`, `/tmp/aegis-v050-hardening-eval-validation.json`; logs com os mesmos nomes e extensão `.log`. Rechecagens: `/tmp/aegis-v050-hardening-eval-calendar-recheck.json`, `/tmp/aegis-v050-hardening-eval-promotional-recheck.json`, `/tmp/aegis-v050-hardening-eval-clarify-recheck.json`. Nenhum Google/push real foi executado nessas avaliações.

### Aceitação física e limites preservados

**Web Push real no Android e OK com acknowledgement foram validados após os ajustes operacionais e de UX.** A afirmação anterior de que nenhuma entrega física tinha sido validada ficou desatualizada. VAPID foi configurado por configuração privada no ambiente HTTPS publicado; API/PWA receberam os ajustes. Nenhuma chave privada ou credencial é incluída neste relatório. Os testes automatizados descritos anteriormente continuam sendo simulados e não são usados como prova de entrega física.

Não há variáveis de ambiente novas. O setup usa as existentes `AEGIS_WEB_PUSH_SUBJECT`, `AEGIS_WEB_PUSH_PUBLIC_KEY` e `AEGIS_WEB_PUSH_PRIVATE_KEY`; o par e o volume de Data Protection devem continuar persistentes. Mudanças de configuração exigem recriação do container. Permissão concedida não substitui esse setup.

Evidências verificadas no histórico manual e, novamente neste passe, por consulta somente de leitura ao PostgreSQL publicado, sem texto de lembretes, endpoints ou tokens:

| Cenário | Estado e evidência |
| --- | --- |
| Android Chrome/PWA: ativação, criação pelo chat e notificação real | Validado pelo relato do usuário na conversa `be02c911-eb16-4126-9047-e70837382560` e pelos registros do reminder `a4bedb69-2c22-46f6-ba8f-8548e7d3adae`. |
| OK real / acknowledgement | `TriggeredAt=2026-09-27T15:45:13.351022Z`, push HTTP 201 aceito às `15:45:14.983695Z`, `AcknowledgedAt=15:45:26.478601Z`; `OpenedAt` permanece nulo. Isso registra reconhecimento explícito, não prova leitura cognitiva. |
| Múltiplos dispositivos: aceitação técnica | Reminder `c55fc2bb-bc0c-46f6-b1f6-5c0c22fc63f9`: duas tentativas de dispositivos distintos, HTTP 201 às `16:05:07.448613Z` e `16:05:07.744418Z`; ACK global às `16:07:24.969360Z`. Não comprova que ambos exibiram a notificação nem uma falha física com retry pendente. |
| Desktop Brave: entrega visível e interações | Pendente de registro específico. A necessidade de **Use Google services for push messaging** foi identificada; aceitação técnica não substitui confirmar visualmente o desktop. |
| Clique no corpo | Pendente em dispositivo físico: abrir/focar a PWA, `OpenedAt != null`, sem ACK. Coberto com transporte/interação simulados. |
| Android com PWA fechada e OK sem abrir a PWA | A entrega real foi confirmada; fechamento prévio e comportamento de foco após OK não foram registrados como checklist físico independente. Permanecem para confirmação específica. |
| ACK interrompendo retry real em outro dispositivo | Pendente de induzir falha transitória física. Coberto nos testes, inclusive ACK durante processamento ativo com outra conexão PostgreSQL. |
| Downtime e recuperação no ambiente publicado | Pendente de teste físico com horário vencendo enquanto o backend está parado. Coberto automaticamente com relógio falso e PostgreSQL. |
| Recriação do backend/container e tokens já enviados | Pendente de clicar OK em notificação anterior à recriação, preservando VAPID e volume Data Protection. Reload do key ring e validação dos tokens têm cobertura automatizada. |
| Revogação externa da permissão / subscription desaparecida | Pendente de registrar esse fluxo em dispositivo físico. Reconciliação/desativação e recuperação offline são cobertas automaticamente. |

O badge monocromático da Aegis, o bitmap transparente para evitar o fallback do large icon, o botão OK, a tag estável e `renotify: false` foram preservados. A transparência do bitmap e a atualização do service worker foram verificadas em Chromium com push sintético; a aparência final no sistema Android não é inferida desse teste. O README mantém o roteiro dos cenários físicos restantes.

Uma requisição push já iniciada pode terminar antes de o ACK obter o row lock; após o commit do ACK, nenhum novo envio começa. Uma aceitação externa seguida de crash antes do commit continua sujeita a duplicação ambígua: `push_outcome_unknown`, tag estável, `renotify: false`, tentativas persistidas e retries limitados foram preservados. Sem storage de management não é possível identificar com segurança um registro antigo; revogação enquanto a PWA está fechada só pode ser reconciliada quando ela reabre ou o serviço rejeita o endpoint. Notificações já visíveis em outros dispositivos não são removidas remotamente.

Recorrência, cron/RRULE, snooze, prioridade, reincidência, escalada, adaptação, scheduler genérico, automations, monitoramento autônomo/triggers externos e plataformas fora de Chromium/Android continuam excluídos. Não foi introduzido LLM no disparo nem infraestrutura de exactly-once.

## Passe final de documentação e validação

Execução em **27/09/2026**, a partir do commit funcional `a55d381`, na mesma branch `feat/v0.5.0-reminders`. Este passe altera somente `README.md` e este relatório. Não modifica schema, migration, domínio, worker, claim/lease, retry, ACK, PushSubscription, tools, prompt comportamental ou UI. Não faz merge na `main`.

### Consistência de versão e UX

Verificados README/histórico, `AegisIdentityCard.vue`, `aegis_identity.md`, `vite.config.ts`, manifest gerado, `package.json`/lockfile, `AegisMetrics`, metadata de `ChatService` e `OpenAIResponsesClient`: versão **0.5.0**, com **"Knock Knock"** nos locais que apresentam codinome. Referências às versões anteriores no histórico e nos relatórios antigos permanecem como histórico. `name`/`short_name` continuam **Aegis**.

O aviso contextual **Permitir** acima do composer, feedback dispensável/temporário, reconciliação automática de permissão já concedida e tratamento de revogação foram conferidos no código e descritos corretamente. Nenhum toggle permanente foi reintroduzido. Badge monocromático, bitmap transparente, OK, tag estável, `renotify: false` e Background Sync permanecem intactos.

### Validação automatizada atual

O host continua sem SDK .NET no PATH. Os comandos .NET foram executados em `mcr.microsoft.com/dotnet/sdk:8.0`, com o repositório montado em `/workspace`. PostgreSQL 16 rodou em container descartável e rede isolada dos serviços publicados, com `AEGIS_REMINDER_TEST_DATABASE` apontando exclusivamente para esse banco. Nenhum restart, envio real de push ou alteração do banco publicado foi necessário neste passe.

| Check | Resultado |
| --- | --- |
| `dotnet test backend/Aegis.sln` com PostgreSQL habilitado | **296/296**, zero falhas, zero skips. |
| Cenários PostgreSQL dentro da suíte | **2/2**: migrations/claim atômico/lease/recovery/vida independente/disparo; ACK commitado entre envios por dispositivos impedindo o próximo envio do processor ativo. |
| `dotnet build backend/Aegis.sln --configuration Release` | Aprovado, zero erros; um warning preexistente xUnit2031 em `CalendarTests.cs:1138`. |
| `npm test --prefix frontend/aegis-pwa` | **41/41**, zero falhas, zero skips. |
| `npm run build --prefix frontend/aegis-pwa` | Aprovado; typechecks Vue/TypeScript e SW, Vite e injectManifest, 17 entradas de precache. |
| `docker compose config --quiet` | Aprovado. |
| `git diff --check` | Aprovado. |
| `dotnet ef migrations has-pending-model-changes` | Nenhuma mudança pendente. **Nenhuma migration nova**; `20260927130234_AddRemindersAndWebPush` permanece intacta. |
| Reexportação do catálogo de produção | **28 tools**, JSON idêntico ao catálogo do hardening. |

Comandos executados no SDK container e no host, respectivamente:

```bash
# SDK container, com AEGIS_REMINDER_TEST_DATABASE no banco descartável:
dotnet test backend/Aegis.sln --logger 'trx;LogFileName=premerge.trx' --results-directory /validation-output/aegis-v050-premerge-results
dotnet build backend/Aegis.sln --configuration Release
dotnet build scripts/Aegis.ToolCatalogExport --configuration Release
dotnet run --project scripts/Aegis.ToolCatalogExport --configuration Release --no-build > /validation-output/aegis-v050-premerge-tools.json
/tools/dotnet-ef migrations has-pending-model-changes --project backend/src/Aegis.Infrastructure --startup-project backend/src/Aegis.Api --configuration Release --no-build

# Host:
npm test --prefix frontend/aegis-pwa
npm run build --prefix frontend/aegis-pwa
docker compose config --quiet
git diff --check
python3 scripts/eval_tool_intent.py --tools-json /tmp/aegis-v050-premerge-tools.json --report-json /tmp/aegis-v050-premerge-eval.json
```

Evidências locais: `/tmp/aegis-v050-premerge-backend-tests.log`, `/tmp/aegis-v050-premerge-results/premerge.trx`, `/tmp/aegis-v050-premerge-backend-build.log`, `/tmp/aegis-v050-premerge-frontend-tests.log`, `/tmp/aegis-v050-premerge-frontend-build.log`, `/tmp/aegis-v050-premerge-model.log` e `/tmp/aegis-v050-premerge-tools.json`. O histórico de resultados anteriores acima não foi substituído por estes números.

### Rodada final dos evals

Executada uma rodada integral com `gpt-5.6-luna`, `reasoning.effort=medium`, os **147 casos** e as **28 tools** atuais. Os resultados das tools permanecem simulados; a avaliação não envia emails, eventos nem push. Catálogo reexportado, identidade e script/checkers foram conferidos sem alterações:

- Catálogo: SHA-256 `1b57ae0fdbca7c3de4244979b802a09a995f70a2ffbc03afcb7c9a6506f41356`.
- Identidade: SHA-256 `05ca5d849b95368d39d2a04018f4e3e7c11421221b19d219d260b7d81e39714c`.
- Script/checkers: SHA-256 `766b50882c217caf7b92f29baf07cd3c7ddcf80bca96160e3bacccb87dfa13a0`.

O histórico de **146/147, 145/147 e 146/147** do hardening permanece acima, incluindo **114/114 legados** na terceira rodada e execuções corretas para cada caso distinto. Não houve alteração de prompt, descrições ou checker, nem rechecagem seletiva neste passe. Uma execução perfeita, quando observada, não torna o modelo determinístico; tampouco combinar aprovações isoladas produz uma rodada perfeita.

| Rodada | Total | Legados | Novos |
| --- | --- | --- | --- |
| Passe final antes do merge | **146/147** | **113/114** | **33/33** |

Única falha: `proactive_promotional`, “Resume esse email da loja sobre as promoções de setembro.” As únicas chamadas foram `email_search` e `email_read`; não houve criação de Reminder ou evento. O modelo terminou o resumo oferecendo um lembrete para conferir cupons/vitrines. O checker atual rejeita essa oferta contextual dispensável. É a mesma classe de variação observada na segunda rodada do hardening; o caso passou em outras execuções já registradas. A falha permanece visível e não foi tolerada, removida ou reexecutada seletivamente.

Todos os **33 casos novos** passaram nesta rodada. A terceira rodada histórica de hardening continua tendo **114/114 legados**, enquanto esta rodada final tem **113/114**; esses resultados não são intercambiáveis. Todos os 147 casos distintos já tiveram pelo menos uma execução correta, sem que isso prove estabilidade ou elimine as falhas probabilísticas registradas.

Relatório completo: `/tmp/aegis-v050-premerge-eval.json` (SHA-256 `ac009f28d3ff123ad62c73c1aa59771f39ad55637378322667890817899a83ba`); log `/tmp/aegis-v050-premerge-eval.log`, com `RESULT 146/147 passed`. O processo retornou código 1 por essa falha semântica, sem erro de transporte/API. A ordem e a quantidade dos 147 resultados foram verificadas contra os casos atuais do script.

A branch permanece preparada para revisão/merge como **Aegis v0.5.0 — "Knock Knock"**, com as pendências físicas explicitadas. Nenhuma migration foi criada e nenhum merge, squash ou remoção da branch foi executado.
