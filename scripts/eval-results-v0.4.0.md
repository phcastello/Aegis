# Eval de intenção de tools — v0.4.0 — “Booked!”

## Anotações nativas de eventos

Validado em 27/09/2026 (Brasília), com `gpt-5.6-luna`, `reasoning.effort=medium`, identity preservada e as **24 tools** exportadas do registry de produção em ordem determinística. Os **109 casos atuais passaram por grupos**: nove novos casos de anotações e duas regressões de 50 casos cada. O agregado foi comparado com todos os casos atuais, incluindo repetições, sem omissões. Não foi uma única invocação da suíte completa. Gmail/Calendar usam respostas stubadas; nenhuma operação Google real foi executada.

As criações curtas já possuem título e duração de uma hora no histórico; não dependem de inventar o término. A listagem compacta omite `description`, assim como o backend. O GET retorna `Preciso levar os exames.`; os updates simulados preservam essa anotação quando o campo é omitido. A proposta pendente usada no último caso já contém a mesma anotação.

| Pedido | Comportamento observado | Resultado |
| --- | --- | --- |
| Marca X amanhã às 14h. | Criação omitiu `description`. | PASS |
| Marca X amanhã às 14h e anota que preciso levar os exames. | Criação enviou `description: "Preciso levar os exames."`. | PASS |
| Anota que preciso chegar 15 minutos antes naquele evento. | Leu o evento e preparou somente a alteração da anotação. | PASS |
| Adiciona na anotação que tenho que levar documento. | GET antes do update; preservou os exames e acrescentou o documento. | PASS |
| Troca a anotação para 'levar RG e comprovante'. | Substituiu a anotação, sem manter o conteúdo anterior. | PASS |
| Remove a anotação desse evento. | Update enviou `description: ""`. | PASS |
| Muda o evento para 16h. | Omitiu `description`, preservando a anotação existente e a duração de uma hora. | PASS |
| Qual é a anotação dessa reunião? | Listagem para resolver o evento, GET e resposta com a anotação; sem mutação. | PASS |
| Troca a anotação para 'levar RG e comprovante' em uma criação pendente. | `calendar_amend_pending_action` alterou somente a anotação da proposta. | PASS |

Todas as alterações permaneceram pendentes, sem execução/confirm tool no mesmo turno. As operações sobre eventos encontrados preservaram `eval-diario`; criação normal permaneceu em `primary`. As preferências de lembretes não foram alteradas. Os 100 controles anteriores também verificaram que preparações sem pedido de anotação não enviaram `description` automaticamente. A interpretação de “anota”, “adiciona”, “troca” e “remove” pertence ao modelo; nenhum matcher linguístico foi adicionado ao backend ou à identidade.

Validação complementar: **254/254 testes backend**, **9/9 testes frontend de Email/OAuth** e build frontend aprovados. Os 17 casos backend adicionais cobrem criação sem descrição, anotação explícita persistida/lida, substituição/limpeza sem alterar os demais campos, acréscimo com resposta externa perdida sem duplicação, mudança de horário preservando a descrição integral, ETag após alteração concorrente, leitura truncada sinalizada, rejeição de `null` explícito/excesso de tamanho e emenda auditável de anotações pendentes. O limite existente de 8000 caracteres foi preservado; leituras maiores são sinalizadas e não devem ser usadas para reconstruir a anotação completa. Nenhuma migration, credencial, scope, tela ou scheduler adicional foi necessário.

```bash
dotnet run --project scripts/Aegis.ToolCatalogExport --configuration Release > /tmp/aegis-notes-tools.json
python3 scripts/eval_tool_intent.py --tools-json /tmp/aegis-notes-tools.json --report-json /tmp/aegis-notes-eval.json
```

Os relatórios completos incluem argumentos e valores efetivamente preservados/preparados. A avaliação continua probabilística e verifica seleção de tools e payloads; as operações reais com uma conta Google não foram executadas por ela. As seções abaixo registram as validações anteriores da mesma v0.4.0.

## Lembretes de eventos

