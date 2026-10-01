# Aegis v0.6.1 — “Yeah, I know.”

Rodada de refinamento em 01/10/2026, baseada em `origin/main` (`1397934`), com a v0.6.0 aceita e mergeada. Branch: `fix/v0.6.1-assistant-refinement`, publicada com upstream antes das alterações. Nenhum merge na main. O arquivo local não rastreado `conversas_7f876a2c_a_9ec08941.zip` foi preservado e não integra a entrega.

## Problema e diferença de comportamento

A v0.6.0 combatia tautologia com `DeclarativeResponseGuard`. O pós-processamento podia substituir respostas conversacionais por “Certo.”/“Entendi.”. No streaming, `ShouldReview()` também retinha todos os tokens de declarações até `Normalize()`. A identity favorecia confirmação mínima sempre que não houvesse comentário “não óbvio”. Essa combinação prejudicava presença e latência percebida.

Na v0.6.1, a classe e seus testes específicos foram removidos. ChatService persiste o conteúdo real do modelo e transmite cada chunk imediatamente, mantendo o acumulador apenas para persistência final. Não há novo filtro lexical, regex, substituição de resposta ou chamada de personalidade. A identity pede reação curta e pertinente a atualizações ricas, evita explicar a frase do usuário e permite confirmação mínima somente quando apropriada. Consequências úteis podem ser óbvias. História e memória pertinente podem situar a reação sem anunciar recordação, expor provenance, forçar conselho ou sarcasmo. Retrieved memory continua dado não confiável, nunca instrução ou autorização.

## Busca casual e continuidade

`MemorySearchTool` deixou de depender de `EndsWith('?')`. Conteúdo original não vazio de até 200 caracteres é a primeira âncora, inclusive mensagens sem pontuação e imperativos casuais. A query do modelo só é tentada depois de zero memórias **e** zero paths, se diferente. Mensagens acima do tamanho suportado usam a query estruturada, sem truncar o original. `asOf`, relações, validação canônica, referências observadas, Memory Activity e supressão de fan-out permanecem no fluxo existente.

`IAegisDbContext.GetConversationMessagesAsync`/`AegisDbContext` carregam todas as mensagens de conversa não deletada, ordenadas por `CreatedAt` e `Id`, sem `Take`. Os dois caminhos do ChatService retiram a mensagem atual pelo ID antes de entregarem o histórico ao PromptBuilder, que a acrescenta uma vez. `RecentHistoryLimit` foi removido. Estrutura de prompt/cache e redaction foram preservadas. Limites do extractor, Graph, retrieval, tools, títulos e listagem de conversas permanecem especializados e intactos. Sem migration, compaction, resumo ou fallback de truncamento.

## Integrações recuperáveis

Nenhuma tool é escondida por conexão. Calendar já distinguia conexão, scopes Events/List, API desativada, acesso ao recurso, argumentos inválidos e indisponibilidade temporária; seus códigos estáveis foram preservados. Reminder já impede criação quando Web Push não está configurado ou não existe assinatura ativa, com erros distintos e sem promessa de entrega.

A lacuna de Gmail era a perda de significado de falhas de scope/provider no fallback genérico do tool loop. GmailService agora produz erros sanitizados de autenticação, scope, API desativada, acesso negado, recurso ausente, argumentos inválidos e falha transitória/rate limit. O loop traduz exceções de conexão, scope, provider, timeout e rede de Gmail em resultados operacionais para a continuação do modelo; cancelamento do turno continua sendo propagado. Não altera confirmação de mutações nem o limite de quatro rounds. A renovação compartilhada de token já preserva a conexão em falha temporária; sua regressão permanece na suíte.

| Estado | Recuperação |
| --- | --- |
| Tool desconhecida | Informar ausência dessa capacidade; nenhuma poda por autenticação |
| `email_not_connected`, `calendar_not_connected` | Link da conexão Google compartilhada |
| Autenticação recusada | Reautorizar pela tool de link correspondente |
| `email_scope_missing`, `calendar_scope_missing`, `calendar_list_scope_missing` | Reautorização com scopes necessários |
| `email_api_disabled`, `calendar_api_disabled` | Ativar API no projeto Google Cloud; reconectar não resolve |
| Acesso negado a recurso | Rever permissões/recurso, sem presumir scope ausente |
| `notifications_not_configured` | Configurar envio no servidor |
| `notifications_unavailable` | Habilitar notificações na interface e pedir novamente |
| Indisponibilidade temporária/timeout/rate limit | Tentar mais tarde, sem reconectar automaticamente |
| Argumentos inválidos | Corrigir dados ou esclarecer antes de continuar |

