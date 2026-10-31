# Correção da reprodução incremental na preview hands-free

O transporte de voz usa o mesmo `/api/voice/speech`, os mesmos identificadores de
turno/mensagem e o PCM `s16le`, mono, 24 kHz do serviço existente. O PCM deixou de
ser acumulado em um `ByteArrayOutputStream`: a leitura HTTP entrega amostras ao
player nativo enquanto a resposta ainda está sendo recebida.

## Fluxo e limites

`NodeHandsFreeBackend` valida os headers antes de entregar áudio e lê em blocos de
até 8.192 bytes. `HandsFreePcm16Framer` conserva um byte pendente entre leituras e
usa dois bytes auxiliares para reconstruir uma amostra partida. EOF vazio, ímpar,
incompleto ou acima do limite falha. O limite permanece 8.640.000 bytes de PCM,
equivalentes a 180 segundos; não depende da fragmentação HTTP.

Cada turno recebe um `HandsFreePcmSink` próprio, aberto de forma preguiçosa.
`AudioTrack.WRITE_BLOCKING` fornece controle de fluxo; não existe fila adicional
de áudio no aplicativo. O buffer nativo tem capacidade fixa solicitada de
`max(getMinBufferSize(...), 24.000)` bytes, independente do tamanho da resposta.
As estruturas internas limitadas do transporte permanecem separadas desse
buffer. Não há armazenamento do áudio de resposta em disco.

Depois do EOF HTTP validado, o backend aguarda o marcador e confirma o contador
de frames reproduzidos do `AudioTrack`. O retorno `playedAudio=true` e a decisão
de continuação só existem após essa confirmação. A janela de dois segundos é
aberta pelo controlador depois desse retorno. Erro de rede, formato, player,
timeout ou cancelamento aborta o áudio pendente e cancela os recursos remotos do
turno; não retorna uma continuação normal.

A API pública de `AudioTrack` distingue escrita, contador de reprodução e
marcadores. No Android 12/API 31 ou superior, o limiar de início é reduzido a um
frame; nas APIs anteriores usa-se o menor buffer aceito pelo sistema. A resposta
curta e as rotas de áudio ainda precisam de homologação física nessas versões.
Fonte: [referência oficial de AudioTrack](https://developer.android.com/reference/android/media/AudioTrack).

O TTS inteiro tem prazo de 300 segundos, incluindo transferência, escrita
bloqueante e drenagem final do player. Uma tarefa única por turno em um executor
compartilhado de uma thread aborta player e chamada de rede ao vencer esse prazo;
é cancelada e removida da fila no `finally`. Um guard atômico impede a corrida
entre terminar normalmente e o callback do prazo. Não há polling. O transporte
conserva também o timeout de chamada e o timeout de leitura ociosa de até 120
segundos. A espera final do player ainda tem seu limite de duração restante mais
cinco segundos, no máximo 185 segundos, subordinado ao prazo total. STT e chat
conservam seus limites anteriores. Tudo executa fora da thread da UI.

## Cancelamento, identidade e dependência

O teste com download interrompido pelo servidor mostrou que
`HttpURLConnection.disconnect()` não liberava uma leitura bloqueada de maneira
confiável no ambiente JVM. O caminho hands-free passa a usar **OkHttp 4.12.0**,
fixado no Gradle, com `Call.cancel()` imediato. Os outros transportes do Node
permanecem com sua implementação atual. Não há retries, redirects, cache de
respostas, interceptador de logs ou cookies adicionados. O pool ocioso é limitado
a uma conexão por 30 segundos.

O contrato de cancelamento e os timeouts foram conferidos em `Call.kt` e
`RealCall.kt` do [artefato-fonte publicado da versão 4.12.0](https://repo.maven.apache.org/maven2/com/squareup/okhttp3/okhttp/4.12.0/okhttp-4.12.0-sources.jar),
SHA-256 `d91a769a4140e542cddbac4e67fcf279299614e8bfd53bd23b85e60c2861341c`.
Essa versão traz Okio 3.6.0. As licenças Apache 2.0, MPL 2.0 e a atribuição da
Public Suffix List estão nos assets de licenças do aplicativo.

Cancelamento invalida a sessão, aborta o sink e cancela a chamada de rede
sincronamente, sem aguardar DELETEs remotos. Pause/flush/stop descartam amostras
pendentes. Cada sink captura sua geração antes de criar o player: um fechamento,
cancelamento ou evento tardio de foco de uma reprodução antiga não invalida a
seguinte. A origem privada, credencial, Node, sessão e conversa continuam
fixados. A autoridade é verificada antes de enviar requests, após headers e
periodicamente durante a leitura de áudio, além dos limites de cada estágio.

## Instrumentação

`tts_first_audio` mede o tempo entre iniciar a requisição TTS e o primeiro PCM
aceito pelo player. É latência observada de entrega ao player, não uma medição
acústica da primeira palavra. `tts_fetch` mede a transferência, incluindo
backpressure; `tts_total` inclui a drenagem final; `tts_playback` mede do primeiro
PCM aceito até a confirmação de término ou falha. São eventos agregados por
turno, sem logs por bloco ou conteúdo de voz.

## Evidência automatizada e limites

`NodeHandsFreeBackendTest` inclui um servidor TCP/HTTP loopback real, identidade
sintética e player simulado. A resposta lenta envia primeiro três bytes e só
libera o restante depois que o teste comprova entrega de uma amostra ao player
antes do EOF. Fragmentos ímpares e blocos maiores recompõem exatamente os 50.000
bytes esperados; cada escrita tem até 8.192 bytes. Também cobre EOF prematuro,
cancelamento enquanto a rede está bloqueada, falha do player depois do download,
headers, limites e manutenção dos identificadores. Dois testes injetam prazo de
um segundo com o player deliberadamente bloqueado na escrita ou na drenagem após
EOF: ambos descartam a fila e encerram o turno sem continuação. Nenhuma ferramenta
real do backend é executada.

`NodeHandsFreePcmStreamTest` usa o mesmo coordenador e framer da produção com
dispositivo simulado. Verifica reconstrução das amostras, fila limitada pelo
dispositivo, produtor bloqueado liberado por cancelamento, EOF sem conclusão
prematura, corrida do marcador, drenagem cancelada e eventos tardios de streams
anteriores. A simulação controla explicitamente o contador de frames e não prova
saída acústica.

Os 22 testes de transporte e oito testes do coordenador/player passaram no runner
JVM local com o OkHttp real. As verificações Android estão registradas no
relatório da preview. Não houve dispositivo físico disponível para este subteste. Reprodução
no alto-falante, Bluetooth/headset, perda de foco real, latência acústica e tela
bloqueada continuam sujeitos à homologação no aparelho; os testes simulados não
substituem essa evidência.
