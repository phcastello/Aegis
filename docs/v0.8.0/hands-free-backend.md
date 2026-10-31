# Contrato conversacional hands-free — v0.8.0

O Android usa os serviços de STT, chat, cancelamento e TTS existentes. Não há
novo backend ou provedor de voz, nem migration. As rotas de chat/voz mantêm o
acesso pela infraestrutura privada atual; o pareamento do Node e suas credenciais
não são substituídos por reconhecimento de voz.

## Captura e transcrição

`POST /api/voice/transcriptions`, multipart:

- `audio`: WAV PCM mono 16 kHz, nome `command.wav`, tipo `audio/wav`;
- `transcriptionRequestId`: UUID exclusivo da tentativa;
- `clientDurationMilliseconds`: duração do WAV em milissegundos.

Resposta: `{ "transcriptionRequestId": "UUID", "text": "pedido" }`.
O limite corrente do servidor é configurável (padrão de 90 segundos/20 MiB).
Silêncio, erro e texto vazio não iniciam chat. O pre-roll pode conter o nome:
Android e servidor retiram apenas o `Aegis` inicial isolado; nome sem pedido
não cria conversa, mensagem nem chamada ao modelo.

## Turno e destino fixos

`POST /api/chat/messages`:

```json
{
  "turnId": "UUID-do-turno",
  "conversationId": null,
  "origin": "hands_free",
  "voiceSessionId": "UUID-da-sessao",
  "content": "O que tem de importante hoje?"
}
```

`conversationId=null` cria a conversa somente após a validação do pedido.
Uma continuação usa o `conversationId` retornado e o mesmo `voiceSessionId`, com
novo `turnId`. A interface do chat não escolhe esse destino durante uma sessão.
`voiceSessionId` é obrigatório para `origin=hands_free`. O campo não autentica o
proprietário. A origem e a sessão entram no contexto operacional/auditoria do
turno e nos metadados do modelo; as verificações de ferramentas continuam iguais.

A resposta normal conserva `conversationId`, `conversationTitle`, `titleSource`
e `message`, e acrescenta `voiceContinuation: "continue" | "close"`.
`message.id` identifica a mensagem para TTS. A rota opcional
`POST /api/chat/messages/stream` recebe os mesmos campos e acrescenta a decisão
no evento NDJSON `done`; consumidores anteriores podem ignorar o campo.

## Decisão no próprio turno

Somente turnos com origem `hands_free` oferecem a função terminal
`hands_free_reply({ speech, continuation })`. O modelo a usa depois de suas
consultas/ações normais. O loop transforma `speech` em resposta final e encerra
sem executar outra rodada do modelo para confirmar ou decidir continuação.
Essa função não executa ações, não concede permissões e não conta como confirmação
de uma ferramenta. Uma chamada terminal misturada com chamadas de ação é
rejeitada antes de executar a lista ambígua.

`continue` significa que, após a reprodução realmente terminar, o Android abre
até dois segundos para início de fala. Quando a fala começa a tempo, o VAD limita
o fim do comando; os dois segundos não truncam sua duração. `close` retorna à
wake word. Texto final sem função, decisão ausente/inválida ou timeout de fala
usa `close`; nenhum caso cria escuta de comando indefinida. Resposta terminal
vazia ou malformada falha sem inventar uma mensagem.

Na rota streaming, o texto hands-free é retido somente até a conclusão da rodada:
comentário intermediário de ferramenta não se mistura à resposta falada final.
O streaming dos clientes atuais sem origem hands-free permanece incremental.

## Reprodução e cancelamento existentes

`POST /api/voice/speech` recebe:

```json
{
  "turnId": "UUID-do-turno",
  "speechRequestId": "UUID-da-reproducao",
  "assistantMessageId": "message.id"
}
```

O Android valida `X-Aegis-Audio-Format=pcm_s16le`,
`X-Aegis-Sample-Rate=24000`, `X-Aegis-Channels=1` e reproduz o PCM
nativamente. Usa a mensagem persistida e o perfil de voz existente. O fim da
transferência HTTP não representa o fim da reprodução no alto-falante.

