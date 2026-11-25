# Descarte silencioso de ativações indevidas

## Pesquisa e decisão

A detecção da palavra de ativação precisa ser rápida e funcionar durante a escuta contínua. Após detectar uma possível chamada e transcrever a fala, é possível usar informações adicionais para verificar se a fala se destinava à assistente:

- A Apple descreve o Siri Directed Speech Detection, que combina indícios acústicos, do reconhecimento de fala e semânticos para reduzir disparos indevidos. Uma de suas estratégias avalia o texto da fala completa. [Voice Trigger System for Siri](https://machinelearning.apple.com/research/voice-trigger).
- A Amazon estudou a combinação de características acústicas, da transcrição e do decodificador para recusar falas de fundo e permitir continuações sem nova palavra de ativação. No Follow-Up Mode, também usa o turno anterior para interpretar mensagens breves. [Device-directed Utterance Detection](https://www.amazon.science/publications/device-directed-utterance-detection) · [How Alexa knows when you’re talking to her](https://www.amazon.science/blog/how-alexa-knows-when-youre-talking-to-her).
- A Apple também investigou uma arquitetura que utiliza um modelo de linguagem com sinais de áudio e transcrição. [A Multi-signal Large Language Model for Device-directed Speech Detection](https://machinelearning.apple.com/research/llm-device-directed-speech-detection).

A v0.8.0 usa um classificador textual centralizado depois do STT do nó vencedor, com até duas mensagens anteriores (máximo de 600 caracteres de cada). Essa abordagem funciona com clientes Android e Windows e com os provedores de STT existentes. O modelo recebe somente o texto necessário, usa o modelo e a configuração de raciocínio do gerador de títulos e retorna `attend` ou `ignore`, sem ferramentas e com `store=false`. Uma decisão de descarte exige evidência clara de fala alheia. A incerteza, falha de rede, resposta inválida ou prazo de dois segundos esgotado preservam o atendimento. Até três palavras passam direto para preservar interrupções, respostas breves e pedidos sem contexto. Uma ativação que contém apenas a palavra de chamada é descartada sem o modelo. Não há mudança no detector local nem no momento do sinal sonoro.

O gate ocorre antes de persistir a conversa, gerar a resposta ou chamar ferramentas. Somente o fluxo de voz hands-free com opção explícita participa. O chat comum e clientes anteriores mantêm seu comportamento. Uma fala descartada encerra a captura, retoma a escuta da palavra de ativação e libera a mídia pausada pelo cliente. Se uma continuação foi interrompida, a conversa anterior continua registrada.

## Contrato

- O pedido inclui `origin: "hands_free"` e `allowSilentIgnore: true`.
- `POST /api/chat/messages`: HTTP 204 sem corpo significa descarte.
- `POST /api/chat/messages/stream`: HTTP 200 NDJSON, um evento terminal exclusivo `{"type":"ignored","turnId":"..."}`.
- As respostas de atendimento mantêm o contrato anterior.

## Verificação de integração

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
