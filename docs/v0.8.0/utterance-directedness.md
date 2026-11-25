# Descarte silencioso de ativações indevidas

## Pesquisa e decisão inicial (.35)

A detecção da palavra de ativação precisa ser rápida e funcionar durante a escuta contínua. Após detectar uma possível chamada e transcrever a fala, é possível usar informações adicionais para verificar se a fala se destinava à assistente:

- A Apple descreve o Siri Directed Speech Detection, que combina indícios acústicos, do reconhecimento de fala e semânticos para reduzir disparos indevidos. Uma de suas estratégias avalia o texto da fala completa. [Voice Trigger System for Siri](https://machinelearning.apple.com/research/voice-trigger).
- A Amazon estudou a combinação de características acústicas, da transcrição e do decodificador para recusar falas de fundo e permitir continuações sem nova palavra de ativação. No Follow-Up Mode, também usa o turno anterior para interpretar mensagens breves. [Device-directed Utterance Detection](https://www.amazon.science/publications/device-directed-utterance-detection) · [How Alexa knows when you’re talking to her](https://www.amazon.science/blog/how-alexa-knows-when-youre-talking-to-her).
- A Apple também investigou uma arquitetura que utiliza um modelo de linguagem com sinais de áudio e transcrição. [A Multi-signal Large Language Model for Device-directed Speech Detection](https://machinelearning.apple.com/research/llm-device-directed-speech-detection).

A prévia .35 usava um classificador textual centralizado depois do STT do nó vencedor, com até duas mensagens anteriores (máximo de 600 caracteres de cada). Essa abordagem funciona com clientes Android e Windows e com os provedores de STT existentes. O modelo recebe somente o texto necessário, usa o modelo e a configuração de raciocínio do gerador de títulos e retorna `attend` ou `ignore`, sem ferramentas e com `store=false`. Uma decisão de descarte exige evidência clara de fala alheia. A incerteza, falha de rede, resposta inválida ou prazo de dois segundos esgotado preservam o atendimento. Até três palavras passam direto para preservar interrupções, respostas breves e pedidos sem contexto. Uma ativação que contém apenas a palavra de chamada é descartada sem o modelo. Não há mudança no detector local nem no momento do sinal sonoro.

O gate ocorre antes de persistir a conversa, gerar a resposta ou chamar ferramentas. Somente o fluxo de voz hands-free com opção explícita participa. O chat comum e clientes anteriores mantêm seu comportamento. Uma fala descartada encerra a captura, retoma a escuta da palavra de ativação e libera a mídia pausada pelo cliente. Se uma continuação foi interrompida, a conversa anterior continua registrada.

## Contrato

- O pedido inclui `origin: "hands_free"` e `allowSilentIgnore: true`.
- `POST /api/chat/messages`: HTTP 204 sem corpo significa descarte.
- `POST /api/chat/messages/stream`: HTTP 200 NDJSON, um evento terminal exclusivo `{"type":"ignored","turnId":"..."}`.
- As respostas de atendimento mantêm o contrato anterior.

## Verificação de integração da .35

- Backend: 48 testes hands-free aprovados; suíte completa com 833 aprovados, 101 ignorados por condições de integração e zero falhas. Os testes do serviço cobrem descarte antes da persistência e cancelamento antes do registro do turno.
- Windows: 109 testes nativos aprovados e quatro ignorados intencionalmente em bancada isolada; o cliente retoma a escuta diante de HTTP 204 e só solicita voz quando há resposta.
- Android: 184 testes unitários JVM das suítes de voz, todos aprovados, além de 34 verificações de contrato. Testes HTTP locais verificaram descarte sem PCM, requisição de TTS, criação de mensagem ou invalidação de chat nos dois protocolos.

## Evidência da bancada textual

Executamos a classe real do classificador no SDK .NET 8 em um contêiner descartável, no servidor privado, usando o modelo `gpt-6-luna` com raciocínio `none`. As falas e os turnos anteriores são sintéticos. As chamadas ao modelo tiveram paralelismo 3. O tempo mede a função do classificador, incluindo HTTP e parsing, sem as etapas de captura, STT, chat ou TTS.

| Configuração | Pedidos legítimos aceitos | Falas alheias descartadas |
| --- | ---: | ---: |
| Modelo sem proteção de fala curta (73 casos iniciais) | 41/42 | 29/31 |
| Modelo com proteção (mesmos 73 casos) | 42/42 | 26/31 |
| Modelo com proteção (32 casos adicionais, separados até depois do ajuste) | 16/16 | 14/16 |
| Total final (105 casos) | 58/58 | 40/47 |

A primeira configuração descartou a interrupção legítima “Para aí.”. A proteção de falas curtas resolveu esse caso e preservou todos os pedidos legítimos da bancada. O preço dessa proteção é aceitar algumas falas alheias muito curtas. Uma transcrição completa de uma pergunta de TV pode ser indistinguível de uma fala do usuário pelo texto.

Dos 105 casos finais, 35 seguiram diretamente sem chamar o modelo. Nos 70 restantes, a mediana foi 779 ms, o percentil 95 foi 1.508 ms e um pedido atingiu o prazo de dois segundos. O resultado detalhado, com textos sintéticos, rótulos e tempos por amostra, está em [utterance-directedness-bench.json](utterance-directedness-bench.json).

Esses dados medem a classificação de texto em uma amostra escolhida para a bancada. A incidência de ativação falsa, a precisão nos áudios cotidianos, a latência percebida no uso real e o resultado de mídia com fala indistinguível ainda precisam de avaliação física. Um teste importante é dizer “Aegis”, esperar o sinal sonoro e fazer um pedido, inclusive curto. Outro é deixar conversa ou mídia ao fundo e verificar se ativações indevidas terminam sem mensagem na lista de conversas.

## Decisão no próprio modelo de resposta

Após a avaliação da prévia .35, o modelo de resposta da Aegis passou a tomar a decisão no seu primeiro turno. O pipeline mantém a escolha do nó, a captura e o STT existentes. O servidor prepara o contexto em memória e oferece a função terminal `hands_free_ignore` exclusivamente na primeira rodada do modelo, junto à função normal de resposta de voz e às ferramentas habituais. A configuração atual usa GPT-6.1 Terra com raciocínio `low`.

O modelo pode julgar o destinatário da fala a partir da transcrição e da história da conversa, inclusive quando ela contém uma tarefa que a Aegis saberia realizar. Indícios de outro interlocutor, chamadas a uma audiência e conteúdo de mídia justificam o descarte. Pedidos curtos, relatos, ditados e continuações plausíveis são atendidos. A pesquisa da Apple com contexto de turnos anteriores também avalia LLMs para essa tarefa. [Device-Directed Speech Detection for Follow-up Conversations Using Large Language Models](https://machinelearning.apple.com/research/device-directed).

A decisão de ignorar encerra o turno sem resposta registrada, extração de memória ou execução de ferramentas. Antes da primeira ação ou resposta, o serviço registra a aceitação e persiste a mensagem do usuário. O turno provisório pode ser cancelado sem substituir um turno ativo anterior; uma aceitação mais recente na mesma conversa invalida candidatos obsoletos. O streaming de Android continua liberando o primeiro segmento válido após a aceitação. O cliente Responses já usa `parallel_tool_calls=false`, que restringe cada rodada a zero ou uma função. Um descarte válido termina silenciosamente. Lotes incompatíveis antes do aceite são descartados ou rejeitados sem persistência ou execução de ferramentas. Chamadas incompatíveis após a aceitação falham sem executar ferramentas do lote. [OpenAI Function calling](https://developers.openai.com/api/docs/guides/function-calling#parallel-function-calling).

A primeira chamada, que pode resultar em descarte, usa `store=false`. Uma interação legítima não requer uma chamada de classificação adicional. A Luna continua responsável pelos títulos e pelos demais componentes que a usam. O contrato 204/ignored permanece válido; o sinal sonoro da ativação continua ocorrendo antes da transcrição.

### Fragmentos isolados sem intenção

Uma falsa ativação também pode produzir um fragmento estranho de STT que não se
parece com fala dirigida a outra pessoa. Como defesa adicional, o próprio
modelo de resposta recebe a orientação para ignorar silenciosamente apenas um
fragmento isolado **sem intenção conversacional reconhecível e sem relação
plausível com o contexto recente**. Não deve inventar uma intenção ou perguntar
o significado de uma etiqueta solta sem propósito. A regra não usa lista de
palavras, pontuação, idioma, quantidade de palavras ou limiar de confiança.
Pedidos curtos (por exemplo, “pare”, “hora?”, “agenda”, “volume”), respostas,
termos técnicos pertinentes à conversa e ditados continuam válidos. Quando há
dúvida razoável de que exista uma interação plausível com a Aegis, mantém-se
o atendimento.

A orientação e a descrição de `hands_free_ignore` conservam o mesmo contrato
de aceite: apenas no primeiro turno, para hands-free com descarte habilitado,
sem nova chamada de classificação nem modificação do STT. Os testes de
contrato verificam a orientação efetivamente enviada ao modelo, a ausência
dela nos pedidos manuais/legados e a aceitação de uma resposta curta.
Não comprovam a escolha semântica de um modelo real em cada exemplo; a regra
não substitui a detecção acústica nem garante que toda alucinação de STT
será ignorada. A investigação do STT e a salvaguarda para silêncio estão
descritas em [STT realtime](hands-free-realtime-stt.md#salvaguarda-de-transcrição-sem-fala-44).

### Integração

A suíte geral do backend passou com 827 testes aprovados, 101 condicionais ignorados e zero falhas. Os 56 casos focados passaram, incluindo descarte exclusivo, persistência, cancelamento e segmentos de voz incrementais.

### Comparação textual

Usamos os 105 casos anteriores e mais 48 falas, incluindo os dois exemplos informados pelo proprietário. O teste do Terra usou o prompt de identidade e as orientações e funções terminais do fluxo de voz, com declarações de quatro ferramentas representativas sem executá-las. O limite de 512 tokens do ensaio era inferior ao limite de produção, para medir a escolha inicial. A métrica de atendimento inclui duas respostas longas cujo modelo iniciou a função terminal correta e atingiu o limite de tokens do ensaio. Repetimos essas duas falas por protocolo síncrono com o limite de produção de 4.000 tokens. Ambas concluíram com a função de resposta, em 33,5 e 36,8 s. O primeiro ensaio com timeout de 35 s por leitura não foi suficiente para essa reexecução; o ensaio de confirmação usou 130 s.

| Modelo e abordagem | Pedidos legítimos com decisão de atendimento | Falas alheias descartadas |
| --- | ---: | ---: |
| Luna como classificador separado (.35), 153 casos | 82/82 | 60/71 |
| Terra em seu primeiro turno, 153 casos | 82/82 | 70/71 |
| Terra, os 48 casos adicionais | 24/24 | 23/24 |

Ambos os exemplos de erro informados pelo proprietário passaram no piloto e no conjunto ampliado. O caso alheio remanescente foi uma fala sintética formulada como pergunta a uma pessoa em um ponto de ônibus, após uma conversa com a assistente sobre sair de casa. O texto sozinho não permite resolver sempre a intenção do interlocutor.

No Terra, a primeira escolha observável nos fluxos streaming teve mediana de 1.134 ms para pedidos legítimos. Nas falas alheias, a mediana do término da primeira geração foi 1.313 ms entre os dois protocolos. O ensaio mede o modelo, sem STT, persistência ou TTS. No caminho síncrono, mede-se toda a resposta do modelo. Não há comparação medida de latência completa das duas arquiteturas. As respostas e a avaliação por caso ficam em [utterance-directedness-terra-bench.json](utterance-directedness-terra-bench.json); os textos dos dois exemplos reais foram omitidos.
