# Pesquisa web em segundo plano

A ação explícita de pesquisa cria um trabalho persistente no backend ligado à conversa e à mensagem do usuário. A aceitação do trabalho libera o chat e permite novos turnos. Fechar o aplicativo, abandonar a conexão ou iniciar outro turno não cancela a pesquisa. Clientes antigos podem continuar usando a pesquisa durante o turno comum.

Um trabalhador no backend acompanha cada pesquisa, sem executar ferramentas locais de memória, calendário, e-mail ou lembretes. Ele reutiliza a pesquisa hospedada, contagem de uso, auditoria e citações do fluxo normal. A resposta concluída é salva como mensagem da conversa. A sincronização existente atualiza os clientes conectados, e o resultado fica no histórico ao reconectar.

A API expõe o estado atual, a fase observada e até 128 fontes. Os contadores são cumulativos por URL e refletem observações do provedor. A leitura de trabalhos recentes devolve até 20 resultados, com os ativos primeiro. O andamento não estima percentuais, tempo ou custo.

Pergunte “como está a pesquisa?” no chat ou por voz. A Aegis consulta o estado salvo e informa o que sabe até aquele momento. A consulta cobre pesquisa, Python e vídeo e preserva o trabalho em andamento. Uma nova sessão hands-free pode consultar trabalhos recentes de outra conversa, usando somente tipo, fase, horário e quantidade de fontes para identificá-los. O anúncio inicial e a leitura da resposta pronta continuam sendo as falas automáticas.
[Respostas progressivas no Alexa Skills Kit](https://developer.amazon.com/en-US/docs/alexa/custom-skills/send-the-user-a-progressive-response.html)

O trabalho usa a revisão das instruções personalizadas que estava em vigor no turno que o iniciou. A revisão e o texto usados ficam protegidos junto ao trabalho. Mudanças nas configurações valem para os pedidos seguintes.
[Proteção de dados no ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/introduction)

O cancelamento do trabalho é explícito e persistente. O trabalhador interrompe sua requisição ao detectar o cancelamento. Ações que o provedor já executou podem continuar faturadas. Caso a execução seja interrompida por reinício do backend, um trabalho ainda na fila continua disponível. Um trabalho cuja execução expirou fica com falha e pode ser solicitado novamente pelo usuário.

## Contrato HTTP

- `POST /api/chat/research-jobs` aceita `jobId`, `conversationId` opcional e `content`, retornando HTTP 202 com o estado. Repetir o mesmo pedido com o mesmo `jobId` recupera o trabalho existente.
- `GET /api/chat/research-jobs/{jobId}` consulta o trabalho.
- `GET /api/chat/research-jobs?conversationId=...` consulta os trabalhos recentes da conversa.
- `DELETE /api/chat/research-jobs/{jobId}` cancela um trabalho ainda ativo e devolve seu estado atual.
