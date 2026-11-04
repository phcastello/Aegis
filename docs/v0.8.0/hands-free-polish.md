# Aegis v0.8.0 — I Miss the Peace and Quiet

Rodada de refinamento na branch `feat/v0.8.0-hands-free-polish`, worktree isolada
`/home/phc/Aegis-hands-free-polish`. A base hands-free `c7779ab` foi atualizada
para `6c201ab`, descendente publicado que documenta a `unstable.10`, antes de
integrar as alterações. A coleta privada de ativações e suas correções de ciclo
de vida foram preservadas. A worktree inicial e as demais sessões não foram
alteradas. Não houve merge com `main`, squash ou force push.

Este documento registra somente o polish. Os contratos de voz permanecem em
[pipeline conversacional](conversational-pipeline.md), e a persistência dos WAVs
em [coleta de ativações](activation-dataset.md). A v0.8.0 continua em
desenvolvimento; não há declaração de conclusão ou estabilidade.

## Conversas e sincronização

O backend nativo persistia turnos sem invalidar o estado já carregado pelo
WebView. A interface atualizava o histórico durante abertura/turnos locais, mas
não recebia os turnos executados pelo serviço hands-free. Usar o evento de abrir
conversa corrigiria apenas parte do problema e mudaria indevidamente a navegação.

A sincronização passa por invalidações com identificadores, sem conteúdo de
mensagens. A consulta recupera somente a conversa afetada. A retomada do
aplicativo também reconcilia a cabeça do histórico e a conversa selecionada.
O estado de navegação e os rascunhos continuam pertencendo ao cliente.

O contrato é `aegis-conversation-changed`, com `generation` UUID,
`conversationId` UUID e `revision` crescente dentro da geração. O evento sinaliza
invalidação; não abre conversa. A operação read-only
`node_hands_free({ operation: 'conversationChanges' })` recupera
`{ generation, changes: [{ conversationId, revision }] }` pela ACL Android já
existente. A reflexão JNI correspondente está preservada no R8.

Um journal em memória mantém a revisão mais recente de até 128 destinos. A
consulta não consome eventos, e uma leitura temporariamente indisponível da
identidade oculta os identificadores sem descartá-los. A autoridade é verificada
sob o mesmo monitor da publicação/leitura do journal para impedir um callback
antigo de limpar invalidações de uma identidade nova. Troca da geração invalida
leituras pendentes e provoca reconciliação da cabeça/seleção. Morte do processo
recria o journal; a retomada recupera o estado persistido pelo histórico.

No fluxo segmentado, as notificações seguem `conversation` (usuário persistido),
cada `final_segment` e `done`. O leitor valida conversa, mensagem e sequência
antes de notificar, independentemente do backpressure do player. No fluxo JSON,
notifica depois da resposta persistida validada, inclusive quando o TTS falha
em seguida. Nenhuma notificação é gerada a partir de transcrição vazia.

O cliente agrupa rajadas por 120 ms e faz até três consultas simultâneas, mesmo
com chamadas concorrentes de flush. A fila rejeita snapshots substituídos por
novos eventos, novas revisões locais, navegação ou mudança de geração. Turnos
locais ativos adiam a aplicação até terminar. Mensagens persistidas são mescladas
por ID, preservando objetos/chaves locais e mensagens interrompidas em sua
posição relativa. O stream atual não fornece ID do usuário: sua associação local
usa ordem/conteúdo entre mensagens conhecidas, sem expandir o contrato do backend.

`getConversation` e `getConversations` usam `cache: 'no-store'`. Além de exigir
dados atuais, isso evita o lock de cache observado no Chromium: um segundo GET
da mesma URL aguardava uma consulta antiga bloqueada, impedindo a leitura mais
recente de vencer durante navegação.

Há retentativas limitadas para falhas transitórias e para o job existente de
título assíncrono. Após esgotá-las, a pendência continua na fila; próximo evento,
retorno ao primeiro plano ou `online` tenta novamente. Não há polling contínuo.
A retomada mescla somente a primeira página do histórico, mantendo páginas e
cursor já carregados, e atualiza a conversa selecionada. Windows/PWA usam a mesma
mesclagem de estado; a emissão e o replay nativos ficam restritos ao Android.

## Continuidade renovável de dez segundos