Identity e descriptions de links permitem gerar autorização na mesma interação de um pedido direto bloqueado. Gerar link não altera Gmail/Calendar. A execução original não é anunciada como concluída e o usuário pode continuar/repetir após autorizar. Falta de duração de reunião é esclarecida, sem presumir uma hora.

## Validação automatizada

SDK local: `/tmp/aegis-dotnet`, EF em `/tmp/aegis-dotnet-tools`. PostgreSQL, Qdrant e Neo4j exclusivos e descartáveis foram usados; as bases/projeções da aplicação não foram alteradas. Todos os testes físicos opt-in foram habilitados.

| Comando/checagem | Resultado |
| --- | --- |
| `dotnet test backend/Aegis.sln` | PASS: 408/408, 0 falhas, 0 skips |
| `dotnet build backend/Aegis.sln --configuration Release` | PASS: 0 erros; 1 aviso xUnit2031 preexistente em CalendarTests:1138 |
| `npm test --prefix frontend/aegis-pwa` | PASS: 47/47, 0 falhas, 0 skips |
| `npm run build --prefix frontend/aegis-pwa` | PASS: build e service worker PWA |
| `docker compose config --quiet` | PASS |
| `git diff --check` | PASS |
| `dotnet ef migrations has-pending-model-changes --project backend/src/Aegis.Infrastructure --startup-project backend/src/Aegis.Api` | PASS: nenhuma mudança de modelo pendente |
| Compilação dos quatro scripts Python de eval | PASS |

Novos testes cobrem histórico de 30 mensagens nos caminhos normal/streaming, primeiro token antes do final do modelo, conteúdo sem normalização, ordem e inclusão única da mensagem atual, conversa deletada, queries com/sem `?`, imperativo casual, anáforas e `asOf`. Há teste PostgreSQL real para equivalência das consultas e fallback, além do caso literal histórico FaZe com fatos alheios e da regressão de Graph hub. Testes de provider Gmail distinguem oito estados de erro mais API desativada e verificam ausência do corpo sensível nas mensagens; testes de tool loop verificam continuação e manutenção das tools no catálogo.

A suíte completa conserva extração automática, recent canonical context, correct/transition, forget suppression, canon PostgreSQL, projeções Qdrant/Neo4j, Activity/diagnostics, recorrência/confirmations Calendar, Gmail, reminders/Web Push, prompt caching, bounded tool loop, voz/cancelamento e frontend/PWA.

## Evals live

Modelo de chat configurado: `gpt-6-luna`, reasoning `medium`; modelo de extração configurado: `gpt-6-luna`, reasoning `low`. Os scripts usam configuração da aplicação, sem trocar modelo para passar. Todos os pedidos OpenAI usam `store=false`; judges usam JSON Schema estrito/Structured Outputs. Não houve mutação externa real de Gmail/Calendar. Evidências completas, incluindo tentativas com falhas, estão em [eval-results-v0.6.1-live.json](eval-results-v0.6.1-live.json).

| Eval | Trials | Resultado final |
| --- | --- | --- |
| Presença conversacional (`AEGIS_MEMORY_DECLARATIVE_LIVE=YES python scripts/eval_memory_declarative.py --trials 3`) | 8 cenários × 3 | PASS: 24/24 |
| Caso literal dos dois monitores, sem contexto | 3 | PASS: 3/3; nenhuma confirmação mínima ou tautologia |
| Dois monitores após conversa sobre alternar código/documentação em uma tela | 3 | PASS: 3/3; reação situada |
| Recuperação (`python scripts/eval_integration_recovery.py --trials 3`) | 5 cenários × 3 | PASS: 15/15 |
| Gmail desconectado | 3 | PASS: 3/3, busca/status bloqueado → link |
| Calendar desconectado | 3 | PASS: 3/3, bloqueio → link |
| Calendar conectado sem scope | 3 | PASS: 3/3, reautorização |
| Reminder sem notificações | 3 | PASS: 3/3, nenhuma promessa de criação/entrega |
| Provider temporariamente indisponível | 3 | PASS: 3/3, nenhuma reconexão |
| Intent Memory, catálogo real e dados fake | 18 casos, 1 trial cada | PASS: 18/18 |
| Extração (`AEGIS_MEMORY_TEST_OPENAI=YES python scripts/eval_memory_extraction.py`) | 28 casos, 1 trial cada | PASS: 28/28 após correção do critério de modalidade |
| Controles negativos do judge de modalidade | 2 | PASS: posse textual sem relação e relação OWNS foram rejeitadas |

