# Validação — v0.4.1 — “Booked!”

Branch `feat/v0.4.1-polish`, criada da `main` atualizada (`64e751b`), sem merge na `main`. Validação em 27/09/2026, com o modelo existente `gpt-5.6-luna`, `reasoning.effort=medium` e as **24 tools reais** exportadas do registry de produção. Nenhum modelo, configuração de raciocínio, schema de tool ou fluxo de confirmação foi alterado.

## Proatividade

A identidade recebeu uma instrução geral para oferecer um próximo passo concreto com outra integração quando a consulta solicitada revelar utilidade prática. A oferta não depende de uma decisão prévia de participar. Preparação e execução continuam esperando interesse do usuário. A regra original contra usar tools por menções casuais foi preservada.

Somente alterar a regra no prefixo produziu ofertas inconsistentes. O ajuste final no loop geral reapresenta a mesma identidade confiável depois dos resultados das tools, em uma mensagem developer transitória. O prefixo de cache e o histórico nativo permanecem intactos, sem acumular lembretes, adicionar breakpoints dinâmicos ou promover conteúdo de tools a instruções. Dois testes novos verificam essas propriedades em streaming e no fluxo normal. Nenhuma regra de intenção, nome de integração, palavra de conteúdo ou data foi acrescentada ao loop.

Os cinco casos novos não pedem organização ou uso de Calendar. Duas rodadas focadas com a identidade reapresentada passaram **5/5** cada, usando somente `email_search` e `email_read`:

| Fixture | Resultado observado | Resultado |
| --- | --- | --- |
| Hackers do Bem: webinar em 30/09/2026 às 16h | Resumo correto e oferta de colocar o webinar na agenda. | PASS |
| OpenAI DevDay: 29/09/2026, 10 a.m. PT | Resumo, conversão para 14h em Brasília e oferta de colocar no calendário. | PASS |
| Palestra de acessibilidade da semana anterior | Resumo dos temas e materiais, sem oferta de agenda. | PASS |
| Newsletter histórica: Netscape em 1994 | Resumo, sem oferta de agenda. | PASS |
| Newsletter promocional com várias datas | Resumo, sem oferta mecânica de agenda. | PASS |

Nenhum caso novo chamou Calendar, incluindo `calendar_create_event` ou `calendar_confirm_pending_action`. As fixtures e as verificações textuais pertencem exclusivamente ao eval; não há roteamento por datas, palavras ou regex na aplicação. O caso cross-tool antigo e todos os controles anteriores foram preservados.

Rodada completa com o ajuste final: **114/114 aprovados em uma única execução CLI sequencial**, incluindo as **109 regressões anteriores** e os **cinco casos novos**, com a conversão DevDay para 14h em Brasília. Relatório final: `/tmp/aegis-v041-eval.json` (também preservado em `/tmp/aegis-v041-eval-release.json`). Log: `/tmp/aegis-v041-eval.log`.

Rodadas anteriores ao ajuste final: a primeira execução completa ficou em **113/114** (109 regressões aprovadas, `/tmp/aegis-v041-eval-initial-complete.json`); outra ficou em **112/114** (`/tmp/aegis-v041-eval-current.json`), omitindo ambas as ofertas positivas. Uma repetição paralela foi interrompida por `429`, sem relatório completo, e mostrou ofertas indevidas nos controles negativos. Esses resultados motivaram o reforço geral da identidade; não foram contados como aprovação. Uma comparação isolada com outro modelo também ofereceu lembretes promocionais, portanto a configuração de produção foi preservada.

O checker passou a detectar ofertas de lembrete mesmo sem a palavra Calendar. A validação de timezone distingue uma oferta futura de conversão de uma conversão efetivamente apresentada, e não confunde `14h em Brasília (10h PT)` com duas horas locais. A segunda rodada focada foi reavaliada com essa correção, sem refazer nem alterar a resposta do modelo. Relatórios finais focados: `/tmp/aegis-v041-policy-replay.json` e `/tmp/aegis-v041-policy-replay-repeat-scored.json`.

```bash
dotnet test backend/Aegis.sln
dotnet run --project scripts/Aegis.ToolCatalogExport --configuration Release > /tmp/aegis-v041-tools.json
python3 scripts/eval_tool_intent.py --tools-json /tmp/aegis-v041-tools.json --report-json /tmp/aegis-v041-eval.json
```

