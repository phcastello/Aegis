# Streaming do hands-free no Windows

## Diagnóstico

O modo manual do desktop apresenta o texto à medida que recebe os deltas da resposta. O modo hands-free recebe unidades de fala finais, persistidas no servidor antes de serem autorizadas para síntese. Até a prévia .42, o worker nativo validava essas unidades, descartava seu texto e notificava a interface local apenas quando descobria um novo ID de conversa. A apresentação dependia, portanto, de recarregamentos de snapshots após a notificação de alterações via WebSocket. Isso explicava a aparição brusca de grandes trechos no chat, mesmo quando os segmentos e a reprodução já eram incrementais.

O fluxo de áudio não espera pela resposta completa. O servidor envia unidades finais em NDJSON; duas unidades de síntese, no máximo, ficam em andamento, e o player Windows consome o PCM em ordem conforme chega, sem esperar pelo fim da requisição. O servidor já publica invalidações de conversa após os commits, preservando a sincronização com outros nós.

## Evento de apresentação local

O worker Windows emite no webview principal `aegis-windows-voice-chat-stream`, sem abrir uma janela, navegar a cada trecho ou mudar o protocolo de rede:

| Tipo | Momento |
|---|---|
| `conversation` | Primeiro marcador válido de aceitação; também emitido para uma conversa existente. Em fluxos compatíveis sem marcador explícito, o primeiro segmento validado confirma a aceitação. |
| `final_segment` | Unidade final validada, com texto e identidade persistidos. |
| `done` | Fim válido do fluxo de geração, sem esperar o término da fala. |
| `ignored` | Transcrição vazia ou turno descartado; não cria uma conversa. |
| `interrupted` / `error` | Limpeza restrita ao turno ao interromper ou falhar. Pode seguir `done` se a reprodução tiver sido interrompida ou falhado após o final da geração. |

Todos os eventos contêm `type`, `turnId`, `conversationId` e `assistantMessageId` (IDs de conversa/mensagem nulos se ainda não aceitos ou conhecidos). `final_segment` contém adicionalmente `segmentId`, `sequence` (começa em zero) e `content`. Não se apresentam deltas especulativos de texto, argumentos de ferramentas ou trechos malformados. A navegação para a conversa permanece em seu evento dedicado e só ocorre quando o ID muda. O cancelamento de uma resposta e a transição para nova captura compartilham a trava que protege os eventos locais; somente a limpeza do turno anterior pode segui-los. A sincronização com o snapshot persistido continua necessária para recuperação e metadados.

## Evidência de bancada e limites

O teste `publishes_committed_text_and_plays_first_pcm_before_either_http_stream_finishes` mantém abertas as respostas HTTP de geração e síntese depois de emitir o primeiro segmento e as primeiras amostras PCM. O servidor só libera o restante da fala e a segunda unidade depois que o player simulado confirma a primeira escrita real de amostras. O teste confirma a ordem dos eventos locais e dos quadros PCM, a ausência de deltas não comprometidos e a ausência de espera pelo fim de qualquer resposta HTTP. Os testes de regressão também conferem limpeza sem conversa fantasma, interrupção antes/depois da aceitação, áudio descartado e posição efetivamente ouvida.

A latência do primeiro áudio ainda depende do modelo concluir uma unidade final de fala, do persistimento e do primeiro PCM da síntese. Uma frase ou parágrafo longo fornecido como unidade pelo modelo pode demorar mais que uma unidade curta. A reprodução pode sofrer com a rede ou a síntese mais lenta que a fala. A bancada de transporte não mede lacunas acústicas, o tempo real do TTS ou a fluidez visual no equipamento do proprietário. Não se alteraram o prompt, os limites do modelo, a síntese, o AEC ou a captura que já haviam sido aprovados.


## Apresentação de texto no chat compartilhado

A migração PWA → cliente nativo (`d29a2f5`) preservou a lógica existente:
renderização de Markdown a partir do prefixo até a última palavra completa,
reação a cada delta do stream e animação de todas as palavras novas por 600 ms.
O tamanho e o momento dos lotes de rede, o agrupamento de atualizações pelo Vue
e a re-renderização inteira do Markdown podiam produzir saltos ou muitos
fragmentos no WebView. Por isso o mecanismo foi refinado nos dois hosts e
não apenas no modo voz.

`StreamTextReveal` apresenta exclusivamente prefixos do texto de fato
recebido, em passos curtos a cada 32 ms, com velocidade adaptativa e cauda
limitada a aproximadamente 1,8 s após a última chegada. A primeira atualização
é agendada, permitindo coalescer inclusive centenas de pequenos deltas NDJSON
processados no mesmo lote sem transformar a resposta em um único frame. Não
há atraso de rede, TTS ou conclusão do turno. O Markdown continua sanitizado;
a animação é um fade discreto de 180 ms (opacidade 0,68 → 1), em no máximo
seis grupos recentes, sem criar elemento para cada letra/palavra. Fragmentos
expirados deixam de receber wrappers e os ativos são removidos ao término.
Movimento reduzido, janela oculta e conversa em segundo plano mostram o texto
real imediatamente. Erros/interrupções preservam o conteúdo recebido.

O stream local Windows aplica as mesmas regras a `final_segment`, concatenando
unidades de fala com um espaço, como o servidor. O acumulador valida turno,
conversa, mensagem e ordem, deduplica reenvios idênticos e não inventa texto
se houver lacunas. Durante a revelação o marcador `streaming` impede que uma
atualização completa do histórico substitua abruptamente o prefixo. Ao término,
a sincronização existente reconcilia o conteúdo e os metadados persistidos.
Não se anima histórico carregado nem se navega por causa de cada segmento;
turnos ignorados não produzem mensagem. Uma resposta recebida antes da animação
não bloqueia novo pedido. Preservam-se navegação, rascunhos, rolagem manual,
continuidade, interrupção do pensamento e o ponteiro de áudio ouvido.

A validação Chromium com HTTP e IPC simulados injeta dois segmentos em rajada,
uma duplicata idêntica, snapshot completo concorrente, final antes do fim do
áudio, interrupção, descarte, navegação, movimento reduzido e mais de 700 deltas
Unicode pequenos em um único lote do chat digitado. Testes de Markdown cobrem
prefixos, sanitização, links, layout e limite/limpeza dos fragmentos. Os testes
unitários conferem prefixos Unicode e igualdade do texto final com o recebido.
Trata-se de evidência em navegador e Rust nativo, não de homologação física
no WebView/PC do proprietário.
