# Vibração, fala curta e falsas interrupções no Android

## Ocorrência e hipótese técnica

O relato na prévia `0.8.0-unstable.44` envolveu uma vibração do telefone, uma interrupção indevida da Aegis e a transcrição de `context:`. A telemetria agregada do S20 FE registrou confirmação **durante processamento**, antes da reprodução daquele TTS (`path=processing`, piso RMS 100, pico RMS 17.472, 84 quadros de comando entregues, STT batch de 2.866 ms). A captura duplex posterior registrou zero quadros independentes e nenhuma interrupção aceita. Assim, não se deve atribuir esse episódio somente ao eco da própria Aegis: a evidência aponta para uma vibração mecânica que satisfez a detecção convencional de atividade vocal no estado de pensamento. A causa exata da palavra transcrita não pode ser provada sem gravação; não há evidência de vazamento do contexto da conversa.

O caminho duplex também merece um controle específico: em `HandsFreeBargeInOnset` da `.44`, o modo `learnedEvidence` vota a partir da evidência independente do AGD4, que compara captura e reprodução. Independência acústica e presença de fala humana não são a mesma propriedade. Tanto o VAD tradicional no caminho de processamento quanto o detector condicionado à referência no caminho de reprodução podem aceitar uma fonte mecânica que não é a voz do usuário. O filtro deve preservar o início de comandos curtos e evitar desligar indiscriminadamente o detector sempre que o aparelho se mover. A investigação da STT e de transcrições vazias está separada em [hands-free-realtime-stt.md](hands-free-realtime-stt.md).

## Pesquisa e alternativas

