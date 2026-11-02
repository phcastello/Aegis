# Coleta de ativações e sessão hands-free — v0.8.0

Correção derivada de `feat/v0.8.0-hands-free` em `c7779ab`, na branch
`fix/v0.8.0-activation-lifecycle`, worktree `/home/phc/Aegis-activation-fix`.
A pasta inicial `/home/phc/Aegis` estava em `feat/v0.7.0-completion` com alterações
locais; essas alterações, arquivos não rastreados e as outras worktrees foram preservados.

## Detector e separação do PCM

O modelo continua `aegis-mww-fleurs-dev-2026-10-31`, SHA-256
`f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514`.
Pesos, arquitetura, treinamento, threshold, janela de scores, histerese e
refratário não foram alterados. A única extensão nativa observa os scores
instantâneo e agregado no primeiro evento aceito do bloco; não reutiliza o último
score do bloco para representar essa decisão.

O retorno nativo já é a posição cumulativa da decisão, com resolução de 160 samples
(10 ms), e não o fim do bloco Kotlin de 3.200 samples. O comando agora começa
nessa posição dentro do bloco. A cauda desse mesmo bloco entra tanto na captura
normal quanto na persistência local. O ring de 40.000 samples continua sendo o único
buffer circular do microfone: seus aproximadamente dois segundos anteriores só
entram em `trigger_context.wav`. Nenhum contexto anterior é passado ao realtime,
WAV batch ou backend. A regra existente de texto final permanece; regex não foi
usada para corrigir a fronteira de PCM.

`post_activation.wav` conserva todo PCM da captura, inclusive silêncio e áudio
rejeitado pelo VAD. O limite total é 320.000 samples (20 s). O WAV é fechado antes
do processamento/TTS e não cresce durante a continuação da conversa. A continuação
mantém a correlação da sessão e começa no onset qualificado pelo VAD. No modo
exclusivamente diagnóstico, o detector mantém o stream contínuo e cada evento
retém até três segundos posteriores (o limite existente de ausência de fala), sem
iniciar STT. Eventos diagnósticos sobrepostos compartilham o mesmo grupo.

Ativação manual registra um evento imediatamente, mesmo se Cancelar chegar antes
da primeira amostra. Sua posição é estimada pelo relógio da captura; scores e
posição nativa são null. Na wake word, a fronteira PCM é a posição retornada pelo
modelo, não uma estimativa do início acústico da palavra. O horário absoluto da
ativação e o corte de cancelamento mapeiam samples ao retorno da leitura bloqueante;
latência do dispositivo e escalonamento continuam sendo incertezas explícitas.

## Coleta e exportação

A coleta vem desligada. Em **Configurações → Áudio e voz → Hands-free**, habilite
**Guardar ativações localmente**. A tela explica que um falso disparo pode gravar
conversas próximas. O limite padrão é 100 MiB; a interface oferece outros limites
(o contrato nativo aceita 10–2.048 MiB). Desativar revoga imediatamente novas
amostras; reativar não retoma uma gravação antiga. Há exclusão local confirmada,
permitida quando não há eventos ativos. Nenhum evento é apagado automaticamente.

Dados ficam em `Context.noBackupFilesDir/hands-free-activations-v1`, privados do
aplicativo e excluídos de backup automático. O thread do microfone apenas copia
PCM limitado para uma fila de 256 tarefas; escrita, hashes, fsync e recuperação
ocorrem no escritor dedicado. Reserva conservadora por evento impede exceder a
quota com capturas simultâneas. Limite, fila cheia e falhas ficam visíveis no estado
da coleta; eventos podem ter lacunas explicitamente marcadas quando a escrita falha.
Não há rede, log de gravações, telemetria de conteúdo nem treinamento nesse módulo.
Uma morte do processo antes de uma tarefa alcançar armazenamento pode perder essa
cauda ainda em memória; não se promete durabilidade síncrona no thread de captura.

A operação existente **Exportar métricas e ativações (ZIP)** abre o seletor oficial
`ACTION_CREATE_DOCUMENT` com MIME `application/zip`. O proprietário escolhe o
provedor/pasta e o nome datado `aegis-handsfree-AAAA-MM-DD_HH-mm-ss-0300.zip`.
O bridge devolve apenas estado, sem caminhos privados ou gravações para a WebView.

