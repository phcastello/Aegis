# Pesquisa web nativa — v0.9.0 Thinking Outside

## Integração compartilhada

A Responses API do GPT-6.1 Terra oferece pesquisa nativa. A Aegis disponibiliza `web_search` entre as ferramentas do modelo conversacional, junto das funções locais existentes, com escolha automática. O trabalho usa a infraestrutura de pesquisa da OpenAI. O cliente solicita `web_search_call.action.sources`, conserva os itens da pesquisa no contexto da rodada e registra a resposta na auditoria existente. Uma busca hospedada não é uma chamada de função local a ser executada pelo registro de ferramentas.

A configuração `AEGIS_WEB_SEARCH_ENABLED=true` disponibiliza a pesquisa. `AEGIS_WEB_SEARCH_MAX_SEARCH_ACTIONS=20` estabelece o orçamento desejado por turno (de 1 a 20), incluindo todas as rodadas de ferramentas. O nome anterior `AEGIS_WEB_SEARCH_MAX_CALLS` é aceito como alternativa. Somente itens `web_search_call` cuja ação é `search` reduzem esse saldo; uma ação pode apresentar várias consultas. `open_page` e `find_in_page` podem continuar mesmo quando o saldo é zero, sem quota própria na Aegis. O modelo recebe o saldo em cada rodada, com instrução explícita de não realizar novas buscas ao esgotá-lo. O limite `max_tool_calls` da Responses API alcança todas as ferramentas hospedadas, então não é enviado. A Aegis não tem um controle rígido específico de ações `search` durante uma resposta hospedada: excesso efetivamente observado é registrado, a resposta é preservada, e o saldo da próxima rodada fica em zero. Os limites existentes de rodadas conversacionais e tempo do provedor continuam válidos. A auditoria de cada turno registra o orçamento, totais observados por tipo e ações search excedentes. O contador de ações observadas não pressupõe cobrança de toda ação. A tarifa publicada de US$ 10 por mil chamadas faturáveis se soma aos tokens do conteúdo recuperado. A documentação identifica `search` como ação faturável, sem explicitar a cobrança independente das outras ações. [Preço da API](https://developers.openai.com/api/docs/pricing) · [Contrato do limite agregado](https://developers.openai.com/api/reference/resources/responses/methods/create). O modelo decide a pertinência da pesquisa. O prompt pede consultas reais para fatos atuais e verificação, respeito às preferências de busca e proteção de informações privadas, sem criar uma classificação adicional para perguntas triviais.

## Conversação e voz

Chat digitado e entrada manual de voz usam a ferramenta diretamente. No hands-free, a ferramenta de decisão de direcionamento continua precedendo efeitos externos. O primeiro turno oferece uma intenção de pesquisa local; quando ela é escolhida, o backend aceita a fala e libera a busca nativa na rodada seguinte. Uma resposta trivial continua em uma única chamada. Enquanto a decisão de ignorar ainda está aberta, o cliente Responses também bloqueia o anúncio da ferramenta hospedada.

Nos turnos hands-free que solicitam pesquisa, a resposta final vem como texto natural anotado pelo provedor, depois é adaptada aos segmentos de voz existentes. Isso evita atribuir a posições em argumentos de função as citações que pertencem a `output_text`. A apresentação do resultado usa as referências válidas para o texto realmente exibido. Se os índices não puderem ser associados com segurança, as fontes ficam acessíveis separadamente.

## Progresso, fontes e persistência

Os eventos SSE reais `response.web_search_call.in_progress/searching/completed` geram estados de progresso no transporte de chat. Eventos repetidos de um mesmo item são deduplicados. Os clientes Android e Windows encaminham o progresso transitório de um turno aceito; a sincronização das conversas recupera o resultado persistido.

O campo `MetadataJson` já existente das mensagens guarda uma projeção `webResearch` das fontes observadas e das citações válidas, sem migration. A interface oferece citações clicáveis ao lado de afirmações quando os intervalos coincidem com o texto e uma lista recolhível de fontes da pesquisa. Somente a evidência do provedor permite identificar uma página como explicitamente aberta. O texto que alimenta o TTS é preservado. URLs inválidas ou com credenciais não se tornam links clicáveis.

## Validação e etapas seguintes

- Validar com testes unitários, integração sintética e CI os orçamentos, as fontes, as citações, os estados, o descarte de fala alheia, a sincronização e a apresentação no histórico.
- Fazer o ensaio real autorizado na API e nos Nodes Android/Windows. Cobrir consulta trivial, fato atual simples, comparação de fontes e disponibilidade de produtos. Medir primeira fala/informação útil, custo e conclusão. Erros de permissão devem ser analisados nas configurações do projeto e da chave de API.
- Implementar pesquisa em segundo plano e anúncios falados de trabalhos demorados, com progresso verificável e cancelamento. Este primeiro incremento ainda usa o loop conversacional existente para a busca e não introduz um trabalhador de pesquisa assíncrono.

## Referências primárias

- [Pesquisa web na Responses API](https://developers.openai.com/api/docs/guides/tools-web-search)
- [Capacidades do GPT-6.1 Terra](https://developers.openai.com/api/docs/models/gpt-6.1-terra)
- [Contrato de criação de Responses, incluindo max_tool_calls](https://developers.openai.com/api/reference/resources/responses/methods/create)
- [Eventos de streaming](https://developers.openai.com/api/reference/resources/responses/streaming-events)
- [Gerenciamento de projetos e permissões](https://help.openai.com/en/articles/9186755-managing-projects-in-the-api-platform)
