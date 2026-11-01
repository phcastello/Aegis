# STT realtime com finalização local — v0.8.0

O hands-free abre uma conexão **somente após a wake word**, envia o pre-roll e
os novos blocos da mesma captura PCM16 mono/16 kHz e pede commit quando o VAD
local aprovado encerra o turno. Não há STT durante a espera passiva, reabertura
do microfone, novo endpointing remoto ou execução de chat/ferramentas em parcial.
O batch anterior permanece disponível para clientes antigos e fallback técnico
limitado; não roda em paralelo com realtime em toda requisição.

## Contrato nativo

`GET /api/voice/transcriptions/realtime?transcriptionRequestId=<UUID>` com upgrade
WebSocket e `Authorization: AegisNode <credencial existente>`. Autenticação usa a
política nativa `NodeManagement` antes do upgrade; o UUID de tentativa não concede
acesso. A credencial é conferida novamente antes do commit e da entrega final.
O backend mantém a chave ElevenLabs e nunca a inclui em mensagens, erros ou logs.

Servidor envia:

```json
{"type":"ready","transcriptionRequestId":"UUID","model":"scribe_v2_realtime","minAudioMilliseconds":0}
```

O cliente envia mensagens binárias com PCM s16le, mono, 16 kHz, em ordem; recomenda-se
200 ms por mensagem. Não enviar WAV ou cabeçalho; enviar somente o pre-roll
relevante e o comando capturado após a ativação. Nenhuma sílaba precisa
esperar pela rede. Ao fim local, enviar exatamente `{"type":"commit"}`.

Servidor envia uma única conclusão de tentativa:

```json
{"type":"final","transcriptionRequestId":"UUID","text":"transcrição agregada"}
```

ou `{"type":"error","transcriptionRequestId":"UUID","code":"código limitado"}`.
Erros não encaminham detalhes crus do provedor. Desconexão/timeout/erro técnico
permitem ao cliente cancelar realtime e usar uma vez o WAV já capturado. O cliente
é a autoridade de finalização da tentativa e deve descartar callbacks antigos;
o relay não inicia conversas e não contém referência a chat ou ferramentas.
Texto final vazio encerra sem conversa, evitando interpretar silêncio como ação.

## Provedor, limites e segmentos

Contrato atual ElevenLabs `/v1/speech-to-text/realtime`, modelo
`scribe_v2_realtime`, `audio_format=pcm_16000`, `commit_strategy=manual`,
`no_verbatim=false`, `language_code=pt`. Não há revisão ou reformulação por LLM.
A sessão deve confirmar 16 kHz, modelo correto e commit manual. A conta real
confirma manual por `config.vad_commit_strategy=false`; também se aceita a
representação explícita `config.commit_strategy="manual"`. VAD remoto habilitado
por engano rejeita a sessão em vez de adicionar outra pausa.

Cada `committed_transcript` é agregado em ordem. O evento complementar
`committed_transcript_with_timestamps` nunca acrescenta o mesmo segmento outra
vez; parciais não autorizam conclusão. Não se remove repetição legítima comparando
strings iguais. A conclusão é entregue somente depois do commit local, incluindo
segmentos confirmados anteriormente. A sessão não solicita commits periódicos.
O limite de áudio é inferior aos aproximadamente 36 segundos em que o provedor
poderia efetuar seu commit automático.

Limites por conexão: 23 s de PCM, mensagem binária de até 64.000 bytes, 16.000
caracteres de transcrição, até 32 segmentos, 8 s para conexão/configuração, 40 s
totais e 10 s para resposta ao commit. O prazo de commit não estende o prazo total.
Há no máximo quatro sessões simultâneas, uma por identidade nativa, sem retries
no relay. Cancelamento/desconexão libera o socket pago e sua capacidade; retorno
do aplicativo usa nova tentativa. Não há keepalive com áudio falso, polling ou
conexão STT permanente. Cancelar apenas este relay não altera a conexão normal
de presença do Node.

## Validação e limites da evidência

Documentação oficial consultada em 01/11/2026:
[server-side streaming](https://elevenlabs.io/docs/eleven-api/guides/how-to/speech-to-text/realtime/server-side-streaming),
[transcripts/commit](https://elevenlabs.io/docs/eleven-api/guides/how-to/speech-to-text/realtime/transcripts-and-commit-strategies)
e [event reference](https://elevenlabs.io/docs/eleven-api/guides/how-to/speech-to-text/realtime/event-reference).
A documentação descreve início de processamento após cerca de dois segundos;
isso não é tratado como uma espera artificial obrigatória para commit curto.

Probe limitado da conta configurada confirmou modelo/configuração acima, sem
mudar serviço ou assinatura. Áudio sintético reservado de 3.481 ms, transmitido
em blocos de 200 ms, teve handshake de 317 ms e commit → confirmação de 164 ms.
Um segundo áudio natural curto de 589 ms também recebeu confirmação, em 173 ms,
sem preenchimento ou espera: não foi necessário forçar fallback em todo comando
menor que dois segundos. A transcrição sintética curta divergiu da palavra
esperada; este resultado não comprova qualidade de comandos curtos no telefone.
As transcrições e os arquivos continuam privados, e as amostras não compõem
aprovação acústica do aparelho. O custo destas sessões decorre do áudio transmitido;
não há STT pago contínuo. O preço por unidade depende da conta existente.

Os dez `RealtimeTranscriptionTests` aprovados exercitam WebSocket real do ASP.NET TestServer com
autenticação nativa e provedor simulado: ausência de autenticação, configuração,
bytes exatos, commit curto sem padding, segmentos múltiplos, representação
duplicada, ausência de final antes do commit, limites, falha sanitizada,
revogação, cancelamento, nova tentativa e capacidade. TestServer não comprova
proxy de produção ou telefone. O proxy PWA existente já preserva upgrade HTTP/1.1,
Authorization, buffering desativado e timeouts de 300 s; a verificação do WSS real
faz parte da implantação e não requer enfraquecer a configuração global.