O runtime conserva em memória a conversa hands-free elegível, sua autoridade
de identidade e o instante monotônico do último `player_drained`. Esse evento já
é emitido pelo player depois de atingir o playback head esperado. O retorno HTTP,
fim da geração e início da fala não autorizam a janela. A renovação só é aceita
quando o turno inteiro termina com sucesso, usando o instante de drenagem já
observado, sem acrescentar o tempo de fechamento do player.

Para uma ativação aceita em `a` e uma última reprodução concluída em `p`, a
conversa é reutilizada quando `0 <= a - p < 10.000 ms`. No limite exato de
10.000 ms, uma nova chamada inicia outra conversa. O destino é fixado na
aceitação: captura, geração e reprodução podem durar mais de dez segundos sem
expirar o turno. A próxima resposta integral renova a janela por completo, sem
limite para a duração total da sequência.

Cada nova chamada recebe sessão e turno próprios. A continuação automática
mantém a sessão ativa e seu prazo existente de dois segundos para início da
fala, contado da mesma drenagem. A resposta dessa continuação renova os dez
segundos. Timeout de continuação não renova nem apaga a elegibilidade anterior.

Falso positivo, comando vazio, erro de STT/TTS e cancelamento não renovam a
janela. Uma elegibilidade anterior ainda pode ser usada dentro do prazo
original. A troca/revogação da identidade, desligamento do runtime e morte do
processo invalidam esse estado. Callbacks antigos não podem renovar uma
ativação posterior. Não há timer de expiração, escrita dessa janela em disco
ou alteração/exclusão da conversa persistida.

## Painel Android

O painel continua uma `VoiceInteractionSession` oficial, com cartão arredondado,
paleta da Aegis, botão de cancelar de 48 dp e estado curto. Sua janela usa
gravidade inferior e altura do conteúdo, respeita insets e restringe a região
tocável ao cartão. Não escurece a tela inteira nem mantém a tela ligada. A
tentativa única de despertar da ativação publicada anteriormente é preservada,
sem contornar o bloqueio do sistema.

As nove barras usam RMS do bloco PCM16 mono/16 kHz já capturado. A medição não
retém áudio; normaliza a energia, aplica piso de −60 dBFS e limita a escala em
−12 dBFS. Não há ganho automático para transformar silêncio em atividade.
O sinal visual ocupa um único slot com sessão e proprietário; uma amostra com
mais de 400 ms deixa de alimentar as barras. A captura permanece em seus blocos
existentes, e a interpolação visual fica limitada a aproximadamente 30 fps.

Processamento e reprodução usam pontos animados de estado, sem apresentar uma
medição acústica fictícia. O áudio TTS não foi instrumentado. Ao ocultar/destruir
o painel, frames, listeners e medição visual param. Isso não cancela o pedido;
o cancelamento explícito continua pertencendo ao runtime. Redução de movimento
desliga a decoração e a interpolação; o nível real pode atualizar em 5 Hz.
Erros apresentam frase curta e fechamento limitado, mantendo detalhes nas
configurações/diagnóstico existentes.

O cancelamento genérico também fecha o erro depois de o turno ter terminado.
O pedido guarda um snapshot da apresentação; antes de fechar, o callback exige
runtime ainda inativo, mesma instância e mesmo snapshot. Nova apresentação,
ocultação ou destruição invalida esse snapshot. Assim, um cancelamento atrasado
não fecha o painel da próxima sessão, inclusive quando o Android reutiliza a
mesma instância da janela.

## Identidade editorial

`releaseIdentity.ts` reconhece toda a família `0.8.x`, incluindo
`0.8.0-unstable.N`, como **I Miss the Peace and Quiet**, sem tagline. Sobre e o
cartão lateral usam o mecanismo existente, com a versão efetiva de cada host.
A família `0.7.x` conserva codinome e frase anteriores; versões inválidas ou
famílias desconhecidas não herdam a identidade de `0.8.x`.

## Compatibilidade, dados e TTS paralelo

Não há novo provedor, modelo, migration, formato PCM, endpoint de TTS ou protocolo
de reprodução. O repositório `aegis-tts`, sua instância, configuração e
implantação não fazem parte desta rodada. A refatoração paralela não foi
incorporada. A janela e a UI dependem apenas da conclusão já disponível no
player Android atual; não dependem da implementação interna futura do TTS.

