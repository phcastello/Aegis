# Marcos de latência no Android

A preview acrescenta um `turn_latency` por turno executado ao buffer privado já
existente (128 eventos), com durações agregadas por dia e identidade de build/
configuração. Não grava texto ou áudio. Não há profiling por frame, polling novo,
timer permanente ou chamadas extras a provedores. A escuta passiva não cria
esses registros. Uma consulta monotônica por leitura bloqueante já existente
(normalmente cinco por segundo) ancora o pre-roll recebido antes da ativação;
não cria leituras ou tarefas adicionais.
O mapa detalhado de até 32 marcos só é exportado em `BuildConfig.DEBUG`.
Na preview de distribuição, profiling detalhado fica desligado; permanecem
agregados e resumo limitado por turno (IDs, resultado, durações e limites).
Essa configuração também integra a identidade das métricas.

O UUID do turno é gerado antes do STT, enviado no header `X-Aegis-Turn-Id` das
três requisições e reutilizado no `turnId` de chat. O registro associa somente
esse UUID aos IDs da transcrição e síntese. Credenciais não entram nas métricas.

Todos os pontos do turno usam **`System.nanoTime()` do processo Android**,
convertido em milissegundos. A exportação contém diferenças relativas ao
endpoint; não subtrai relógios de servidores. O relógio `elapsedRealtime` usado
anteriormente para disponibilidade e a hora civil dos eventos continuam com
suas funções anteriores e não entram na subtração desses pontos.

## Entrada e endpointing

O VAD apenas expõe a posição final da última frame que satisfez sua regra
ativa existente. Nenhum threshold, timeout, modelo ou decisão foi alterado.
O controlador estima seu horário a partir da posição dessa frame dentro da
leitura bloqueante de áudio (até 200 ms), usando o instante em que essa leitura
retornou. Conserva a estimativa da leitura em que a frame ocorreu; bloqueios/
intervalos posteriores continuam incluídos no tempo até o endpoint.

`vad_endpoint_estimate` mede última frame ativa estimada → decisão de endpoint,
incluindo os 800 ms aprovados de silêncio e o efeito dos blocos. Não é uma
medição acústica do último fonema: aquisição, buffering, hangover/classificação
e escalonamento do Android introduzem incerteza. Pre-roll incerto sem frame
ativa não produz essa medida nem a métrica principal; não se inventa um início.

`wav_prepare` mede a preparação real do WAV. `stt_multipart_prepare` separa sua
cópia para multipart. Eventos do OkHttp registram início dos headers, início/
fim da escrita do corpo e recepção dos headers. `stt_upload` representa escrita
do corpo no transporte local, sem afirmar recepção remota naquele instante.
`stt_wait_response` inclui API, provedor, rede e leitura/validação da resposta;
os marcos próprios do backend atribuem suas partes sem misturar relógios.

## Saída PCM e player

`tts_first_byte`, `tts_first_pcm_available`,
`tts_first_useful_pcm_available` e `tts_first_useful_pcm_delivered` são pontos
distintos. O critério observável para “útil” é a primeira amostra PCM16 com
`abs(sample) >= 64`, inclusive quando dividida entre chunks HTTP. Esse critério
detecta sinal acima do piso digital escolhido; não prova conteúdo linguístico,
inteligibilidade ou pressão sonora. O índice dessa amostra também é registrado.
A busca para na primeira ocorrência e não processa novamente o restante do
áudio. Silêncio inicial nunca melhora o marcador de áudio útil.

“Disponível” antecede `sink.write`; “entregue” ocorre quando a escrita que contém
a primeira amostra útil retorna com sucesso. Isso é um limite superior local
para a entrega dessa amostra, sujeito ao tamanho do bloco/backpressure.
Falha ou cancelamento dessa escrita não registra entrega útil.

`player_play_called` é a chamada de software `AudioTrack.play()`. Uma única
consulta do head após a primeira escrita pode produzir
`player_first_head_observed`; ausência desse ponto significa início não
observado. `player_drained` ocorre somente após o head confirmar todas as
amostras, reutilizando a espera/marker de término existentes. Não foi criado
polling para tentar detectar o primeiro sample no alto-falante.

A principal métrica nova é
**`vad_frame_to_first_useful_pcm_delivered_estimate`**. Inclui endpointing,
WAV, STT, chat, transporte TTS e entrega local. O nome e o registro declaram a
estimativa e `speakerAcousticsMeasured=false`; ela não certifica a meta física
de quatro segundos. `endpoint_to_first_useful_pcm_delivered` permite comparação
mais próxima do histórico `processing → speaking`, conservando as diferenças
de definição. Os tempos totais de fetch/reprodução permanecem sobrepostos e
não devem ser somados como etapas independentes.

Cada registro informa resultado (`played`, `empty`, `failed` ou `cancelled`).
Não se emite a métrica principal sem entrega útil observada. A continuação
continua sendo decidida após a drenagem real e mantém o prazo de dois segundos.

## Verificação reproduzível

```sh
cd apps/aegis-node/src-tauri/gen/android
ANDROID_HOME=/tmp/aegis-hands-free-sdk \
ANDROID_SDK_ROOT=/tmp/aegis-hands-free-sdk \
JAVA_HOME=/usr/lib/jvm/java-25-amazon-corretto \
  ./gradlew :app:testUniversalDebugUnitTest \
  --tests 'com.aegis.node.NodeHandsFree*Test' --console=plain
```

**63 testes passaram** (14 de áudio, 23 de backend, 16 de métricas, dez de PCM),
com compilação do Kotlin de produção. Exercitam HTTP real em loopback com fragmentação/atraso controlados,
correlação STT/chat/TTS, silêncio inicial, amostra negativa dividida, escrita
cancelada, métricas com relógio injetado, identidade inicial preservada e
endpoint sem mudança de duração. Também cobrem EOF, backpressure, cancelamento
e continuação somente depois do head simulado terminar. São testes de contrato
e dispositivos simulados, não reprodução física Android.