Validado em 27/09/2026 (Brasília), com `gpt-5.6-luna`, `reasoning.effort=medium`, identity preservada e **24 tools** do registry de produção, em ordem determinística. Os **100 casos atuais passaram por grupos**: 77 regressões sem mudança de critério, 15 casos novos de lembretes, quatro controles de feriado/operação incompatível e quatro esclarecimentos. Não foi uma única invocação da suíte completa. As respostas Gmail/Calendar são stubadas; nenhuma operação Google real foi executada.

Os fixtures de criação curta fornecem previamente título e duração de uma hora; o pedido de dia inteiro substitui explicitamente o horário. O caso anterior sem duração e sem delegação continua exigindo dados antes da preparação. As leituras retornam lembretes de evento de 60 minutos (popup) e 15 minutos (email), e defaults reais do calendário de 30 minutos (popup) e 90 minutos (email), diferentes da policy Aegis.

| Pedido | Modo esperado | Resultado |
| --- | --- | --- |
| Marca amanhã às 14h. | `aegis_default` automático, sem enviar os cinco valores manualmente | PASS |
| Marca amanhã às 14h mas me lembra só uma hora antes. | `custom`, somente popup 60 | PASS |
| Marca amanhã às 14h sem lembrete. | `none` | PASS |
| Marca amanhã às 14h usando o padrão de lembretes da agenda. | `calendar_default` | PASS |
| Cria como dia inteiro. | `aegis_default`, policy all-day, datas sem timezone na chamada | PASS |
| Coloca os lembretes normais da Aegis nesse evento. | `aegis_default` | PASS |
| Coloca os lembretes normais. | `aegis_default` | PASS |
| Usa o padrão da agenda para os lembretes desse evento. | `calendar_default` | PASS |
| Tira os lembretes desse evento. | `none` | PASS |
| Me lembra desse evento 3 dias antes e na hora. | `custom`, incluindo popup 4320 e 0; pode preservar os existentes | PASS |
| Me lembra desse evento só 3 dias antes e na hora. | `custom`, somente popup 4320 e 0 | PASS |
| Me lembra uma semana antes também. | `custom`, adiciona popup 10080 e preserva os dois existentes | PASS |
| Muda esse evento pra 16h. | `keep`/omissão, mantém os lembretes | PASS |
| Quais são os lembretes desse evento? | Leitura pela listagem, que já retorna reminders | PASS |
| Quais são os lembretes padrão do calendário Diario? | Calendar List, informa 30 minutos e 1h30 reais | PASS |

A rodada final de criação all-day enviou `start=end=2026-09-27`, `allDay=true`, sem `timeZone` nem overrides manuais; o stub resolveu `3480, 600, 240`. A criação timed sem preferência também omitiu os campos de lembretes, resolvidos em `4320, 1440, 240, 60, 0`. Updates e mudanças de lembretes preservaram o calendário `eval-diario`. Todas as preparações permaneceram pendentes, sem confirmação no mesmo turno.

As rodadas intermediárias levaram a correções do harness: ele aceitava timezone em all-day, diferentemente do backend; isso passou a ser rejeitado e o schema de produção foi esclarecido. Foi corrigida também uma variável que substituía os argumentos CLI durante a avaliação. A consulta de reminders não exige um GET redundante quando a listagem já contém a informação. O critério distingue acréscimo de substituição exclusiva com “só” e aceita a apresentação de 90 minutos como 1h30.

Também foram corrigidos controles anteriores que confundiam forma textual com intenção: “Confirme para criar” e “Diga o que você quer que eu faça” são válidos mesmo sem `?`. O controle Gmail deixou de usar o ambíguo “pode criar” como rejeição obrigatória e passou a solicitar explicitamente um evento **em vez de** marcar o email como lido. Esse caso descartou a proposta Gmail e pediu os dados do evento, sem executar a modificação Gmail. Nenhum matcher linguístico ou regra adicional de identidade foi introduzido no backend.

Para reproduzir a suíte atual:

```bash
dotnet run --project scripts/Aegis.ToolCatalogExport --configuration Release > /tmp/aegis-reminders-tools.json
python3 scripts/eval_tool_intent.py --tools-json /tmp/aegis-reminders-tools.json --report-json /tmp/aegis-reminders-eval.json
```