Nenhum caminho de atualização limpa dados, reinstala o aplicativo, troca
credenciais ou toca no modelo privado, preferências, métricas e WAVs existentes.
Os testes utilizam fixtures sintéticas e loopback; gravações/modelos privados
não entram nos commits.

## Validação e publicação

Verificações executadas em 04/11/2026, com fontes congeladas ao final da revisão:

| Verificação | Resultado e limite |
| --- | --- |
| Kotlin/JVM Android, código de produção | 138 aprovados, zero falhas/skips; inclui 16 de continuidade, dez do painel, três do journal e 32 do backend HTTP/WS em loopback |
| Instrumentação Android/Tauri | `compileUniversalDebugAndroidTestKotlin` aprovado; novo teste de `conversationChanges` compilado, não executado em dispositivo |
| Node/interface compartilhada | 175 aprovados; typecheck e build de produção aprovados |
| PWA/interface compartilhada | 103 aprovados; typecheck, build e service worker aprovados |
| Chromium: sincronização hands-free | 21 verificações aprovadas com Vue/HTTP real e journal Android simulado |
| Chromium: streaming/navegação/leitura | 260 verificações aprovadas em PWA, Android simulado e Windows simulado |
| Chromium: identidade editorial/Sobre | 30 cenários de layout e nove variantes da versão de runtime aprovados nos três hosts, 320–1.100 px e fonte 100%/200%; zero falhas no relatório final |
| Contratos de release/IPC/painel | 66 aprovados |
| Rust portátil | 90 aprovados; dois opt-in existentes ignorados |
| Rust Android ARM64 | `cargo check --target aarch64-linux-android --locked` aprovado com Rust 1.94/NDK 29 |
| APK Android ARM64 | `npm run build:android` aprovado; ZIP conferido com somente `arm64-v8a` e sem `.tflite` |
| Backend: contratos hands-free/transcrição | 37 aprovados, zero falhas/skips, sem serviços reais |
| Distribuição/publicador | 16 testes JS de preview/ledger e 32 testes Python do publicador aprovados, sem publicação durante esses testes |

O teste de continuidade usa o sink PCM de produção e um dispositivo simulado:
EOF não abre a janela enquanto o marcador/head não concluir. Cobre 9.999 ms,
10.000 ms, expiração posterior, renovação repetida, respostas longas, continuação
automática, cancelamentos durante geração/playback, erro de TTS, STT vazio/falho,
troca/revogação de identidade, reinício e callback concorrente de sessão antiga.
A aceitação manual também é testada antes de um handoff de captura ultrapassar
o prazo, sem deslocar seu instante original.

A revisão acrescentou regressões de navegação versus snapshot antigo, renomeação,
usuário local cancelado antes de `done`, posição de mensagem interrompida, limite
global de três GETs e autoridade antiga versus journal novo. Uma fixture HTTP
falhava quando o cancelamento fechava uma conexão antes do primeiro byte: passou
a aceitar somente esse EOF vazio, mantendo a rejeição de cabeçalhos parciais.
O lote JVM completo passou após o ajuste. Uma captura visual inicial ocorreu
durante alterações/HMR e foi invalidada; o resultado definitivo da repetição
é registrado separadamente.

Comandos principais:

```sh
npm test --prefix apps/aegis-node
npm run build --prefix apps/aegis-node
npm test --prefix frontend/aegis-pwa
npm run build --prefix frontend/aegis-pwa
npm run test:release --prefix apps/aegis-node
npm run test:rust --prefix apps/aegis-node
npm run test:hands-free-sync --prefix packages/aegis-client
node packages/aegis-client/tests/ux-stream-browser.mjs
node packages/aegis-client/tests/release-identity-browser.mjs
# Em apps/aegis-node/src-tauri/gen/android:
./gradlew :app:testUniversalDebugUnitTest \
  --tests 'com.aegis.node.NodeHandsFree*Test' \
  --tests 'com.aegis.node.NodeConversationChangesTest' \
  :app:compileUniversalDebugAndroidTestKotlin --console=plain
```

Ambiente local: Rust 1.94, Java 25, SDK 37 e NDK 29 já disponíveis. O SDK reduzido
não tinha wrappers clang de todos os alvos exigidos pela descoberta do Tauri;
foi criado `/tmp/aegis-polish-sdk`, com links para os arquivos existentes e os
wrappers padrão de alvo. O SDK compartilhado não foi alterado. Cargo e evidências
ficam em diretórios temporários desta tarefa. O APK local é de depuração;
a versão/código distribuídos serão os reservados pelo publicador oficial.

