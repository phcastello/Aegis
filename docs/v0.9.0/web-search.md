# Pesquisa web nativa — v0.9.0

## Primeiro incremento: transporte do provedor

O cliente Responses compartilhado aceita um orçamento explícito de chamadas de pesquisa nativa por requisição. Quando o orçamento é positivo, anuncia a ferramenta `web_search` ao lado das funções já existentes, usa `tool_choice=auto`, solicita `web_search_call.action.sources` e limita as chamadas hospedadas via `max_tool_calls`. O provedor devolve os itens da pesquisa e as anotações de texto; o cliente conserva esses itens e a auditoria existentes. Uma busca hospedada não é uma função local a ser executada pelo registro de tools.

O GPT-6.1 Terra oferece pesquisa nativa, streaming e funções. As anotações de citação do provedor pertencem ao texto efetivamente retornado. Fontes consultadas, páginas efetivamente abertas e fontes citadas têm significados distintos.

Enquanto houver a função de descarte `hands_free_ignore`, a pesquisa hospedada permanece indisponível mesmo com orçamento positivo. A decisão de atendimento deve preceder qualquer consulta externa. O backend aproveita o descarte silencioso já implantado na v0.8.0.

Por padrão, o orçamento ainda é zero. Este incremento prepara e testa o transporte em memória, sem ativar a busca para usuários.

## Próximas integrações

- Decidir o orçamento de pesquisa por turno no loop comum (texto, voz pelo botão e hands-free). Para a primeira rodada hands-free ainda pendente, oferecer uma intenção de pesquisa que só produz a consulta externa após aceitar a fala. Uma pergunta direta continua sem round de classificação independente.
- Propagar os eventos observados de pesquisa do Responses para o contrato existente de progresso, sem emitir atividade antes de aceitar a fala. Agregar fontes de todas as rodadas.
- Persistir os resultados e citações validadas junto à mensagem. Exibir as citações com links clicáveis no texto e uma lista de fontes consultadas no histórico. A locução deve permanecer natural. As funções de voz atuais não geram anotações de texto, portanto as posições de citações da resposta textual não podem ser reutilizadas na fala.
- Validar a API real com a instalação autorizada, a pesquisa simples e a longa, a ausência de pesquisa em pedidos triviais, e as três modalidades no Android e Windows.

## Referências primárias

- [Pesquisa web na Responses API](https://developers.openai.com/api/docs/guides/tools-web-search)
- [Capacidades do GPT-6.1 Terra](https://developers.openai.com/api/docs/models/gpt-6.1-terra)
- [Contrato de criação de Responses, incluindo max_tool_calls](https://developers.openai.com/api/reference/resources/responses/methods/create)
- [Eventos de streaming](https://developers.openai.com/api/reference/resources/responses/streaming-events)