`DELETE /api/chat/turns/{turnId}` cancela o turno e TTS associado.
`DELETE /api/voice/speech/{speechRequestId}` cancela apenas a reprodução.
Falhas de TTS não autorizam janela de continuação baseada em reprodução inexistente.
O registro normal de turnos impede falar uma mensagem de outro turno/conversa.

## Verificação

`HandsFreeVoiceTests` cobre resposta terminal streaming/síncrona sem rodada
adicional, continuidade de conversa e elegibilidade do TTS, contexto operacional,
função normal seguida de resposta, fallback fechado, lote ambíguo sem ação,
compatibilidade do chat comum e ativação sem pedido antes de persistir.
Os testes usam modelo falso e EF em memória: não comprovam qualidade da decisão
real do modelo, rede do aparelho ou funcionamento físico do áudio.

Verificação local em 31/10/2026: 54 testes aprovados, nenhum falho ou ignorado,
selecionando `HandsFreeVoiceTests`, `ToolLoopTests`, `PromptPayloadTests`,
`OpenAiStreamingToolTests` e `AssistantRefinementTests`, com SDK .NET 8.0.426.
Dois avisos já existentes foram emitidos em `NodeTransportTests` (nullable) e
`CalendarTests` (xUnit2031).

## Transporte Android

`NodeHandsFreeBackend` usa uma instância por sessão nativa, fixando identidade,
origem, sessão e conversa durante suas continuações. Um cancelamento encerra essa
instância. Ele valida o WAV PCM16 mono/16 kHz produzido pelo controlador, o ID
retornado pelo STT, a conversa/mensagem retornadas pelo chat e o formato PCM do TTS.
Redirecionamentos HTTP são recusados. Os corpos têm limites independentes de
64 KiB para STT, 256 KiB para chat e 180 segundos de PCM para reprodução. Um prazo
monotônico limita cada requisição, incluindo upload bloqueado; não há retry de
comando. O prazo usa uma tarefa cancelável por requisição, removida da fila ao
concluir, e sua thread expira durante inatividade.

A autoridade do cofre é verificada nas fronteiras de cada requisição e antes de
entregar o resultado. O laço de bytes verifica apenas cancelamento, evitando
abrir/descriptografar o AndroidKeyStore para cada bloco de áudio. Troca de origem,
credencial, Node ou revogação impede o uso do áudio retornado. Cancelamento descarta
respostas tardias e solicita o cancelamento dos IDs remotos associados.

`NodeHandsFreeBackendTest` usa HTTP real em loopback e identidade sintética
injetada, sem API real nem ações de ferramentas. `org.json:json` é dependência
somente dos testes JVM, pois a classe JSON do `android.jar` é um stub fora do
Android. Os testes verificam bytes multipart, destino, falhas de STT/chat/TTS,
limites, redirecionamento, integridade de IDs/WAV, ativação sem pedido,
continuação, cancelamento concorrente e troca/revogação de identidade.

Revisão do transporte em 31/10/2026: 14 testes de loopback aprovados em JVM,
mais cinco testes de áudio e sete de métricas. O ensaio isolado compilou as fontes
reais de transporte, WAV/VAD, sessão nativa, modelo e métricas com Kotlin 2.3.21,
Android 37 e JUnit 4.13.2; somente as fronteiras de preferências Android e
BuildConfig receberam adaptadores de teste. Não houve leitura de cofre real,
rede de produção ou reprodução física. A execução Gradle integrada é registrada
no relatório geral da entrega.

As observações de bateria ficam também no agregado de cada dia: primeiro,
último, mínimo/máximo, quantidade de amostras e transições observadas de estado
de carga. São observações de eventos do sistema, não consumo atribuível à Aegis,
duração de carregamento ou quantidade de recargas. Mudança de fuso invalida o
cache de agrupamento diário no próximo tick/evento; o export registra os fusos
anterior/atual e a observação anterior, sem completar horas ausentes.
