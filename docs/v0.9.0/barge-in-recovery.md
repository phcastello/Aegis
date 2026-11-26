# Recuperação de interrupções no hands-free

Escopo obrigatório da v0.9.0, relacionado à [issue #32](https://github.com/phcastello/Aegis/issues/32). Os relatos incluem uma fala destinada a terceiros e ruído ou som não verbal durante a resposta. Nos dois casos, o usuário pode perder o restante da fala. O objetivo é manter o corte acústico rápido, recuperar a resposta quando não houve uma interrupção intencional e interpretar o encerramento da locução pela intenção da fala.

## Causa e evidência

Na v0.8.0, o disparo acústico confirma que há um candidato a interrupção. O cliente já cancela a geração anterior antes de conhecer a transcrição final ou seu destinatário.

- Android: `NodeHandsFree.kt`, `startProcessingCapture` e `startResponseCapture`. Ambos chamam `sink.abort()` e `owner.requestCancellation()` no aceite acústico. O `run` encerra a sessão quando o turno seguinte retorna `ignored` ou vazio. A recuperação atual de captura vazia reabre o microfone com a resposta anterior já perdida. `NodeHandsFreeBackend.executeTurn` já evita o modelo quando a transcrição, depois da remoção do wake word, é vazia.
- Windows: `windows_hands_free.rs`, `monitor_turn` cancela `turn_cancel` ao detectar um prefixo acústico. `cycle` reúne o relatório do turno cancelado com o áudio novo em `prepare_handoff` e encerra ao receber `ignored`. `windows_voice_backend.rs`, `run_turn` já retorna sem chamar o modelo quando o STT é vazio.
- Backend: `ActiveTurnRegistry.TrackPendingTurn` permite reservar candidatos sem cancelar o turno atual. `RegisterAndGetSuperseded` substitui o predecessor na aceitação. Contudo, `ChatService.PrepareTurnAsync` cancela o predecessor através de `CancelVoiceGenerationAsync` antes de classificar quando recebe `interruptedGeneration`. `VoicePlaybackContext` já conhece a posição realmente ouvida e instrui o modelo a preservar esse contexto.
- A observação da prévia .44 em `docs/v0.8.0/hands-free-realtime-stt.md` registrou vibração durante processamento, STT em lote e fallback após uma resposta do provedor marcada como falha. A salvaguarda da versão atual separa ausência de palavras e falha técnica. Isso reduz um tipo de transcrição alucinada, mas não recupera reprodução perdida por cancelamento prematuro.

As conclusões foram verificadas no código da v0.8.0, na main posterior à manutenção e no desenvolvimento de pesquisa web. Os pontos de transporte deverão ser adaptados à versão efetivamente integrada dessa pesquisa, sem substituir seus eventos de progresso.

## Referências e decisão arquitetural

- O [LiveKit](https://docs.livekit.io/agents/logic/turns/#false-interruptions) implementa retomada de fala depois de uma interrupção que não produz transcrição. Sua janela padrão de dois segundos é uma referência de temporização, sujeita à latência do STT da Aegis. Sua detecção adaptativa também diferencia sinais de acompanhamento de uma interrupção real. Na Aegis, uma expressão de gratidão que comunica encerramento deve terminar a locução, mesmo se soar como acompanhamento.
- A [Apple](https://machinelearning.apple.com/research/device-directed) encontrou redução dos alarmes falsos na detecção de falas dirigidas ao assistente ao combinar contexto anterior e incerteza da transcrição em conversas de seguimento. A [Amazon](https://www.amazon.science/publications/device-directed-utterance-detection) também encontrou benefício ao combinar sinais acústicos e textuais. A Aegis já dispõe dessas etapas em seu detector, STT e modelo principal. Reutilizá-las evita um classificador novo para todas as falas.
- A [OpenAI](https://developers.openai.com/api/docs/guides/realtime-conversations#interruption-and-truncation) documenta a importância de indicar quanto áudio o usuário ouviu numa interrupção. A Aegis continuará usando os IDs e posições dos seus segmentos, preservando a íntegra do texto da resposta no chat quando produzida.
- O [AudioTrack](https://developer.android.com/reference/android/media/AudioTrack#pause()) preserva os dados pendentes ao pausar e tocar novamente. No Windows, [`waveOutPause`](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutpause) e [`waveOutRestart`](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutrestart) preservam a posição. Essas capacidades permitem recuperar a fala no mesmo dispositivo sem uma nova síntese.

O resultado depende de três componentes coordenados: saída de áudio com pausa reversível, controlador do turno com captura provisória e autorização da substituição no backend depois de reconhecer a intenção. Ajustar apenas a sensibilidade do detector ou melhorar o prompt pode reduzir a incidência, mas o cancelamento prematuro continuaria perdendo respostas. Regerar a resposta depois do descarte também arriscaria repetir conteúdo ouvido, gastar recursos e repetir efeitos de ferramentas. A implementação deve preservar o turno e a saída original.

## Contrato de comportamento

| Situação depois do disparo acústico | Destino da resposta anterior | Destino da nova fala |
| --- | --- | --- |
| Captura vazia, ruído, silêncio ou STT que retorna somente ausência de palavras | Retomar a saída, ou continuar o processamento, no mesmo turno | Descartar sem chamar modelo |
| Fragmento sem intenção e sem relação plausível, fala ao fundo ou destinada claramente a terceiro | Retomar o mesmo PCM do ponto efetivamente ouvido | O modelo principal decide ignorar, sem persistir uma mensagem nem executar ferramentas |
| Sinal de acompanhamento que indica que pode prosseguir | Retomar | Descartar como interrupção não conclusiva |
| A fala dirigida expressa que já ouviu o suficiente | Encerrar a reprodução e esvaziar o áudio pendente. Resultados de um trabalho que já começou continuam chegando ao chat | Encerrar em silêncio, sem exigir frase fixa |
| Nova pergunta, correção, pedido específico de mudança | Suspender e então substituir a locução conforme o contrato de interrupção aceito | Atender usando o contexto e a posição realmente ouvida |
| Cancelamento explícito do trabalho | Aplicar a política de cancelamento do trabalho e da locução | Resultado terminal sem reexecutar ações |

Exemplos para calibrar, sem converter em lista de expressões: “tá bom, valeu” pode encerrar. “Tá bom, mas e o preço?” é pedido de continuação/modificação. “Aham” pode ser um sinal de acompanhamento ou uma resposta pertinente à pergunta anterior. A relação temática ajuda a interpretação, mas não basta para definir o destinatário. A decisão depende da fala completa, do trecho anterior e de seu papel na conversa.

## Etapas de implementação

### 1. Separar pausa de cancelamento

Estender a fronteira do player para oferecer uma operação de pausa reversível e sua retomada, além do encerramento definitivo já existente. A propriedade do dispositivo, dos buffers e da posição continua no turno que iniciou o áudio. A captura nova não cria outro player enquanto a decisão é provisória.

- Android: `NodeHandsFreePcmStream.kt`, `NodeHandsFreePlayback.kt` e seus testes. A chamada `AudioTrack.pause()` não usa `flush` nem invalida o sink. Retomar com `play()` no mesmo dispositivo. Se uma escrita bloqueante for interrompida pela pausa, contabilizar corretamente a transferência parcial e esperar a resolução sem perder bytes. Ajustar a espera pelo marcador final e o prazo de drain para uma pausa autorizada. A API pode devolver uma escrita parcial quando é pausada por outra thread. Verificar as transições com device fake e com AudioTrack físico.
- Windows: `windows_voice_audio.rs`, `windows_voice_backend.rs` e sua fronteira `TurnAudio`. Encaminhar pausa e retomada ao mesmo handle sob controle de propriedade. Preservar os quatro buffers limitados e o cursor ouvido. As esperas de `reclaim` e `finish` precisam suportar a pausa, com possibilidade de cancelamento e limite de tempo. Atualizar a referência acústica e a onda apresentada conforme o estado de reprodução. Evitar acesso concorrente a um handle depois do fechamento. Validar em Windows real.
- Manter os limites existentes de PCM, segmentos, concorrência e prazo por turno. A janela provisória tem limite e evita acumular áudio ilimitado. Medir prazo sem progresso separadamente do tempo decorrido enquanto o candidato é resolvido. Reaproveitar os buffers existentes e o backpressure. Não interromper indevidamente o fluxo de progresso e texto de uma pesquisa longa, nem deixar que a pausa de áudio obrigue a geração do texto a recomeçar.

### 2. Capturar e decidir sem abandonar o predecessor

No Android adaptar `startProcessingCapture`, `startResponseCapture`, `ResponseBargeIn`, `run`, `NodeHandsFreeBargeInCapture` e as fronteiras do `NodeHandsFreeBackend`. No Windows adaptar `monitor_turn`, `cycle`, `prepare_handoff` e `windows_voice_backend.rs`, reutilizando o STT. Compartilhar dentro de cada cliente a decisão de uso do resultado provisório entre as fases de processamento e reprodução.

1. Ao detectar a fala, pausar rapidamente se já há áudio. Se ainda está processando, impedir que o player comece até concluir a decisão, mantendo o processamento anterior. Preservar o prefixo acústico capturado. Manter o ID e a identidade da sessão e o cursor que representa o que foi realmente ouvido.
2. Capturar a nova fala no candidato com um ID próprio. A troca de rota do microfone do Android entre processamento e reprodução deve aguardar a decisão da captura pendente e nunca soltar o gravador por uma transição tardia. O Windows mantém o único dono do microfone no worker original.
3. Finalizar STT pelo caminho existente. Captura/resultado realmente vazio encerra somente o candidato e libera o predecessor, sem enviar uma chamada de chat. Não criar mensagem, outra conversa, extração de memória ou resposta de voz. Não encaminhar ausência de fala a um fallback de transcrição que pode inventar palavras.
4. Quando existir texto candidato, usar a aceitação semântica do modelo principal na etapa seguinte. O predecessor e sua saída pausada permanecem recuperáveis. Com o cursor congelado, nem o TTS adiantado nem o progresso do produtor mudam o que foi ouvido.
5. Se o modelo der `ignored`, retomar a execução ou reprodução original na mesma posição. A captura de um candidato rejeitado jamais é composta ao pedido anterior. Em falha técnica ou prazo excedido durante a decisão provisória, preservar o pedido anterior e evitar efeitos do candidato não aceito. Uma mensagem curta claramente dirigida e recorrente não deve ser descartada pelo tamanho ou tempo. Começar com os limites já existentes de captura/STT, medir latência até retomada, e avaliar a janela de referência de dois segundos de ausência de transcrição.
6. Depois de um aceite genuíno, transferir propriedade para o novo turno e finalizar a locução precedente uma só vez. Depois do encerramento semântico, liberar o áudio pausado e bloquear novas reproduções daquele turno. O usuário retoma a wake word normal. O trabalho anterior eventualmente ainda em andamento pode publicar resultado textual. Um cancelamento explícito do trabalho é uma decisão distinta.

### 3. Corrigir a fronteira de aceitação do backend

O mecanismo existente de candidato provisório é a base. Trabalhar em `ChatService`, `ActiveTurnRegistry`, `HandsFreeDirection`, `HandsFreeReply`, `VoicePlaybackContext` e `AegisToolLoop`; verificar o contrato de `SendMessageRequest`, dos eventos de streaming e do endpoint síncrono. A mudança requer coordenação com os novos eventos de pesquisa.

- Identificar explicitamente a interrupção provisória no contrato. Validar vínculo a sessão, conversa, predecessor e candidato antes de acessar contexto ou alterar estado. Nunca aceitar a posição fornecida pelo cliente como autoridade para mudar outro turno; verificar os segmentos na fonte já usada.
- Separar leitura de contexto do cancelamento. Durante o julgamento, não executar `BeginVoiceGenerationHandoff` destrutivo. Preparar o contexto a partir de um snapshot do pedido anterior, recibo e posição de áudio pertinentes. Se a primeira fala ainda estiver em transcrição, preservar seu áudio até recuperar o pedido. A lógica atual de juntar os dois áudios é necessária apenas quando a nova fala for genuína e o pedido ainda não estiver transcrito.
- Para o predecessor ainda não persistido, inclusive a primeira pergunta de uma nova conversa, manter a identidade provisória existente. Julgar o candidato pelo texto da fala nova e o contexto anterior separadamente, evitando que o pedido anterior mesclado seja confundido com um novo pedido dirigido. Aguardar o settlement somente depois da confirmação da substituição, reconciliar o estado persistido e respeitar as garantias atuais de ausência de conversa fantasma. Se a decisão preceder a chegada HTTP do primeiro turno, preservar a semântica existente de cancelamento de POST atrasado no caso aceito. Uma reserva cancelada explicitamente não pode reabrir após aceitar resultado tardio.
- O cliente envia apenas candidatos com transcrição a `POST /api/voice/interruptions/classify`, com a credencial do Node e a referência à sessão e à resposta anterior. O modelo principal devolve uma decisão de retomar, encerrar a locução ou aceitar o pedido novo. Essa chamada oferece somente a ferramenta de decisão. Um aceite gera um recibo de uso único vinculado à fala e ao predecessor, consumido pelo turno que vai responder, sem repetir a decisão na rodada de chat. Uma fala descartada ou um encerramento não cria mensagem nem extração de memória. Clientes antigos conservam seu protocolo.
- Na aceitação real, autorizar e sequenciar cancelamento, settlement e registro do sucessor. Revalidar o snapshot/cursor para não perder o trecho final que o predecessor persistiu enquanto a captura estava em andamento. O modelo e as ferramentas recebem o histórico reconciliado. Não repetir chamadas de ferramentas já executadas. Se o predecessor já terminou, preservar seu resultado e resolver a disposição sem cancelar outra geração.
- Interrupção de locução e cancelamento do trabalho passam por estados separados. O backend deve concluir e persistir o texto e as fontes de um trabalho autorizado mesmo com reprodução encerrada. O cliente pode dispensar o TTS de segmentos subsequentes e drenar os eventos necessários ao histórico. Verificar que eliminar a demanda de áudio não prende a geração em uma fila de TTS. Cancelamento explícito e mudança intencional de pedido continuam com as garantias de identidade, estado e cancelamento da tarefa aplicáveis à v0.9.0.

### 4. Integrar e validar os estados

Os clientes precisam suportar os três resultados do candidato: descartar e retomar, encerrar só fala, aceitar substituição. Usar IDs dos turnos, sessões e geração de player para serializar transições; handlers de um candidato antigo não podem retomar a saída de outro turno. Uma troca normal de identidade, perda de audio focus, botão de cancelamento ou encerramento de aplicativo revoga recursos. Depois de uma retomada bem-sucedida, o sistema deve aceitar uma segunda interrupção. Uma falha de dispositivo ou perda real de rota deve liberar recursos e preservar o resultado textual caso disponível. Não declarar retomada exata onde o dispositivo não permitiu recuperar o cursor.

Registrar duração da pausa, resultado de STT (vazio, texto, erro), disposição (retomada, encerramento, substituição), segmento e quantidade de frames efetivamente ouvidos e motivo de cancelamento. Vincular os registros ao turno/sessão, limitar retenção e evitar texto bruto ou áudio duplicados em logs de diagnóstico. A coleta experimental de wake word segue sua configuração própria.

## Matriz mínima de aceitação

Os casos de propriedade e efeitos devem passar em testes determinísticos nas duas plataformas e backend. A acústica real requer bancada Android e Windows com o TTS instalado.

| Cenário | Resultado que precisa ser demonstrado |
| --- | --- |
| Ruído/vibração/tosse durante reprodução e captura sem fala | Mesmo turno e mesmo áudio retomados, sem perda de frames além do cursor físico, sem novo pedido nem nova chamada de modelo |
| STT retorna string vazia/whitespace/wake word apenas | Sem chat novo, sem fallback indevido, retomada |
| STT devolve fragmento implausível ou fala a terceiro | Modelo decide ignorar, resposta anterior retomada, zero ações e mensagens do candidato |
| Fala de mídia plausível, mas claramente destinada a outra pessoa | Verificar destinatário pelo contexto, sem responder ao conteúdo do terceiro |
| Conversa contínua ao fundo e comando em primeiro plano mais alto | Aceitar o comando mesmo durante a sobreposição e finalizar sua captura quando a voz em primeiro plano terminar, com o fundo ainda presente. Durante a leitura, outra voz não deve descartá-la |
| “Tá bom, valeu”, “já deu”, “beleza, era isso” | Encerramento silencioso conforme intenção, sem reiniciar a fala ao chegar novo evento do predecessor |
| “Valeu, mas me diz o frete”, correção da resposta, mudança de assunto | Pedido aceito no contexto anterior, preservando informação sobre o trecho ouvido |
| “Sim” ou “aham” durante pergunta/explicação | Decisão sensível ao contexto, fala curta preservada quando é resposta ou pedido legítimo |
| Interrupção na transcrição inicial ou antes de primeira mensagem persistida | Falsa interrupção mantém pedido original; interrupção genuína preserva pedido anterior e correção sem conversa fantasma |
| Ruído durante pesquisa/cálculo antes de primeira fala | Trabalho anterior conclui sem segunda execução ou perda; locução normal após a decisão |
| Encerramento de locução durante pesquisa | Fala permanece encerrada e resultado/texto/fontes chegam ao chat |
| Fala aceita antes/depois de compromisso do primeiro segmento | Cursor real e contexto reconciliados; predecessor não persiste efeitos depois do settlement indevidamente |
| Duas interrupções consecutivas, pausa no último buffer, resposta que termina durante decisão | Sem deadlock, perda de fila, reprodução duplicada ou retomada tardia de sessão antiga |
| Timeout/falha de STT ou modelo, gravação interrompida e troca de rota | Sem falso aceite e com preservação do predecessor quando a saída ainda for recuperável; limpar slots e recursos |
| Cancelamento explícito durante pausa, app encerrado ou troca de identidade | Recursos liberados, callbacks anteriores ignorados, nenhuma retomada após revogação |
| Cliente legado e chat/voz manual | Protocolos anteriores continuam aceitos, sem novos efeitos em fluxos não participantes |

Suítes existentes a ampliar: backend `HandsFreeVoiceTests` (wake-only, ignored, primeiro aceite, thinking handoff e settlement), `ActiveTurnRegistryTests`, `RealtimeTranscriptionTests` e testes de segmentos; Android `NodeHandsFreeBackendTest`, `NodeHandsFreeBargeInCaptureTest`, `NodeHandsFreePcmStreamTest`, `NodeHandsFreePlaybackProgressTest`, `NodeHandsFreeContinuityTest` e testes de rota de áudio; Windows `windows_voice_backend_tests.rs`, `windows_hands_free.rs` e `windows_voice_audio.rs`. As fixtures de PCM com conteúdo identificável devem comprovar o cursor e os bytes ainda pendentes. Usar respostas sintéticas e provedores falsos que exponham a contagem de geração, TTS e ações. Os testes caricaturais de decisão semântica não substituem ensaio com o Terra configurado e amostras reais. Comparar latência de corte, tempo até retomada e taxa de falhas de barge-in com a v0.8.0, inclusive com áudio de pesquisa no meio do streaming e áudio final.

## Fronteira de entrega

Uma pequena correção defensiva de STT vazio é possível dentro da nova pausa reversível. Sozinha, ela deixa descobertas as falas alheias, os fragmentos sintetizados pelo STT e as interrupções da geração. A menor entrega completa deve integrar a pausa, o controlador provisório e o backend antes de publicar a correção como resolvida. O trabalho pode ter commits internos por dependência, com testes de contrato antes dos consumidores; ativação para uso real somente depois da composição e dos casos de aceite. Manter a issue #32 aberta até haver evidência dos dois defeitos corrigidos nas plataformas previstas. Melhorias posteriores de VAD, modelos acústicos, cancelamento de eco e detecção de backchannels podem diminuir pausas falsas, mas o requisito inicial é preservar resposta e contexto quando uma pausa falsa ocorrer.
