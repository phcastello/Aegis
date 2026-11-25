# Entrega de falas durante uma geração interrompida

## Contrato compartilhado

Quando a pessoa fala antes ou durante uma resposta, o cliente cancela o turno anterior, preserva a nova fala e envia o próximo pedido à mesma sessão hands-free. A prática segue a separação de turnos e cancelamento descrita pelo [LiveKit](https://docs.livekit.io/agents/logic/turns/) e a distinção entre conteúdo gerado e ouvido da [API Realtime da OpenAI](https://developers.openai.com/api/docs/guides/realtime-conversations#interruption-and-truncation). A Aegis usa o próprio backend segmentado e não depende de uma nova API de áudio externa para arbitrar o contexto.

O próximo `POST /api/chat/messages/stream` inclui `turnId` novo e não vazio, `origin: "hands_free"`, `voiceSessionId` da sessão anterior, `voiceSegments: true`, `allowSilentIgnore: true`, `content` da nova fala e `conversationId` se já o souber. Quando não há posição de reprodução confiável, pode incluir:

```json
{"interruptedGeneration":{"turnId":"<turno-anterior>","previousContent":"<texto anterior opcional>"}}
```

O turno novo e o anterior devem ter identificadores diferentes. O texto de fallback é limitado a 16.384 caracteres e é tratado como conteúdo do usuário, não como instrução privilegiada. `interruptedGeneration` e `interruptedPlayback` são mutuamente exclusivos. Se há um ponto efetivamente ouvido, usar o contrato existente `interruptedPlayback` (turno, mensagem da Aegis, segmento, sequência, frames ouvidos e tamanho do segmento quando conhecido). Não se deve usar o total sintetizado como posição de escuta. O evento `conversation` existente continua sendo a identificação da conversa aceita; o cliente não inventa outro identificador.

## Autoridade e settlement

O registro em memória cria a reserva do turno e seu recibo da geração em uma única operação. O recibo identifica sessão, conversa, mensagem provisória do usuário, conteúdo efetivo e eventual posição herdada. Reserva duplicada com o mesmo `turnId` não pode criar ou substituir um recibo. A autorização do handoff e o cancelamento local do predecessor também ocorrem sob o mesmo bloqueio: um recibo conhecido exige a mesma sessão, e, se fornecida, a mesma conversa. Não se permite usar esse campo para cancelar um turno legado, de texto ou de outra sessão, mesmo que ele esteja somente pendente. Se o predecessor ainda não se registrou, grava-se marcador de cancelamento no mesmo passo, de modo que o POST atrasado não execute ou persista depois da nova fala. A invalidação local é independente da limpeza remota: somente identificadores nativos de segmentos ainda não concluídos são enviados em paralelo para cancelamento pelo handoff, limitados aos slots ativos existentes. O DELETE antigo e o cleanup do stream usam esse mesmo filtro nos turnos segmentados com recibo. Consultar um recibo de resposta que já terminou não reabre nem recancela esse estado terminal.

Com recibo conhecido, o servidor aguarda o predecessor concluir a fronteira de persistência (incluindo atualização dos segmentos interrompidos) por até cinco segundos, antes de ler a conversa. Depois dessa fronteira, a limpeza remota de TTS do stream antigo pode concluir independentemente. Isso evita usar um estado EF de uma gravação em andamento ou inferir persistência a partir de uma resposta HTTP interrompida. Se já existe a mensagem anterior do usuário, ela não é adicionada novamente: a nova fala é um novo turno naquela conversa. Se a geração comprometeu segmentos de resposta mas o cliente ainda não conseguiu ouvir nenhum, registra-se posição zero no primeiro segmento, preservando a distinção entre texto persistido e áudio ouvido. Se o usuário anterior ainda não foi persistido, seu conteúdo efetivo é composto com a nova fala, dando prioridade à correção mais recente e preservando eventual posição de reprodução previamente herdada. Caso a decisão anterior tenha sido `ignored`, não se ressuscita essa fala alheia, mesmo se seu terminal não chegou ao aparelho; o texto ignorado também é descartado do recibo. A nova fala só é persistida após a aceitação do próprio modelo, pelas mesmas regras de descarte silencioso. Com isso, uma sequência de interrupções antes do aceite não cria conversas vazias nem repete um pedido já persistido.

Se o recibo não existe (reinício, expiração, pressão de capacidade ou interrupção antes do registro), o texto anterior fornecido pelo cliente é apenas fallback. O servidor não deduz uma conversa a partir de ID desconhecido nem cancela um turno não associado. Cadeias cujos recibos foram registrados mantêm o conteúdo efetivo; sem recibo, não é possível inferir metadados ancestrais não fornecidos pelo cliente. A aplicação deve manter uma única troca de turno em andamento por sessão. Repetir o mesmo `turnId` é rejeitado; criar IDs diferentes representa solicitações distintas, não repetição idempotente automática. A memória do processo mantém no máximo 512 recibos, com retenção de até dez minutos após settlement (sob pressão, somente recibos terminais são candidatos a descarte). Sem migração, sem áudio salvo e sem mudança em clientes anteriores que não enviam o campo.

O cancelamento não pode desfazer uma ação externa que já ocorreu, como criação de lembrete ou envio de e-mail. O contexto orienta o modelo a não presumir que a ação tenha sido ou não executada após uma interrupção. As garantias de persistência e idempotência dos respectivos serviços continuam aplicáveis.

## Testes

- Aceite do predecessor após compromisso do primeiro segmento: mantém a conversa, duas mensagens distintas do usuário, posição zero ouvida.
- Interrupções sucessivas antes do aceite: conserva todos os pedidos/correções em uma única mensagem composta, sem fallback duplicado nem conversa fantasma.
- Settlement concorrente em dois contextos de banco: novo modelo aguarda a persistência/metadata do fluxo cancelado, mas pode prosseguir enquanto a limpeza remota antiga está pendente; preserva a conversa efetivamente persistida.
- Recibo de silêncio decidido: a fala ignorada não reaparece em outro pedido.
- Recibo desconhecido: fallback disponível, predecessor atrasado cancelado antes do modelo.
- Validação atômica de sessão/conversa, turno legado/pending e identificadores duplicados; cancelamento somente dos segmentos incompletos e estado terminal preservado.

A suíte emprega banco EF em memória e modelos de teste, sem emitir ações externas ou áudio. A homologação física nos Nodes continua separada; as chamadas de cancelamento remoto dependem do serviço de voz disponível e dos limites já configurados para ele.