`--kind` seleciona grupos sem alterar o catálogo. Os relatórios completos incluem argumentos, resultado simulado e reminders efetivamente resolvidos. A avaliação continua probabilística e verifica intenção/argumentos; não comprova entrega de notificações Google nem fidelidade completa de toda resposta textual.

Validação complementar: **237/237 testes backend**, **9/9 testes frontend de Email/OAuth**, build frontend e `docker compose config --quiet` aprovados. Os 43 casos backend adicionais cobrem policies por tipo, horários all-day em `America/Sao_Paulo`, preferências explícitas, preservação em updates/conversão de tipo, edição exclusiva de reminders, contexto/calendário de origem, leitura de defaults reais, limites/métodos/duplicatas/payloads incompletos, emendas, configuração tipada e verificação sem depender da ordem dos overrides. Os reminders ficam no payload JSON pendente; esta mudança não requer migration adicional nem scope novo.

## Confirmação natural, substituição e delegação

Validado em 27/09/2026 (Brasília), com `gpt-5.6-luna`, `reasoning.effort=medium` e **24 tools** exportadas do registry de produção em ordem determinística. Os **85 casos atuais passaram**, executados por grupos: 27 de confirmação/substituição/controles, 14 de cancelamento e 44 de leitura/delegação/regressão. Não foi uma única invocação da suíte completa. Gmail e Calendar usam resultados stubados; nenhuma operação Google real foi executada.

| Grupo | Casos aprovados | Comportamento observado |
| --- | --- | --- |
| Confirmação Calendar | 10/10 | O caso anterior `sim` e as nove formulações novas selecionaram `calendar_confirm_pending_action`. |
| Confirmação Gmail | 10/10 | O caso anterior `confirmo` e nove formulações novas selecionaram `email_confirm_pending_action`. |
| Cancelamento Calendar/Gmail | 14/14 | Sete formulações por integração selecionaram a respectiva cancel tool. |
| Substituição de proposta Calendar | 6/6 | Usou `calendar_amend_pending_action`, preservou campos omitidos e pediu confirmação da nova proposta, sem cancelamento separado ou execução. |
| Delegação explícita | 9/9 | Seis respostas curtas e três pedidos completos produziram parâmetros concretos válidos, preservaram datas/horários informados e prepararam a proposta sem pedir novamente o detalhe delegado. |
| Controles e regressão | 36/36 | Gmail, leituras multi-calendar, destino explícito, feriados, fluxo entre tools e esclarecimento quando falta informação sem delegação. |

As nove confirmações Calendar incluem `sim`, `pode`, `manda bala`, `vai`, `faz`, `beleza`, `confirmo`, `pode criar` e `é isso aí`. Gmail usa `pode marcar` no lugar de `pode criar`, pois a proposta é marcar um email como lido. `Pode criar` nesse contexto Gmail é um controle de esclarecimento separado. Os cancelamentos incluem `não`, `deixa`, `deixa quieto`, `cancela`, `melhor não`, `esquece` e `não faz isso`. Essas variações existem exclusivamente no eval; o backend não compara o texto com nenhuma lista.

Exemplos observados:

- `Manda bala` chamou a confirmação em Calendar e em Gmail.
- `Cancele a marcação do dia 28 de outubro e faça uma pro dia 27` emendou diretamente a proposta para 27/10, mantendo título, horário e calendário, sem excluir um evento Google.
- `Faz às 16h em vez disso` preservou a duração de uma hora e preparou 16h–17h.
- `Muda o nome pra X` alterou somente o título.
- A festa com início às 19h e término delegado recebeu um fim concreto escolhido pelo modelo; o evento de teste com horário delegado também foi preparado. O caso sem delegação continuou pedindo o término.

As rodadas iniciais expuseram dois problemas dos fixtures: `pode criar` não corresponde à operação Gmail de marcar como lido, e `deixa` após uma pergunta de permissão pode significar permitir a ação. O primeiro virou um controle negativo; os cenários de cancelamento passaram a apresentar uma proposta aguardando decisão após o usuário manifestar dúvida. Os 14 cancelamentos foram reexecutados e passaram nesse contexto. Nenhuma regra por frase foi adicionada às tools ou ao backend; a intenção continua dependendo do contexto interpretado pelo modelo.

