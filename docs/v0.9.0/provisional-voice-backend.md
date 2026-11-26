# Controle provisório de interrupções no backend

Primeira etapa interna da [issue #32](https://github.com/phcastello/Aegis/issues/32).
Nenhum cliente ativa a opção nesta etapa. O contrato depende dos players com pausa reversível
e da ligação do fluxo nativo descrita em [recuperação de interrupções](barge-in-recovery.md).

## Contrato experimental

Os endpoints existentes `POST /api/chat/messages` e `POST /api/chat/messages/stream`
aceitam o booleano opcional `provisionalInterruption`. Ausente ou falso, preservam o
comportamento da v0.8.0. Quando verdadeiro, exigem:

- `origin: "hands_free"`, `allowSilentIgnore: true`, `voiceSegments: true`;
- `turnId` novo e diferente do predecessor, `conversationId` e `voiceSessionId` válidos;
- uma de duas referências: `interruptedGeneration: { turnId }` para geração sem áudio ouvido,
  ou `interruptedPlayback` no formato existente, com turno, mensagem, segmento, sequência e
  frames efetivamente ouvidos;
- um recibo conhecido do predecessor na mesma sessão e conversa, com mensagem de usuário
  já persistida. A reprodução também pode referir-se ao último turno completado ainda retido
  pelo registro. A posição de reprodução precisa corresponder aos segmentos persistidos.

O registro cria uma reserva cancelável sem substituir o turno anterior. O mesmo modelo
conversacional avalia a fala nova separadamente do pedido anterior e do cursor, numa chamada
exclusiva de decisão. Essa chamada oferece apenas `hands_free_ignore`,
`hands_free_accept_interruption` e `hands_free_end_speech`. Ela não oferece
pesquisa web ou ferramentas de integração, nem produz fala. É uma rodada adicional
apenas para o caminho experimental de interrupção; o fluxo ordinário mantém suas rodadas.

- `hands_free_ignore`: libera a reserva, sem persistir uma mensagem do candidato, sem
  extração de memória, sem cancelar o predecessor. HTTP síncrono 204 ou evento NDJSON
  `ignored`.
- `hands_free_accept_interruption`: confirma a posse e a validade da reserva no mesmo
  bloqueio que cancela o predecessor. Espera settlement, carrega o histórico reconciliado
  e revalida o cursor. Depois segue os eventos e ferramentas existentes; um turno novo
  aceito na mesma conversa invalida candidatos obsoletos. A geração e as posições do
  predecessor já completado permanecem registradas.
- `hands_free_end_speech`: decisão reservada ao futuro mecanismo de encerrar somente
  a locução. Nesta etapa retorna erro técnico sem cancelar o predecessor. Não deve ser
  tratado pelo cliente como aceite nem como evento de encerramento.

O cliente participante teria de preservar o produtor, o cursor e os buffers do predecessor
durante toda a decisão, inclusive em erro técnico. Ainda não existe essa integração.

## Limites desta etapa

O contador de reservas e a retenção usam o registro existente. O recibo e as relações
da conversa só autorizam alterações enquanto a reserva continua válida.
A decisão não cria registros de modelo para candidatos ignorados, assim como no fluxo
anterior. As métricas podem medir a fase `chat.provisional_interruption`.

Falta tratar a janela anterior à primeira persistência do predecessor e o caso em
que a captura precede seu POST. Esses casos são rejeitados no caminho novo para evitar
a criação de outra identidade provisória ou um cancelamento não autorizado. Também
faltam a disposição de encerramento de locução, seu efeito sobre TTS e entrega
dos resultados de pesquisa, a integração dos controladores nativos, ensaios em hardware
e avaliação semântica real. Uma falha depois da decisão aceita e do cancelamento do
predecessor não pode prometer recuperação acústica nesta etapa.

Os testes de contrato cobrem descarte durante geração, cursor inválido, reprodução em
turnos ativos e completos, aceitação com reconstrução do contexto, cancelamento
do candidato, prevenção de commits repetidos e a preservação dos caminhos legados.
Não fechar a issue #32 antes da validação completa descrita no plano.
