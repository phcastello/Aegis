# Interrupção durante processamento — Android

## Referências e decisão

O [LiveKit Agents](https://docs.livekit.io/agents/logic/turns/) separa a
detecção de turnos e a gestão de interrupções. Sua
[detecção adaptativa de interrupções](https://docs.livekit.io/agents/logic/turns/adaptive-interruption-handling/)
tenta distinguir fala dirigida, sinais de atenção e sons incidentais; também
prevê VAD convencional nas fronteiras do turno. A Aegis reutiliza o VAD local
e o controle de nível já testados, sem adicionar um serviço de áudio externo.
O [protocolo de interrupção e truncamento da OpenAI Realtime](https://developers.openai.com/api/docs/guides/realtime-conversations#interruption-and-truncation)
motiva cancelar o turno quando a fala é confirmada e distinguir a parte de
uma resposta efetivamente ouvida do conteúdo produzido antecipadamente.
A Aegis não migra para a API Realtime: conserva o STT, o modelo, a síntese de
voz e o cancelamento do pipeline existente.

## Captura e transferência

- Na fase de processamento de uma sessão hands-free, um worker abre um
  microfone em modo normal sem esperar a requisição de STT/modelo terminar.
  Não há envio contínuo de áudio nem uma nova escuta da palavra de ativação.
- Antes de tocar qualquer resposta, usa o libfvad em blocos de 20 ms, um piso
  de ruído calibrado com 200 ms e quatro observações de fala em até 140 ms.
  Guarda até 300 ms de áudio anterior à confirmação, incluindo o início
  plausível de um pedido, e usa o endpointing existente para completá-lo.
  A referência externa de mídia, se disponível e consentida, continua
  passando pelo filtro opcional.
- Ao chegar o primeiro PCM, a transição de processamento para reprodução é
  mutuamente exclusiva com a aceitação de fala. O microfone de processamento é
  encerrado e seu fechamento conferido antes de abrir o caminho nativo ou
  software de cancelamento da própria voz. Se a interrupção já foi aceita,
  playback e geração antigos são invalidados, e esse microfone continua
  coletando o pedido. Não se introduz uma aceitação baseada exclusivamente na
  ausência de timestamp de playback: durante fala sintetizada continua sendo
  necessária a evidência do caminho acústico aprovado.
- A primeira decisão confirmada mantém a identidade e a sessão, reutilizando
  o chat já persistido. Não
  emite beep nem pede nova invocação. Se um segmento anterior já foi
  registrado, a posição ouvida é zero enquanto ainda não houve TTS audível;
  caso contrário, a nova requisição leva a referência ao turno interrompido
  e o comando anterior transcrito (`interruptedGeneration`). Não leva texto
  produzido pela assistente mas ainda não ouvido. Se a primeira transcrição
  ainda não terminou, reaproveita o PCM original com o PCM da correção e
  250 ms de separação para uma nova transcrição; o conjunto é limitado ao
  contrato WAV de 90 segundos e preserva os pedidos mais recentes.
- Cancelamento da sessão, perda de identidade, permissão/rota indisponível
  ou falha da captura não autorizam um turno novo. O backend responde sob o
  mesmo identificador de sessão e resolve o destino persistido mesmo quando
  o marcador de conversa não foi recebido antes do cancelamento.

## Limites

O barge-in durante processamento não é reconhecimento de falante nem separação
semântica de falas alheias. Os 200 ms de calibração evitam aceitar ruído
estacionário inicial, mas uma fala muito curta inteiramente dentro dessa
calibração pode não interromper; o pré-buffer preserva o início de falas que
continuem até a confirmação. Entre o fechamento do microfone de processamento
e a abertura da rota de comunicação há uma transição de hardware, cuja
duração é dependente do dispositivo. O comportamento físico, falsas
interrupções, atraso de abertura e consumo nas sessões ainda requerem
homologação em aparelho real. Não foi alterada a escuta passiva fora da sessão.

## Evidência de bancada

- 117 testes JVM das fontes de áudio, captura de barge-in, transporte HTTP,
  playback e ponteiro passaram. As regressões adicionais incluem comando
  curto preservado em fragmentos, ruído/pulsos, aposentadoria concorrente,
  inicialização sem esperar pelo gravador, PCM combinado antes da transcrição
  e cancelamento HTTP antes do marcador de conversa, seguido da requisição
  na mesma sessão com o identificador do turno anterior.
- As fontes Kotlin do runtime, incluindo o controlador, compilaram contra
  SDK Android 37, ONNX Runtime 1.31.1 e OkHttp 4.12, com as fronteiras Android
  de serviço, identidade, notificações e referência externa simuladas. A
  tentativa de Gradle local ficou bloqueada pelo arquivo gerado
  `tauri.settings.gradle` ausente; esta não é compilação completa do APK.
- Em bancada JVM Windows x64, o adaptador JNI de produção e libfvad na revisão
  `532ab666c20d3cfda38bca63abbb0f152706c369` foram compilados a partir de
  fonte com hash conferido. A captura Kotlin de processamento, com esse VAD
  real e entrada simulada, aceitou e entregou 19/19 gravações positivas
  reservadas da coleção existente, precedidas de silêncio curto; 0/4
  controles sintéticos (silêncio, dois níveis de ruído e pulsos) interromperam.
  Isso não é uma nova avaliação de precisão do wakeword, nem prova de
  separação de interlocutores ou funcionamento físico do microfone.