- O [filtro de bancos do WebRTC VAD](https://webrtc.googlesource.com/src/+/b6ee51b7a55386f028b3e9195cf222082a1839bc/common_audio/vad/vad_filterbank.c) mede energia em várias regiões espectrais, incluindo o passa-altas de 80 Hz e as bandas de 80–250 Hz até 3–4 kHz. A implementação [RNN VAD do WebRTC](https://webrtc.googlesource.com/src/+/0b489303d237d84aeb4fa2833d37c3497deafccc/modules/audio_processing/agc2/rnn_vad) utiliza características de pitch e espectro. Um VAD não é, isoladamente, um detector específico de motor vibratório; a avaliação deve incluir fala periódica legítima, principalmente vozes graves e vogais curtas.
- O código AOSP de [`Vibrator.addVibratorStateListener`](https://android.googlesource.com/platform/frameworks/base.git/+/refs/heads/main/core/java/android/os/Vibrator.java) é uma API de sistema que exige `ACCESS_VIBRATOR_STATE`. Depender desse evento não é uma solução disponível a um app Android de terceiros genérico. O [acelerômetro Android](https://developer.android.com/develop/sensors-and-location/sensors/sensors_motion) pode fornecer evidência mecânica opcional, mas o [limite normal de 200 Hz para listeners no Android 12+](https://developer.android.com/develop/sensors-and-location/sensors/sensors_overview#sensors-rate-limiting) e a variabilidade entre sensores impedem tratá-lo como medida universal da frequência de um motor de 150–300 Hz. Evitar novo sensor contínuo em repouso, requisito adicional de permissão ou veto só por movimento. Se for necessário investigar contato mecânico no aparelho, usar aceleração como evidência complementar durante a interação, nunca como prova de ausência de voz.
- A implementação de [decodificação do Whisper](https://github.com/openai/whisper/blob/main/whisper/transcribe.py) combina probabilidade de ausência de fala com confiança da decodificação e, opcionalmente, evidência temporal para lidar com regiões de silêncio e alucinações. É um princípio de projeto, não uma suposição de que o provedor vigente da Aegis seja Whisper ou exponha os mesmos escores. A palavra `context` é legítima; bloquear determinadas strings não corrige falsa interrupção/acústica e pode perder comandos válidos.

## Estudo independente de características (não é o detector de produção)

O script [`haptic-feature-bench.py`](../../scripts/barge-in-dtd/haptic-feature-bench.py) isola uma hipótese deliberadamente estreita: um sinal de motor muito periódico e espectralmente concentrado pode ser descartado como evidência de fala. A combinação é analisada somente em PCM de 16 kHz, sobre dois quadros consecutivos de 20 ms. O veto experimental exige **todos** os seguintes itens:

1. RMS do quadro atual de pelo menos 45 (escala PCM16), média removida no contexto de 40 ms, janela Hann de 640 amostras, FFT de 1.024 pontos.
2. Pelo menos 85% da energia de 60–5.000 Hz está em 60–450 Hz e no máximo 8% em 600–4.000 Hz.
3. A autocorrelação normalizada máxima para lags de 40–200 amostras (80–400 Hz) é de pelo menos 0,985.
4. Concentração de pelo menos 95% da energia em torno do maior pico de 60–400 Hz (±3 bins) **ou** similaridade cosseno de pelo menos 0,999 entre os espectros de potência normalizados L2 de 60–4.000 Hz atual e anterior.

Autocorrelação, estabilidade e concentração não são utilizadas de modo isolado. A hipótese é rejeitar apenas sinais muito semelhantes a um motor tonal; fala que se sobrepõe ao motor deve mudar as características e manter a oportunidade de confirmar. Na integração real, o filtro deve atuar antes de confirmar/cortar a resposta, em ambos os estados de captação, e não apagar votos/acústica comprovada do usuário por qualquer movimento. Estado do filtro deve acompanhar as descontinuidades da captura. Executá-lo somente durante a interação evita adicionar custo à escuta de wakeword em repouso. As constantes do estudo não constituem automaticamente os valores finais do detector Android: a validação com libfvad, AGD4, AEC e captura reais é indispensável.

### Método da bancada independente

Esta bancada **não executa libfvad nem AGD4**. Para separar o efeito das características das decisões desses classificadores, supõe-se propositalmente que *todo* quadro acima de RMS 45 daria um voto positivo; calcula-se se a sequência de seis votos em doze quadros confirmaria uma interrupção com e sem veto. Essa política fornece oportunidades de corte em cenário adverso, não previsão de taxa de falso disparo da Aegis. Não compara latência fim-a-fim nem captura acústica real.

Os negativos são sete formas sintéticas determinísticas: senoide, harmônicos, motor saturado, pulsos, ressonâncias, contato/rattle e rampa de frequência. Cada grupo inclui cinco frequências, três níveis e três durações (160, 320, 560 ms), portanto 315 tentativas por conjunto. As falas sintéticas são os clipes `supertonic3-{f,m}{1..5}-{oi-diz,command}` da coleção já gerada, cortados em 160–460 ms e separados por voz. Depois de selecionar a hipótese estreita no desenvolvimento, a comparação sintética foi usada como verificação intermediária. A avaliação nova utilizou frequências, níveis e sementes distintos e 12 vozes naturais públicas (referências de reprodução do conjunto final do [Microsoft AEC Challenge](https://github.com/microsoft/AEC-Challenge/tree/8f861f0f8ffca26a3d1d16f17f2594ea89abe7ac), `room-19..30-render.pcm`), excluídas do treino/validação do modelo AGD4. Nenhum áudio privado foi utilizado nesta bancada independente.

Um veto mais amplo, com periodicidade menor, reduziu muitos negativos sintéticos, mas também perdeu fala verdadeira quando havia vibração intensa. Por exemplo, entre 315 controles de comparação, bloqueou 216 oportunidades de corte, porém perdeu uma fala seca e preservou somente 8/24 casos de fala misturada a um buzz 10× mais forte. Essa configuração foi abandonada. A avaliação abaixo corresponde à hipótese estreita congelada, não a uma redução global do limiar.

### Resultados

| Conjunto de vibrações (sem fala) | Oportunidades de corte sem veto | Com veto | Interpretação |
| --- | ---: | ---: | --- |
| Desenvolvimento simulado | 315/315 | 120/315 | Bloqueia principalmente forma mecânica tonal |
| Comparação sintética | 315/315 | 132/315 | Verificação intermediária, não novo holdout após outros experimentos |
| Frequências/sementes novas | 312/315 | 120/315 | Os 180 casos senoide/harmônicos/motor/rampa bloqueados |
| Alta amplitude/clipping, RMS 9,5–24,9 mil | 315/315 | 119/315 | Os mesmos 180 casos tonais bloqueados, inclusive saturação |

| Conjunto de fala | Sem veto | Com veto |
| --- | ---: | ---: |
| Sintética, desenvolvimento, seca | 108/108 | 108/108 |
| Sintética, comparação, seca | 72/72 | 72/72 |
| Sintética, comparação, sobre vibração 0,5×/1×/3×/10× | 96/96 | 95/96 |
| Natural pública nova, seca | 104/108 | 104/108 |
| Natural pública nova, vibração 0,5×/1×/3× | 108/108 | 108/108 |
| Natural pública nova, vibração 10× | 36/36 | 30/36 |
| Natural pública, fala forte/clipping | 108/108 | 108/108 |
| Natural pública, motor RMS 17.500 + fala RMS 1.000 | 36/36 | 21/36 |
| Natural pública, motor RMS 17.500 + fala RMS 2.000 | 36/36 | 32/36 |
| Natural pública, motor RMS 17.500 + fala RMS 3.500 | 36/36 | 34/36 |

Após uma vibração sintética de RMS 17.500 e duração de 240 ms, o veto preservou 36/36 oportunidades de fala em cada separação de 0, 80 ou 180 ms. Persistiram 12/36 oportunidades de corte *antes* da fala em cada caso de teste (a forma de contato mecânico complexa). Isso significa que a hipótese estreita pode tratar um motor dominante sem impedir a retomada quando o motor para, mas **não elimina todo tipo de vibração/ruído mecânico**. Contato/rattle e várias ressonâncias continuam indistinguíveis por esta regra estreita, e fala muito mais silenciosa que a vibração também perde sensibilidade. Esses limites são preferíveis a declarar genericidade sem evidência ou aumentar globalmente o limiar de fala.

### Reprodução

Os áudios permanecem fora do repositório. O script recebe WAV (FFmpeg converte em memória para mono 16 kHz PCM16) ou PCM/S16 little-endian já nesse formato e só emite contagens agregadas. Requer NumPy; não instala recursos nem executa API/modelo remoto. Para reproduzir os números públicos, preparar os pares públicos por [`scripts/windows-barge-in-bench/prepare.py`](../../scripts/windows-barge-in-bench/prepare.py), com `--split final`, conforme [a bancada Windows](windows-barge-in-signal.md). O diretório deve conter as 12 referências `room-19..30-render.pcm`; arquivos `-mic` são ignorados. O diretório sintético é opcional, com os 20 clipes Supertonic acima. Não fornecer gravação do proprietário. Exemplo:

```powershell
python scripts/barge-in-dtd/haptic-feature-bench.py `
  --synthetic-dir $synthWavs --public-dir $publicAecPcm `
  --output $resultadoJson
```

Se apenas a coleção pública estiver disponível, omitir `--synthetic-dir`; se apenas os clipes sintetizados estiverem disponíveis, omitir `--public-dir`. O JSON identifica a política de votação hipotética e seus parâmetros congelados. A reprodução deve ser interpretada como estudo de características, **não como aprovação do filtro que realmente está no app**.

## Homologação necessária no S20 e outras rotas

Separar claramente vibração durante pensamento (VAD) e durante fala/TTS (AEC + AGD4). Ensaiar notificações vibratórias com telefone apoiado e na mão, diversos padrões/durações e volume de reprodução zero/baixo/alto; depois comandos curtos de verdade com e sem vibração simultânea e imediatamente após o fim do motor. Em cada caso, verificar corte indevido, preservação do início da fala, resultado STT e continuidade da mesma conversa. Se houver falha, registrar agregados de votos, veto mecânico e RMS — sem guardar áudio por padrão. Nenhuma das métricas sintéticas substitui esse teste em dispositivo físico.

## Validação do detector Android integrado

O relatório recente da .44 registrou interrupção no caminho **processing**
(captura antes da primeira reprodução), RMS de fundo de 100 e pico RMS
de 17.472 PCM16. Não havia áudio no relatório para confirmar o formato
espectral da vibração física. O problema não se limita ao cancelamento do
próprio TTS: o VAD pode aceitar motor como voz e o AGD4 pode considerar
a vibração independente do eco. O caminho native AEC, quando qualificado,
também permite confirmação com escore ≥0,70; outros casos usam o limiar
estrito congelado de 0,769403 e a janela de 6 votos em 12 quadros.

A implementação usa o veto estreito descrito acima em
`HandsFreeBargeInOnset`, tanto antes da reprodução como durante ela.
Analisa os 40 ms causais do sinal disponível ao detector, sem esperar
amostras futuras, com buffers locais reutilizados. Apenas o voto e a
retenção do quadro mecânico são rejeitados; votos de outros quadros,
limiares existentes, calibração, janela e detecção de fala continuam
como antes. Não há novo processamento no wakeword contínuo nem alteração
do áudio de saída ou do PCM transmitido após confirmar uma fala. O estado
espectral e as amostras anteriores são descartados com a perda da
referência, omissão de quadros, reinício ou fechamento. Os resumos
`barge_in_capture` registram somente `mechanicalVetoFrames`, a contagem
agregada de quadros que chegaram com evidência independente e foram
vetados pelo teste mecânico; não são registrados áudio ou espectros.

### Replay do pipeline real em bancada

- Playback: modelo AGD4 distribuído, classe Kotlin real
  `HandsFreeDuplexSpeech`, referência de TTS sintética PCM16 mono 24 kHz,
  qualificação de AEC nativo simulada por microfone sem eco, e
  `HandsFreeBargeInOnset` real. O motor simulado variou de 140 a 300 Hz,
  picos de 400/1.200/3.600 PCM16, duração de 240/600 ms, senoide ou com
  harmônicos. Nenhum acesso a microfone físico.
- Processing: pipeline real `HandsFreeBargeInCapture`, leitura de amostras
  simulada com relógio derivado das amostras, libfvad JNI compilada do
  código fixado pelo aplicativo e mesma calibração de nível/endpoint.
  Motor de 140–300 Hz, picos de 1.200/3.600/24.000/48.000 PCM16 antes de
  saturação, 240/600 ms, duas formas por frequência. A última intensidade
  satura a onda; a penúltima produz RMS comparável ao pico do relatório.
  A fala usa os 19 clipes de teste privados já existentes do proprietário
  (fora de treino), sem salvar cópias de áudio ou enviar dados a serviços.

| Replay | Sem veto | Com veto |
| --- | ---: | ---: |
| Playback, 240 ms motor | 23/30 falsos cortes | 0/30 |
| Playback, 600 ms motor | 24/30 falsos cortes | 0/30 |
| Processing, pico 1.200 | 20/20 falsos cortes | 2/20 |
| Processing, pico 3.600 | 20/20 falsos cortes | 2/20 |
| Processing, pico 24.000 | 16/20 falsos cortes | 2/20 |
| Processing, pico 48.000 (saturado) | 20/20 falsos cortes | 2/20 |
| Processing, fala de teste sem motor | 19/19 entregues | 19/19 entregues |
| Processing, silêncio/ruído branco/clique | 0/4 falsos cortes | 0/4 |

Os oito falsos cortes residuais de processing foram todos no caso
de 300 Hz com os harmônicos simulados. Não se alargou a faixa de veto
para tentar zerar estes casos, evitando penalizar fala. No ensaio
playback as falas privadas em replay artificial pós-AEC tiveram baixa
sensibilidade *na versão original*, dependente de ganho/contexto:
7/19 na amplitude original, 6/19 (pico normalizado 2.500) e 2/19 (pico
6.000). O veto preservou todos esses acertos, sem acrescentar latência nesses casos; esse
ensaio não comprova sensibilidade no aparelho. A bancada pública
independente acima avalia a fala separadamente.

No processing com fala privada e motor 180 Hz, a fala foi reconhecida
em 19/19 tanto durante como imediatamente depois de motor de pico
1.200 ou 3.600. Com pico de 24.000 (RMS próximo do problema reportado),
19/19 durante, 16/19 depois; no caso extremo de saturação (48.000),
12/19 durante e 17/19 depois. Os números de mistura indicam somente
confirmação de interrupção, não inteligibilidade da transcrição. O
resultado depende da forma da vibração e da relação sinal/ruído.
A solução é deliberadamente conservadora: ruído de contato broadband
não é necessariamente rejeitado e precisa de validação física adicional.

O replay com a implementação integrada reproduziu os resultados anteriores
do candidato. Em JVM no computador de desenvolvimento, após aquecimento
de 3.000 quadros e 5.000 quadros por condição, o tempo p95 do novo veto foi
8,6 μs (silêncio), 10,6 μs (sinal com formantes e frequência variável) e
34,2 μs (motor), por bloco de 20 ms. Essa medição exclui o VAD/AGD4/AEC
existente, não mede CPU/bateria física Android e não representa custo em
escuta contínua: o gate só existe enquanto uma resposta está sendo
processada ou reproduzida.