Para reproduzir todos os casos atuais:

```bash
dotnet run --project scripts/Aegis.ToolCatalogExport --configuration Release > /tmp/aegis-pending-tools.json
python3 scripts/eval_tool_intent.py --tools-json /tmp/aegis-pending-tools.json --report-json /tmp/aegis-pending-eval.json
```

`--kind` pode ser repetido para executar grupos. Os relatórios JSON guardam tools, argumentos, resultados simulados, valores efetivos da proposta e resposta final. Os critérios conferem escolha da tool, parâmetros temporais completos, preservação de campos e ausência de cancelamento/execução indevidos durante a substituição. Não há instrução de confirmação por palavras específicas, delegação ou substituição no identity. O runtime apenas informa a proposta pendente depois da mensagem atual, como em produção.

Validação complementar: **194/194 testes backend**, **9/9 testes frontend de Email/OAuth** e build frontend aprovado. A migration `AddPendingActionSupersession` foi aplicada, revertida e reaplicada em PostgreSQL descartável, preservando registros Calendar/Gmail anteriores. Os testes cobrem identidade da mensagem/conversa, confirmação posterior, expiração, fechamento, supersession auditável e proteção de possíveis efeitos externos, inclusive após expiração.

## Rodada anterior de feriados

Tratamento semântico de feriados validado em 27/09/2026 (Brasília), com `gpt-5.6-luna`, `reasoning.effort=medium` e **23 tools** exportadas do registry de produção em ordem determinística. Resultado da suíte completa: **35/35**, incluindo todos os 32 casos anteriores de Gmail/Calendar e três casos novos de feriados. As respostas Google são stubadas; nenhuma leitura ou mutação Google real foi executada.

```bash
python3 scripts/eval_tool_intent.py --tools-json /tmp/aegis-holiday-tools.json
```

O fixture usa um feriado fictício, “Dia da Comunidade”, retornado pelas tools. Os pedidos não mencionam feriado: o modelo precisa interpretar `holidays` e `holidayWarnings` dos resultados. Não há instrução de feriados no identity nem no runtime. Na leitura, o modelo informou que não havia compromissos e distinguiu o contexto do dia. Na criação e alteração, mencionou o feriado e pediu confirmação, sem executar a ação.

| Pedido | Caso | Tools observadas | Resultado |
| --- | --- | --- | --- |
| Tenho algum compromisso no dia 10 de outubro? | calendar_holiday_read | `calendar_list_events` | PASS |
| Marca estudo dia 10 de outubro às 14h por uma hora. | calendar_holiday_create | `calendar_create_event` | PASS |
| Move aquela reunião para 10 de outubro das 16h às 17h. | calendar_holiday_update | `calendar_list_events,calendar_update_event` | PASS |

Exemplo observado na leitura: “Não há compromissos no dia **10 de outubro de 2026**. Há apenas o feriado/contexto **Dia da Comunidade**, que não ocupa horário na agenda.” Na criação: “A data coincide com o feriado **Dia da Comunidade**, mas isso não bloqueia o agendamento. Confirma?”

Os testes backend cobrem identificação pelo ID, separação entre compromissos e feriados, limite global, paginação dos avisos, timezone, fim exclusivo, atualização no calendário de origem e confirmação posterior permitida em feriado. A avaliação do modelo é probabilística e não substitui integração com uma conta Google real.

## Rodada anterior multi-calendar

Fechamento multi-calendar em 27/09/2026 (Brasília), com `gpt-5.6-luna`, `reasoning.effort=medium` e **23 tools** exportadas do registry de produção em ordem determinística. Resultado da rodada final: **32/32**. Gmail e Calendar usam respostas stubadas; nenhuma operação Google real foi executada.

```bash
python3 scripts/eval_tool_intent.py --tools-json /tmp/aegis-tool-catalog-multi.json
```