O ZIP inclui `metrics.json` integral, `manifest.jsonl`, `export.json`, eventos datados
com os dois WAVs e `event.json`, além de `index.html`, scripts, estilos e README.
Uma barreira do escritor cria um snapshot consistente dos WAVs e metadados; eventos
em andamento são identificados e continuam nos originais. A exportação não apaga
dados. Em recuperação após morte do processo, o encerramento efetivo fica null;
`recoveredAt` identifica a observação e `process_restart_unknown` o motivo desconhecido.

## Revisão e dataset offline

Extraia tudo e abra `index.html` em navegador desktop. Os players usam caminhos
relativos. Classifique com 1 (correta), 2 (falso positivo), 3 (incerta), 0 (limpar) e
navegue com as setas. Os rótulos começam não classificados e são locais ao navegador.
Para o dataset com WAVs, selecione explicitamente a pasta extraída no revisor;
essa seleção o navegador concede leitura dos arquivos locais sem servidor.

O ZIP datado do dataset contém WAVs selecionados, JSONL/CSV com os IDs originais,
rótulos humanos, horários, versões, proveniência, hashes e caminhos relativos.
`groupId` é preservado e `split=null`: todos os exemplos de um grupo devem permanecer
juntos em qualquer separação futura entre treino, validação e teste. Casos incertos
continuam incertos. O escritor ZIP do navegador é código próprio, sem biblioteca
ou CDN. O README exportado descreve schema 1, formatos e campos desconhecidos.

## Janela, energia e cancelamento

A sessão de captura/processamento é propriedade do runtime, e não da janela.
`VoiceInteractionSession.onHide()` e `onDestroy()` liberam os recursos visuais sem
cancelar pedidos. Cancelar explicitamente invalida captura, STT, chat, TTS e player,
fecha a janela e libera os recursos de energia. O último bloco em voo é finalizado
em ordem no escritor, inclusive quando o WAV tem zero samples.

O painel é compacto, translúcido e superior, com insets das barras/recortes e região
de toque limitada ao cartão. Partials são transitórios: até 240 caracteres e dez
atualizações por segundo, vinculados à captura; não são comandos nem dados persistidos.

Cada ativação observa `PowerManager.isInteractive` e `KeyguardManager.isKeyguardLocked`.
A janela ativa usa `FLAG_KEEP_SCREEN_ON`; se inicialmente apagada, faz uma única
tentativa pública com `FLAG_TURN_SCREEN_ON`, sem remover o bloqueio. Qualquer
`ACTION_SCREEN_OFF` suprime novas tentativas nessa sessão, sem presumir uma ação
física do usuário. Um partial wake lock limitado a dez minutos mantém a execução
ativa mesmo sem janela; a concessão é renovada a cada continuação ativa e liberada
no encerramento. A escuta passiva não adquire
esse lock nem mantém a tela ligada.

