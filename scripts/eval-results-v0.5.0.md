# Validação — v0.5.0

Branch `feat/v0.5.0-reminders`, criada após atualizar a `main` para `6ffc069`. Execução em 27/09/2026. Modelo preservado: `gpt-5.6-luna`, `reasoning.effort=medium`. Catálogo com **28 tools reais** exportadas do registry de produção, incluindo as quatro novas tools Reminder. Resultados das integrações são simulados; nenhum email, evento Google ou push real é enviado pelo eval.

## Resultado final

**147/147 casos aprovados** em uma execução CLI sequencial, com o catálogo e a identidade finais. As **114/114 regressões anteriores** passaram, assim como **33/33 casos adicionais**. O runtime do eval é fixo: 26/09/2026 às 12h em Brasília (15h UTC), para verificar explicitamente horário absoluto, amanhã e intervalos relativos.

A bateria verifica escolha semântica, argumentos, referências observadas, ambiguidade, limites de intervalo, preservação de texto/horário e resposta honesta quando notificações estão indisponíveis. Menções casuais a lembrar, aviso, agenda, evento, horário e prazos não criaram lembretes. “Me lembra disso amanhã às 18h” depois de aceitar uma sugestão criou somente após autorização suficiente. O caso de cancelamento ordinal usa contexto de referências de uma lista anterior; UUIDs arbitrários são rejeitados pelo stub e pelos testes backend.

Identidade avaliada (SHA-256): `4d0671115e69e06fc3c80710e3e2f40e7411e73223d2fc9504d7a2d8aa386348`.

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