Além da intenção de tools, o eval verifica os argumentos: criação sem destino usa `primary`/omissão; `Diario` e `Family` precisam ser descobertos e seus IDs precisam acompanhar a criação; update/delete devem preservar o evento no calendário observado. Duas agendas chamadas “Faculdade” geram esclarecimento sem preparar mutação. O fluxo Gmail → contexto → oferta de Calendar continua sem regra no identity ou heurística no backend.

A primeira rodada teve 31/32: “Cria um compromisso ... no Family” levou o modelo a pedir o título ausente. O caso passou a fornecer o título explícito “Futebol”; a rodada completa abaixo usa esse enunciado. Esta é uma avaliação probabilística de intenção/argumentos, não de fidelidade da resposta ou de integração Google real. Datas, paginação, permissões, contexto, OAuth e efeitos externos são verificados nos testes backend.

| Pedido | Caso | Tools observadas | Resultado |
| --- | --- | --- | --- |
| Meu professor falou que a prova vai ser difícil. | none | `none` | PASS |
| Preciso responder uma pergunta da faculdade. | none | `none` | PASS |
| O GitHub é uma bagunça às vezes. | none | `none` | PASS |
| Tenho um prazo amanhã. | none | `none` | PASS |
| O que você acha dessa mensagem que eu escrevi? | none | `none` | PASS |
| Isso é importante para a apresentação. | none | `none` | PASS |
| Marcar pontos no texto ajuda a estudar? | none | `none` | PASS |
| Responda essa pergunta de matemática: quanto é 2 + 2? | none | `none` | PASS |
| Confirma que 2 + 2 = 4? | none | `none` | PASS |
| Veja se meu professor mandou algum email sobre a prova. | email | `email_search` | PASS |
| Tem algum email não lido importante? | email | `email_search` | PASS |
| Procura emails do GitHub sobre segurança. | email | `email_search,email_search,email_search` | PASS |
| Leia o último email da Unicentro. | email | `email_search,email_read` | PASS |
| Confere se chegou algum convite do GitHub por email hoje. | email | `email_search` | PASS |
| Vê se chegou. | clarify | `none` | PASS |
| Confere aquela mensagem para mim. | clarify | `none` | PASS |
| Pode resolver isso? | clarify | `none` | PASS |
| confirmo | pending | `email_confirm_pending_action` | PASS |
| O que tenho amanhã? | calendar_read | `calendar_list_events` | PASS |
| Tenho alguma coisa sexta à tarde? | calendar_read | `calendar_list_events` | PASS |
| Quando é minha próxima coisa marcada? | calendar_read | `calendar_list_events` | PASS |
| Procura a prova de Grafos na minha agenda. | calendar_read | `calendar_list_events` | PASS |
| Marca dentista amanhã às 14h por uma hora. | calendar_create | `calendar_create_event` | PASS |
| Quais agendas eu tenho? | calendar_calendars | `calendar_list_calendars` | PASS |
| Marca dentista amanhã às 14h por uma hora no Diario. | calendar_secondary | `calendar_list_calendars,calendar_create_event` | PASS |
| Cria o evento Futebol amanhã das 10h às 11h no Family. | calendar_secondary | `calendar_list_calendars,calendar_create_event` | PASS |
| Marca uma revisão amanhã das 10h às 11h no calendário da faculdade. | calendar_destination_clarify | `calendar_list_calendars` | PASS |
| Marca dentista amanhã às 14h. | calendar_clarify | `none` | PASS |
| Muda aquele compromisso para 16h. | calendar_update | `calendar_list_events,calendar_update_event` | PASS |
| Cancela essa reunião. | calendar_delete | `calendar_list_events,calendar_delete_event` | PASS |
| sim | calendar_pending | `calendar_confirm_pending_action` | PASS |
| Leia e resuma o último email sobre a apresentação no Gmail e me ajude a organizar esse compromisso. | cross | `email_search,email_read` | PASS |

## Rodada anterior da implementação primary-only

Execução real em 26/09/2026 (Brasília) com `gpt-5.6-luna`, `reasoning.effort=medium`, identity de produção intacta e as **22 tools** exportadas do registry de produção. Todas as respostas Gmail/Calendar foram stubadas; nenhuma operação Google real foi executada. Resultado da rodada final: **28/28**.