O ADB disponível em `/tmp/aegis-hands-free-sdk/platform-tools/adb` listou zero
dispositivos conectados nesta execução.

### Preparação da preview

Implementação enviada ao remoto em `6e4e233df12bbac1eed5e06eed1d0f50d803ff9e`.
O plano oficial `publish-dev --plan` confirmou somente `pwa` e `native`, com
nenhuma migration pendente. A operação
`20261104T142743Z-21082efa` publicou a PWA e solicitou a distribuição assinada
pelo workflow confiável da `main`, sem executar código candidato com as chaves.

A PWA está no SHA da implementação, imagem
`sha256:17741b0915057f6c2a580ea465b95f0d89b1e966c12be3ca6a0b8457981337d0`.
A API permanece no SHA `350f892442acba5e1826ee60d376935c75a4fd49`, imagem
`sha256:d583b6278841d785bc7b3486f96ddc6ba456397571ad7c16afd3f0013c5243bf`.
IDs, imagens e instantes de início da API, PostgreSQL, Qdrant e Neo4j foram
comparados antes/depois e permaneceram iguais. Não houve deploy ou reinício de
TTS, API ou bancos, migrador, limpeza de dados, instalação em Node ou mudança
de configuração de serviços.

O ledger reservou **0.8.0-unstable.11**, código Android **8000011**, no
[run 41847991639](https://github.com/phcastello/Aegis/actions/runs/41847991639),
com fonte `6e4e233`. Essa reserva já está consumida e não pode ser reutilizada.
O run foi cancelado antes da assinatura/publicação após a auditoria encontrar
um caso residual: o cancelamento genérico durante o erro terminal não fechava
imediatamente o cartão, embora seu botão funcionasse. A reserva `unstable.11`
permanece consumida; a correção recebe uma nova tentativa, sem reutilizar seus
bytes, versão ou código. O feed continua em `unstable.10` nesse checkpoint.
Aceite físico e a matriz extensa de release estável não são gates desta preview
e continuam pendentes.

A correção é `09f21c6`; o lote completo foi repetido com 138 testes JVM e 66
contratos aprovados, além da compilação de instrumentação. Os testes de UI/web
anteriores continuam válidos: essa correção altera somente Kotlin Android,
seus testes e documentação.

Permanecem obrigatórias no aparelho: atualização assinada sobre a instalação
existente e preservação dos dados; wake word/nível real/silêncio; alto-falante e
fim da fala; chamadas nos limites de dez segundos; continuação sem wake word;
primeiro plano, segundo plano, rede interrompida, tela bloqueada/apagada,
cancelamento em cada fase, navegação/toque fora do cartão, insets e
acessibilidade. Compilação, JVM e Chromium não comprovam esses comportamentos
físicos.

## Riscos e decisões para revisão

- O relógio de referência é a drenagem confirmada por Android, sem medição
  acústica no alto-falante. Roteamento/Bluetooth e atraso físico continuam
  exigindo homologação.
- A visualização da captura é uma intensidade global distribuída entre barras,
  sem análise de frequência. Isso mantém o custo baixo e evita uma FFT sem
  necessidade funcional.
- A identidade de continuidade fica em memória e invalida a sequência diante
  de alteração da autoridade. Nenhuma conversa da interface é adotada como
  destino hands-free.
- A separação entre descarte visual e cancelamento é intencional: ocultar uma
  janela pelo Android não interrompe uma resposta em andamento. O botão e o
  comando explícito cancelam os recursos do turno.
- Sem ID de usuário no evento existente do chat, textos idênticos enviados por
  origens concorrentes entre as mesmas mensagens conhecidas podem associar o
  objeto local a outro ID de usuário equivalente. As linhas/conteúdos persistidos
  continuam presentes; uma correlação exata futura exigiria ampliar o contrato
  de chat, dispensável para esta rodada. Nenhum contrato TTS foi ampliado.
- Título concluído depois das retentativas limitadas e falha de rede prolongada
  dependem do próximo gatilho de reconciliação. Evitou-se polling permanente
  somente para observar o job de título.
