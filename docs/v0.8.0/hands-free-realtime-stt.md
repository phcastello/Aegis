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

## Salvaguarda de transcrição sem fala (.44)

O proprietário relatou uma interrupção involuntária enquanto a Aegis falava no
S20 FE, seguida de uma transcrição inesperada `context:`. A vibração/interrupção
precede a transcrição e é decisão acústica local; o relay STT não dispara
barge-in. No Scribe realtime a Aegis envia somente áudio e parâmetros de formato
e commit, sem `previous_text`, conteúdo da conversa, prompt ou termos de contexto.
No batch há apenas termos técnicos configurados (ElevenLabs) ou uma instrução
fixa de fidelidade da transcrição (OpenAI). A transcrição estranha pode ser uma
inferência incorreta do provedor sobre eco/ruído enviado após a falsa
interrupção. Não há evidência de vazamento de contexto da conversa nem uma
gravação correlacionada que prove a origem da string.

Há uma diferença importante entre a resposta válida do reconhecimento sem
palavras e uma falha técnica. Um `text` string vazio/branco é resultado vazio
válido em realtime e batch e não dispara outro reconhecedor. Campo ausente,
nulo, tipo incorreto, falha de transporte ou timeout permanecem falhas técnicas
com a política de fallback anterior. Assim se evita pedir que outro modelo
interprete novamente como fala um áudio já reconhecido sem palavras.

No relay PCM16, se **todos** os bytes de áudio forem zero, um commit local
autenticado finaliza imediatamente com texto vazio, mesmo que o provedor tenha
sugerido um texto durante essa captura; a sessão upstream é fechada sem commit
remoto. O endpoint batch identifica o mesmo caso somente no WAV PCM16,
mono/16 kHz canônico e estruturalmente válido dos nodes, sem chamar o provedor.
Mesmo a menor amostra não nula preserva o fluxo normal. Isso não é um VAD:
ruído, eco, voz fraca, áudio comprimido e WAV não reconhecido não são
arbitrariamente classificados como silêncio digital, e nenhuma palavra,
inclusive a citada pelo proprietário, é proibida. Não se muda modelo,
endpointing, ganho nem AEC. Logs acrescentam somente indicadores booleanos
de silêncio digital e presença de texto reconhecido, sem gravar transcrições.

A API oficial [de eventos realtime](https://elevenlabs.io/docs/eleven-api/guides/how-to/speech-to-text/realtime/event-reference)
descreve texto final e timestamps opcionais; não apresenta uma confiança de
fala calibrada no evento final consumido nesta integração. O
[batch](https://elevenlabs.io/docs/api-reference/speech-to-text/convert)
expõe log-probabilidades por palavra nos dados detalhados. Não se atribui a
essa medida um limiar de confiabilidade acústica sem benchmark, nem se introduz
uma segunda consulta ao provedor. A bancada usa resultados sintéticos para
provar ausência de fallback em texto vazio, distinção entre vazio e resultado
malformado, silêncio PCM exato, preservação de entrada não nula, autorização
e ordem do commit. Esses testes não demonstram que a ocorrência física de
`context:` foi eliminada no S20 FE; a validação acústica é separada.

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


### Incidente observado na prévia .44 (S20 FE, 25/11/2026)

O relatório sem áudio do aparelho correlaciona uma interrupção na preparação
(`path=processing`) com fundo RMS 100 e pico RMS 17.472, seguida de um turno
com STT em lote. A tentativa correspondente nos logs estruturados do servidor
teve 2.060 ms de áudio WAV: ElevenLabs `scribe_v2` retornou HTTP 200, mas a
aplicação marcou falha técnica; depois, o fallback OpenAI `gpt-4o-transcribe`
foi bem-sucedido. O proprietário relatou que o telefone vibrou e surgiu a
transcrição `context:`. O corpo da resposta do primeiro provedor não foi
retido; os registros são compatíveis com um texto vazio interpretado como
falha, mas não distinguem texto vazio de uma resposta estruturalmente inválida.
O caminho realtime da Aegis não envia um contexto textual de conversa. O
filtro acústico de vibração e a distinção entre ausência de palavras e erro
real tratam as duas camadas, sem lista proibida de palavras e sem mudança de
provedor para fala válida. A ocorrência isolada não demonstra uma taxa geral
de alucinação do serviço, e uma repetição física ainda será necessária.