Nos cenários Calendar, o pedido inicial literal não informa duração. Quando a Aegis pede esse dado, o fake continua a conversa com “Por uma hora.” antes de avaliar a recuperação. O estado bloqueado só é descoberto pela ferramenta; não é informado como conhecimento prévio ao modelo. O eval não exige inferir duração nem anunciar bloqueio antes de observá-lo. O catálogo é exportado da aplicação e o loop fake respeita o bound de quatro rounds, retornando falhas operacionais e links simulados.

Exemplo real final para a atualização rica: “Boa configuração: o QHD a 180 Hz fica ótimo como tela principal, e o FHD pode deixar navegador, chat ou ferramentas abertas sem tirar o foco.” Isso é evidência, não template ou exigência textual.

## Falhas e correções durante a implementação

- Primeira rodada de presença: **22/24**. Uma resposta explicou o significado de PostgreSQL canônico, ainda tautológica; o prompt passou a proibir preencher resposta com definição dos termos do usuário. Outra falha veio de o judge rejeitar qualquer conselho, incluindo duas frases curtas e pertinentes; o critério foi corrigido para rejeitar conselho **genérico**, conforme o escopo. Não foi removida a rejeição de tautologia ou confirmação mínima em updates ricos.
- Primeira recuperação: **11/15**. Dois cenários Calendar terminavam ao pedir duração e dois cenários Gmail ao pedir qual professor, antes de descobrir o bloqueio; houve também suposições de uma hora nos cenários Calendar. A identity explicita esclarecimento de duração, e o eval agora continua o diálogo antes de avaliar o estado operacional. Os resultados iniciais permanecem publicados.
- Extração complementar inicial: **27/28**, falha `modality`. Três trials diagnósticos preservaram “Pedro está pensando em comprar uma RTX 5090.” com `CONSIDERS_BUYING`, mas o eval antigo exigia literalmente `consider` no texto. O critério passou a avaliar o significado com judge estruturado e mantém reprovação incondicional de OWNS/USES. Controles negativos impedem aceitar posse como consideração. O extractor e a ingestão não foram alterados. Não reclassificamos a execução inicial como PASS.
- Revisão do diff detectou substituição ampla de versão que afetava dependências do lockfile e remoção excessiva no cabeçalho README; ambas foram corrigidas antes da entrega. Apenas versões da aplicação foram atualizadas, mantendo histórico documental e codinome.
- Um novo aviso de estilo xUnit foi corrigido. O aviso restante em CalendarTests já existia na main.

## Limitações e escopo

Evals de modelo são amostras probabilísticas e seus judges também variam; 3 trials não garantem todo diálogo futuro. A presença é controlada por prompt, sem filtro pós-geração, portanto ainda pode haver respostas excessivamente curtas ou tautológicas fora da amostra. O cenário contextual live avalia história de múltiplos turnos; regressões automatizadas cobrem retrieval/Activity/contexto canônico, mas não foi repetido o replay completo 001–022 em quatro APIs descartáveis (**NOT RUN** nesta versão).

Os evals de integração simulam estado operacional e autorização; não fazem login OAuth real ou mutação Google (**NOT RUN**, deliberadamente). A suíte usa fakes HTTP para contratos de provider e serviços reais para persistência/projeções descartáveis. Conversas que excederem a janela do provider não têm estratégia de truncamento nesta versão; a falha precisará ser tratada conscientemente em outra etapa. Full history aumenta payload e custo de conversas longas.

Sem nova integração, capability, arquitetura, migration ou mudança de modelo. Versões runtime, métrica, identity, package/lock, PWA manifest e card foram atualizadas para 0.6.1. Relatórios históricos não foram reescritos.
