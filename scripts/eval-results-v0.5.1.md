# Validação — Aegis v0.5.1

Branch `fix/v0.5.1-recurring-events`, criada da `main` atualizada até `258b1f0` (merge da v0.5.0). Execução em 28/09/2026.

## Resultado

- `dotnet build backend/Aegis.sln --configuration Release --no-restore`: passou com o aviso preexistente xUnit2031 em `CalendarTests.cs`.
- `dotnet test backend/Aegis.sln --no-restore`: **325/325**, sem skips, usando PostgreSQL 16 descartável para os dois cenários de integração de Reminder. O banco e a rede temporários foram removidos após o teste.
- `npm test --prefix frontend/aegis-pwa`: **41/41**.
- `npm run build --prefix frontend/aegis-pwa`: passou, incluindo verificações TypeScript, Vite e service worker.
- `python3 -m py_compile scripts/eval_tool_intent.py` e `git diff --check`: passaram.

O SDK .NET não está no `PATH` do host; os comandos .NET foram executados com a imagem `mcr.microsoft.com/dotnet/sdk:8.0` e o código montado em `/work`. Nenhuma migração nova foi criada: os campos normalizados e a RRULE do Google ficam no payload JSON da ação Calendar pendente.

## Evals de intenção

Catálogo com as **28 tools reais** exportado por `scripts/Aegis.ToolCatalogExport`, modelo `gpt-5.6-luna`, `reasoning.effort=medium`, horário operacional fixo do eval. Dados Google, Reminder e push são simulados; nenhum evento real, lembrete real ou push foi enviado.

- Rodada final de criação e seleção: **7/7**. Academia segunda/quarta/sexta, aula terça/quinta informada como rotina, reunião quinzenal, aniversário anual e dois casos legados de criação usaram `calendar_create_event` com primeira ocorrência concreta e, nos quatro casos recorrentes, `recurrence` estruturada correta. Pedido de lembrete diário sem agenda não usou Calendar nem criou um lembrete único incorreto.
- Emenda de proposta recorrente: **1/1**. “Quarta também” usou `calendar_amend_pending_action` com os dias segunda e quarta, preservando a proposta pendente e sem confirmar no mesmo turno.
- Rodada exploratória de regressão: **20/21**. O único desvio pediu título adicional para “Cria uma reunião amanhã às 14h por uma hora”. A descrição da tool foi corrigida para deixar explícito que `Reunião` é título suficiente; o mesmo caso passou na rodada final. Confirmação Calendar, substituição de proposta, lembrete de evento e Reminder passaram nessa rodada.

## Verificações do recurso

Os testes de Calendar verificam RRULE diária, semanal em um e vários dias, quinzenal, mensal, anual, `COUNT`, `UNTIL` inclusivo no timezone do evento, séries sem término, eventos com horário e de dia inteiro, ordem canônica de dias, reminders nos quatro modos, agenda secundária e validação recuperável antes de criar pending action ou enviar mutação. A verificação pós-envio reconhece uma RRULE semanticamente igual apesar de ordem diferente dos componentes e de `INTERVAL=1` explícito. Timeout após inserção mais retry mantém uma única série. Emendas preservam, substituem, adicionam e removem recorrência antes da confirmação; update de master recorrente existente continua recusado e instâncias individuais continuam operáveis. A listagem mantém `singleEvents=true`.

A validação usa um Google HTTP fake que guarda o recurso exato enviado; a série gerada usa um único `events.insert` com `recurrence: ["RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR"]` para o caso de academia. Não houve teste de escrita numa conta Google real. A geração segue a [documentação oficial de recorrência do Google Calendar](https://developers.google.com/workspace/calendar/api/concepts/events-calendars) e a [RFC 5545](https://datatracker.ietf.org/doc/html/rfc5545#section-3.3.10) para `UNTIL` inclusivo.

## Escopo e higiene

Diff revisado: contratos/tool Calendar, conversão Google, testes/evals, prompt, documentação e metadados ativos de versão. Nenhuma migration, configuração sensível ou segredo foi adicionado; o relatório histórico da v0.5.0 permanece intacto. Não há suporte novo para editar/excluir séries existentes nem para “esta e as próximas”.