```bash
python3 scripts/eval_tool_intent.py --tools-json /tmp/aegis-tool-catalog-v040.json
```

O catálogo pode ser exportado com `dotnet run --project scripts/Aegis.ToolCatalogExport --configuration Release`. O eval também faz essa exportação automaticamente quando `--tools-json` é omitido. Requer `OPENAI_API_KEY`; nunca lê tokens OAuth da Aegis nem executa as tools de produção.

| Pedido | Caso | Tools observadas | Resultado |
| --- | --- | --- | --- |
| Meu professor falou que a prova vai ser difícil. | none | `none` | PASS |
| Preciso responder uma pergunta da faculdade. | none | `none` | PASS |
| O GitHub é uma bagunça às vezes. | none | `none` | PASS |
| Tenho um prazo amanhã. | none | `none` | PASS |
| O que você acha dessa mensagem que eu escrevi? | none | `none` | PASS |
| Isso é importante para a apresentação. | none | `none` | PASS |
| Marcar pontos no texto ajuda a estudar? | none | `none` | PASS |
| Responda essa pergunta de matemática: quanto é 2 + 2? | none | `none` | PASS |
| Confirma que 2 + 2 = 4? | none | `none` | PASS |
| Veja se meu professor mandou algum email sobre a prova. | email | `email_search,email_search` | PASS |
| Tem algum email não lido importante? | email | `email_search` | PASS |
| Procura emails do GitHub sobre segurança. | email | `email_search,email_search,email_search,email_search` | PASS |
| Leia o último email da Unicentro. | email | `email_search,email_read` | PASS |
| Confere se chegou algum convite do GitHub por email hoje. | email | `email_search` | PASS |
| Vê se chegou. | clarify | `none` | PASS |
| Confere aquela mensagem para mim. | clarify | `none` | PASS |
| Pode resolver isso? | clarify | `none` | PASS |
| confirmo | pending | `email_confirm_pending_action` | PASS |
| O que tenho amanhã? | calendar_read | `calendar_list_events` | PASS |
| Tenho alguma coisa sexta à tarde? | calendar_read | `calendar_list_events` | PASS |
| Quando é minha próxima coisa marcada? | calendar_read | `calendar_list_events` | PASS |
| Procura a prova de Grafos na minha agenda. | calendar_read | `calendar_list_events` | PASS |
| Marca dentista amanhã às 14h por uma hora. | calendar_create | `calendar_create_event` | PASS |
| Marca dentista amanhã às 14h. | calendar_clarify | `none` | PASS |
| Muda aquele compromisso para 16h. | calendar_update | `calendar_list_events,calendar_update_event` | PASS |
| Cancela essa reunião. | calendar_delete | `calendar_list_events,calendar_delete_event` | PASS |
| sim | calendar_pending | `calendar_confirm_pending_action` | PASS |
| Leia e resuma o último email sobre a apresentação no Gmail e me ajude a organizar esse compromisso. | cross | `email_search,email_read` | PASS |

O caso sem duração perguntou a duração/horário de término sem preparar uma ação. Alteração e exclusão resolveram o evento por listagem antes do preparo. O `sim` no turno posterior selecionou `calendar_confirm_pending_action`. No fluxo cross-tool final, o modelo leu o email e reconheceu agenda na resposta; a primeira rodada também selecionou `calendar_get_status` e preparou `calendar_create_event` após ler o email. Não há regra email → Calendar no identity ou no runtime do eval.

Duas rodadas iniciais tiveram 27/28: o modelo pediu esclarecimento em enunciados ambíguos (“o convite” sem remetente e “esse email” sem seleção prévia). Os casos finais explicitam convite do GitHub hoje e último email sobre a apresentação no Gmail, mantendo os mesmos critérios de intenção. O comportamento é probabilístico; esta avaliação verifica seleção de tools, não correção completa das respostas ou operações na API. Timezone, contexto de IDs, OAuth e efeitos externos são verificados pelos testes backend com HTTP simulado.