Android/OEM pode negar o despertar. Sem privilégios adicionais, o aplicativo não
força o desligamento imediato: ao terminar, remove flags e devolve o controle ao
gerenciamento normal de energia. Não há Device Admin, Accessibility Service ou
pedido para contornar essas restrições. Referências públicas:
[VoiceInteractionSession](https://developer.android.com/reference/android/service/voice/VoiceInteractionSession)
e [flags de janela](https://developer.android.com/reference/android/view/WindowManager.LayoutParams).

## Evidência de validação

- Kotlin Android: 105 testes passaram, compilando o Kotlin de produção. Incluem
  áudio, backend HTTP/WebSocket, cancelamento, playback simulado, métricas,
  persistência/WAV/ZIP, quota, fila cheia, recuperação e política de apresentação.
- JVM/JNI/libfvad e detector reais no host: suíte aprovada com 31 casos
  (30 executados, um condicional não executado); fixture opcional de sobreposição contínua de cue não habilitada. Sem microfone físico.
- Paridade C API: 37 WAVs, 1.856.302 samples, 30 ativações e 14.596 chamadas,
  com blocos de 160, 3.200 e tamanhos irregulares; eventos e scores exatamente
  iguais à biblioteca baseline imutável. Scores do primeiro evento independem do
  particionamento. Relatórios privados em `~/.cache/aegis-hands-free/activation-*`.
- Rust portável: 90 aprovados, dois ignorados pela suíte existente.
- Contratos de release/bridge: 59 aprovados, incluindo oito de hands-free.
- Node/interface: 166 testes aprovados e build/typecheck aprovados.
- Chromium `file://`: revisão, reprodução WAV, rótulos, atalhos, persistência,
  hashes e ZIP datado aprovados, sem requests HTTP/HTTPS. Configurações web também
  exercitadas com IPC simulado, sem atribuir essa evidência ao Android real.
- Integração adicional: classes reais do coletor compiladas pelo Gradle geraram
  um ZIP com assets de produção e PCM sintético; após extração, Chromium `file://`
  reproduziu os WAVs em `events/2026-11-01/<UUID>/`, classificou e gerou o dataset.
  ZIP/CRC32, SHA-256 dos dois WAVs, grupo e ausência de rede foram verificados.
  Isso também é ensaio JVM/navegador, sem captura ou provider SAF no Android.

O ADB listou zero dispositivos conectados. Não foi realizada homologação física
nem se atribui evidência de host/JVM/navegador a um aparelho Android. Permanecem
pendentes no aparelho: escuta passiva apagada; ativação bloqueada/desbloqueada;
comando após conversa; encerramento natural; Cancelar em cada fase; tela apagando
durante operação; exportação de várias ocorrências; reprodução/rotulagem do ZIP
real e geração do dataset. A geometria e o despertar também dependem dessa verificação.

## Publicação

O plano do `publish-dev` confirmou `components=["native"]` e nenhuma migration
pendente. API e PWA permaneceram no SHA `350f892442acba5e1826ee60d376935c75a4fd49`,
sem reconstrução, reinício ou alteração de configuração. Não há merge na main,
release estável, mudança de gateway ou alteração do contrato de TTS.

A primeira reserva `0.8.0-unstable.7`, fonte `ff8a1ec`, foi cancelada no
[run 41373906229](https://github.com/phcastello/Aegis/actions/runs/41373906229)
antes da publicação: a revisão independente encontrou a recuperação incompleta de
um cabeçalho WAV parcialmente escrito. A reserva permanece consumida. O reparo
reescreve o cabeçalho canônico e registra `wav_header_repaired`, preservando PCM
recuperável. A regressão exercita cabeçalho ausente, parcial (dez bytes) e completo corrompido.

A reserva `0.8.0-unstable.8`, fonte `b9101a5`, também foi cancelada no
[run 41374443282](https://github.com/phcastello/Aegis/actions/runs/41374443282),
antes da publicação: um cancelamento entre o endpoint e o STT podia deixar o evento
local aberto. O caminho agora finaliza explicitamente e não recria token de sessão
após o cancelamento. Falhas conhecidas ao abrir o microfone também preservam seu
motivo em vez do encerramento genérico do worker. Um contrato adicional protege
esse ramo; a validação física desse intervalo continua pendente.

A reserva `0.8.0-unstable.9`, fonte `ddba654`, foi cancelada no
[run 41375134665](https://github.com/phcastello/Aegis/actions/runs/41375134665)
antes de publicar. A revisão do caminho de fila cheia identificou que a finalização
de fallback precisava conter também suas próprias exceções de checkpoint.
A proteção agora mantém o erro no escritor da coleta, registra
`capture_finalization_failed`, preserva os samples aceitos e permite nova tentativa
quando o armazenamento volta a funcionar. O teste verifica que nenhuma exceção
escapa ao handler da thread e que o escritor permanece vivo.

A build final foi solicitada como `0.8.0-unstable.10`, código Android
`8000010`, fonte `00f21ff816df67df9ba84085590507bc3e5c14cc`, no
[run 41375742888](https://github.com/phcastello/Aegis/actions/runs/41375742888).
O run concluiu com sucesso em `2026-11-02T02:14:00Z`; a prerelease foi publicada
às `02:13:52Z` e o feed recomendado passou da `.6` diretamente para a `.10`.
As reservas `.7`, `.8` e `.9` permaneceram consumidas/canceladas, sem publicação.
[Release e downloads](https://github.com/phcastello/Aegis/releases/tag/node-v0.8.0-unstable.10).
A atualização está disponível pelo fluxo existente **Configurações → Atualizações**;
a instalação exige a ação normal do proprietário. Evidência privada da operação em
`~/.local/state/aegis/publish-dev/20261102T020422Z-912f46fe/`.

Android e Windows passaram seus jobs de build; o job de assinatura/publicação
passou. A etapa ampla `acceptance` foi pulada pela política de preview; isso não
representa homologação física. O workflow usa o controle confiável da main
`8607dc0`, enquanto a fonte do aplicativo é `00f21ff`.

Os binários assinados do Actions e os downloads públicos sem autenticação foram
comparados por SHA-256, junto ao feed recomendado:

| Artefato | Bytes | SHA-256 |
| --- | ---: | --- |
| `Aegis-Android-arm64.apk` | 27.835.837 | `d8a742b43d6a04d5113b2cbdb423591d3092b0f003391fb83b79089db549dbd4` |
| `Aegis-Windows-x86_64-Setup.exe` | 6.393.014 | `0c27fdcd572ebd24686e155ce5ccbd2d32314710aa5c5e958ece71908c6bed60` |
| `Aegis-Windows-x86_64-Setup.exe.sig` | 456 | `b984441521a7f43cee1b2335caa59a6b24e011c9b7107f2c74263e8c6fc8c06e` |

`apksigner` confirmou v2/v3 e o certificado existente
`df5286c556657ddcee6ce79c510b967678bac8ced18d082db0e318bc47debb31`.
`zipalign -c -P 16 4` passou. Pacote, versão/código e ARM64 foram conferidos.
A assinatura do updater Windows foi verificada criptograficamente com a chave
pública já existente, incluindo o vínculo assinado à versão `.10`.

Os quatro assets do revisor dentro do APK são idênticos aos fontes testados.
Os novos nomes do bridge permanecem no DEX após R8 e o símbolo JNI de scores
permanece exportado na biblioteca ARM64. Não há WAVs de eventos ou modelo privado
TFLite empacotados. A presença de símbolos/strings e assinatura não comprova
execução do bridge num aparelho real; essa etapa continua pendente.

Evidências sanitizadas: [activation-dataset-validation.json](activation-dataset-validation.json).
Os binários, inspeções e ensaio integrado JVM/revisor ficam localmente em
`~/.cache/aegis-hands-free/activation-artifacts-20261101/`, sem entrar no Git.

A gravação/exportação assíncrona usa espaço temporário adicional ao conjunto
armazenado; se faltar espaço para o snapshot, a exportação falha sem apagar os
originais e limpa seu staging. O ZIP de dataset é montado na memória do navegador:
a RAM disponível limita seleções grandes. Os eventos ativos exportados são
provisórios; seus estados finais só ficam completos no encerramento da sessão.

Para repetir as verificações pertinentes:

```sh
npm ci --prefix packages/aegis-client
npm ci --prefix apps/aegis-node
npm test --prefix apps/aegis-node
npm run build --prefix apps/aegis-node
npm run test:release --prefix apps/aegis-node
npm run test:rust --prefix apps/aegis-node # Rust >=1.94
npm run test:hands-free-settings-browser --prefix apps/aegis-node
npm run test:hands-free-reviewer-browser --prefix apps/aegis-node
cd apps/aegis-node/src-tauri/gen/android
./gradlew :app:testUniversalDebugUnitTest --tests 'com.aegis.node.NodeHandsFree*Test'
```

Gradle exige Android SDK/NDK/JDK e arquivos Tauri gerados; os testes de navegador
usam Chromium/Playwright já adotados no repositório. Os testes JNI reais usam
`scripts/hands-free/run-vad-host-tests.py --library-dir <biblioteca-JNI-host>
--wake-model <modelo-privado> --output <diretorio-privado-novo>`. A paridade usa
`scripts/hands-free/test-detector-observations.py` com as bibliotecas baseline e
candidata, o mesmo modelo e fixtures privadas; os argumentos estão em `--help`.
Nenhum áudio ou modelo privado foi adicionado aos commits.

## Commits enviados

- `8aef5c4` — `fix(android): expose precise wake boundaries without changing detector decisions`
- `ff8a1ec` — `feat(android): persist activation datasets and decouple hands-free presentation`
- `d2fc483` — `fix(android): repair interrupted WAV headers before dataset export`
- `b9101a5` — `fix(android): account for inference delay in activation timestamps`
- `ddba654` — `fix(android): finalize captured events when cancellation precedes STT`
- `00f21ff` — `fix(android): isolate activation storage failures from voice capture`

Todos enviados por push normal em `origin/fix/v0.8.0-activation-lifecycle`, sem PR
automático, force push ou merge. O commit final de documentação registra a evidência
após conferir a publicação; não altera o runtime compilado na preview.