O host não tem `dotnet` no PATH; os dois comandos .NET foram executados no container `mcr.microsoft.com/dotnet/sdk:8.0`, com o repositório montado em `/src`. **256/256 testes backend passaram**. O catálogo Release foi reexportado após o ajuste do loop e comparado integralmente: as 24 descrições e schemas permanecem idênticos. Os resultados Gmail/Calendar do eval são simulados; nenhuma operação Google real foi executada. A avaliação de respostas do modelo é probabilística e não garante a mesma oferta em todas as respostas.

## Frontend e responsividade

`npm run build` aprovado e `npm run test:email` com **9/9 testes aprovados**, sem framework ou dependência adicional de UI.

Validação em Chromium, com eventos de touch via CDP, inspeção das imagens e APIs stubadas. Desktop a 1600px: quatro ciclos de abertura/fechamento preservaram exatamente os retângulos de panel, header, conteúdo e composer, além de `scrollTop=400`. O panel e o scroll da conversa permaneceram com a borda direita em `x=1600`; mensagens e composer permaneceram em `x=420`, largura 760px. Não há tracks laterais nem compensação dinâmica.

Mobile a 390×844: swipe começou no meio da conversa (`x=180`). Com sidebar de 310px, 62px de movimento produziram progresso 0,2; 186px produziram progresso 0,6, sidebar em `x=-124` e backdrop com opacidade 0,6. Durante o drag, a transição foi `0s`. Foram verificados cancelamento de gesto curto, abertura suficiente e por flick, fechamento arrastando para a esquerda, botões/backdrop, scroll vertical real (`scrollTop` de 600 para 765), scroll horizontal de código, composer preservado, supressão de clique após drag, novo tap intencional, touch cancel, resize/orientação e reduced motion.

Nas larguras 320, 620, 760, 1380, 1381 e 1920px, o chat continuou ocupando a viewport e o popover ficou dentro dela. Conversa vazia não mostrou o botão de informações. ID, feedback de cópia, click/tap, clique fora, teclado, Enter/Espaço, Escape e retorno de foco foram verificados. Send e stop mantiveram 42×42 e radius 50%; microphone/discard mantiveram a geometria circular. `focus-visible` do composer permaneceu funcional. Mouse em desktop não ativou a gesture.

Artefatos locais: `/tmp/aegis-v041-browser/results.json`, `/tmp/aegis-v041-browser/keyboard-touch-copy.json` e screenshots em `/tmp/aegis-v041-browser/`. Não houve teste em aparelho físico ou Safari.

## Critérios de entrega

| # | Critério | Estado |
| --- | --- | --- |
| 1 | Conversation ID consultável e copiável na UI | PASS |
| 2 | Painel aberto por click/tap, sem depender de hover | PASS |
| 3 | Controles internos do composer circulares e consistentes | PASS |
| 4 | Swipe iniciado no meio da conversa | PASS |
| 5 | Nenhuma exigência de começar na borda esquerda | PASS |
| 6 | Sidebar e backdrop acompanham o dedo proporcionalmente | PASS |
| 7 | Scroll vertical nativo preservado | PASS |
| 8 | Drag para a esquerda fecha | PASS |
| 9 | Scrollbar permanece na extrema direita | PASS |
| 10 | Conteúdo, header e composer não mudam de posição | PASS |
| 11 | Conteúdo futuro relevante gera oferta natural de agenda | PASS |
| 12 | A oferta não prepara nem executa Calendar | PASS |
| 13 | Conteúdo passado, incidental e promocional sem oferta mecânica | PASS |
| 14 | Sem heurística de datas na aplicação | PASS |
| 15 | Proteções e regressões Calendar/Gmail da v0.4.0 | PASS: 256 testes backend e 109 regressões anteriores |
| 16 | Versão v0.4.1 — “Booked!” | PASS |
| 17 | Build, testes e evals aprovados | PASS |

Diff revisado, incluindo os componentes novos. Sem endpoint, rota, migration, monitoramento, automação ou integração nova; código Calendar/Gmail de produção preservado.
