# Pesquisa web nativa — v0.9.0 Thinking Outside

## Integração compartilhada

A Responses API do GPT-6.1 Terra oferece pesquisa nativa. A Aegis libera `web_search` depois da decisão de fazer uma consulta direta ou no processo que executa uma pesquisa delegada. O trabalho usa a infraestrutura de pesquisa da OpenAI. O cliente solicita `web_search_call.action.sources`, conserva os itens da pesquisa no contexto da rodada e registra a resposta na auditoria existente. Uma busca hospedada não é uma chamada de função local a ser executada pelo registro de ferramentas.

A configuração `AEGIS_WEB_SEARCH_ENABLED=true` disponibiliza a pesquisa. `AEGIS_WEB_SEARCH_MAX_SEARCH_ACTIONS=20` estabelece o orçamento desejado por turno (de 1 a 20), incluindo todas as rodadas de ferramentas. O nome anterior `AEGIS_WEB_SEARCH_MAX_CALLS` é aceito como alternativa. Somente itens `web_search_call` cuja ação é `search` reduzem esse saldo; uma ação pode apresentar várias consultas. `open_page` e `find_in_page` podem continuar mesmo quando o saldo é zero, sem quota própria na Aegis. O saldo numérico fica no backend. Em rodadas com pesquisa, o modelo é orientado a fazer somente as consultas necessárias, reaproveitar resultados e parar assim que tiver evidências suficientes. Ao esgotar o saldo, o modelo recebe a instrução de não fazer novas ações `search` e ainda pode consultar as fontes disponíveis. O limite `max_tool_calls` da Responses API alcança todas as ferramentas hospedadas, então não é enviado. A Aegis não tem um controle rígido específico de ações `search` durante uma resposta hospedada: excesso efetivamente observado é registrado, a resposta é preservada, e o saldo da próxima rodada fica em zero. Os limites existentes de rodadas conversacionais e tempo do provedor continuam válidos. A auditoria de cada turno registra o orçamento, totais observados por tipo e ações search excedentes. O contador de ações observadas não pressupõe cobrança de toda ação. A tarifa publicada de US$ 10 por mil chamadas faturáveis se soma aos tokens do conteúdo recuperado. A documentação identifica `search` como ação faturável, sem explicitar a cobrança independente das outras ações. [Preço da API](https://developers.openai.com/api/docs/pricing) · [Contrato do limite agregado](https://developers.openai.com/api/reference/resources/responses/methods/create). O modelo decide a pertinência da pesquisa. O prompt pede consultas reais para fatos atuais e verificação, respeito às preferências de busca e proteção de informações privadas, sem criar uma classificação adicional para perguntas triviais.

## Conversação e voz

Chat digitado, entrada manual de voz e hands-free seguem a decisão do modelo. Uma resposta trivial continua em uma única chamada. Para uma consulta rápida, `request_web_search` libera a busca nativa na rodada seguinte. No hands-free, o modelo decide se a fala foi dirigida à Aegis antes de qualquer pesquisa. Uma fala descartada não cria mensagem nem trabalho.

Nas consultas diretas do hands-free, a resposta final vem como texto natural anotado pelo provedor, depois é adaptada aos segmentos de voz existentes. Isso evita atribuir a posições em argumentos de função as citações que pertencem a `output_text`. A apresentação do resultado usa as referências válidas para o texto realmente exibido. Se os índices não puderem ser associados com segurança, as fontes ficam acessíveis separadamente.

## Progresso, fontes e persistência

Os eventos SSE reais `response.web_search_call.in_progress/searching/completed` geram estados de progresso no transporte de chat. Eventos repetidos de um mesmo item são deduplicados. Os clientes Android e Windows encaminham o progresso transitório de um turno aceito; a sincronização das conversas recupera o resultado persistido.

O campo `MetadataJson` já existente das mensagens guarda uma projeção `webResearch` das fontes observadas e das citações válidas, sem migration. A interface oferece citações clicáveis ao lado de afirmações quando os intervalos coincidem com o texto e uma lista recolhível de fontes da pesquisa. Somente a evidência do provedor permite identificar uma página como explicitamente aberta. O prompt orienta o modelo a atribuir as informações às fontes com as citações nativas, sem repetir listas de referências nem escrever URLs só para atribuição. Links ou listas de fontes podem ser incluídos quando o usuário pedir. Os trechos entre parênteses são omitidos na fala. O texto original e as citações nativas continuam visíveis no chat. URLs inválidas ou com credenciais não se tornam links clicáveis.

## Pesquisa em segundo plano

Quando prevê uma pesquisa com várias etapas ou uma espera perceptível, a Aegis
usa `delegate_web_research` antes da primeira busca. Ela anuncia o que vai
pesquisar na conversa e fala a intenção no hands-free. O turno termina assim que
a fila persistente recebe o trabalho. A intenção e a pesquisa ficam vinculadas ao
pedido original. O usuário pode seguir na mesma conversa ou sair do app enquanto
o servidor trabalha.

O agente de pesquisa recebe um resumo autossuficiente com as referências
públicas relevantes do diálogo e usa apenas a busca web. A resposta e as fontes
entram na conversa original. O status acompanha o pedido, com opção de cancelar.
O resumo usa os metadados da mensagem, sem migration.

Os apps mostram até três sites em círculos sobrepostos, em ordem de frequência nos
resultados observados. Novos ícones entram na pilha conforme chegam fontes do
provedor. Antes da primeira fonte aparece um indicador de busca. O backend da
Aegis procura o favicon nas declarações da página inicial pública do site e usa
o caminho convencional como alternativa. Imagens ausentes, inválidas ou de um
pixel deixam a inicial visível. A animação é reduzida de acordo com a preferência
do dispositivo. [Declarações de ícone no HTML](https://html.spec.whatwg.org/multipage/links.html#rel-icon) · [Animações de lista no Vue](https://vuejs.org/guide/built-ins/transition-group.html)

O cliente pede o favicon à própria API da Aegis. A descoberta aceita ICO, PNG,
WebP, JPEG e GIF com dimensões entre 2 e 512 pixels. A API consulta destinos
públicos e valida cada redirecionamento. Cada descoberta tem até cinco segundos
e nove requisições. Os ícones encontrados ficam em cache por 12 horas, e as
ausências por 15 minutos.

Na resposta, as citações mostram o site. Passar o mouse, usar foco de teclado ou
acionar os detalhes pelo toque mostra uma prévia com a informação de fonte
disponível. A página original pode ser aberta pela própria citação. A pilha de
favicons no fim da barra de ações de cada resposta abre a lista completa de
fontes validadas daquela resposta.

## Validação e etapas seguintes

- Validar com testes unitários, integração sintética e CI os orçamentos, as fontes, as citações, os estados, o descarte de fala alheia, a sincronização e a apresentação no histórico.
- Fazer o ensaio real autorizado na API e nos Nodes Android/Windows. Cobrir consulta trivial, fato atual simples, comparação de fontes e disponibilidade de produtos. Medir primeira fala/informação útil, custo e conclusão. Erros de permissão devem ser analisados nas configurações do projeto e da chave de API.
- Fazer ensaio de cancelamento, continuidade após fechar o cliente e retomada do acompanhamento ao reabrir a conversa.

## Referências primárias

- [Pesquisa web na Responses API](https://developers.openai.com/api/docs/guides/tools-web-search)
- [Capacidades do GPT-6.1 Terra](https://developers.openai.com/api/docs/models/gpt-6.1-terra)
- [Contrato de criação de Responses, incluindo max_tool_calls](https://developers.openai.com/api/reference/resources/responses/methods/create)
- [Eventos de streaming](https://developers.openai.com/api/reference/resources/responses/streaming-events)
- [Gerenciamento de projetos e permissões](https://help.openai.com/en/articles/9186755-managing-projects-in-the-api-platform)
