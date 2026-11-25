# Aegis v0.8.0 — waveform temporal hands-free

Correção na branch `feat/v0.8.0-hands-free-waveform`, worktree isolada
`/home/phc/Aegis-hands-free-waveform`, base `af2855c` de
`feat/v0.8.0-wakeword-strong-production` (`0.8.0-unstable.13`). A autorização
do Strong, rollback ao baseline, configuração, métricas e coleta de ativações
foram preservados. Não houve treinamento, calibração, mudança de pesos ou
alteração de backend/TTS.

## Causa e solução

O visualizador anterior reduzia cada leitura de aproximadamente 200 ms a um
único RMS. As nove barras usavam esse mesmo valor multiplicado por pesos fixos
de posição: eram quase o mesmo sinal, com uma silhueta central simétrica.
Interpolar o nível único não recuperava as variações dentro da leitura. O timer
de 34 ms também não acompanhava o vsync, e `accessibility.isEnabled` reduzia o
movimento mesmo sem uma preferência efetiva para isso.

A nova waveform tem **32 barras finas arredondadas**, ocupando 88% da largura
útil, com alturas baseadas em posições temporais diferentes do PCM real.
Não há pesos simétricos de altura, ruído aleatório, boxes individuais ou brilho.
O viewport tem 72 dp e o padding foi reduzido para manter o cartão praticamente
na mesma altura. Título Aegis, cancelar, paleta azul, janela inferior, insets,
tela bloqueada e ausência de escurecimento permanecem.

## Medição, histórico e apresentação

O produtor lê somente o PCM16 mono/16 kHz já capturado, sem copiar, alterar ou
reter as amostras. Cada janela de **320 amostras/20 ms** produz um RMS. Leituras
parciais acumulam apenas soma de energia e quantidade, nunca PCM adicional.
A escala fixa mapeia −58 dBFS para zero e −12 dBFS para um. Não há ganho
automático: ruído fraco permanece próximo do silêncio, voz baixa continua
distinta de voz normal/alta e a fala normal não precisa saturar.

O envelope usa ataque exponencial com constante de 40 ms. Quedas para níveis
medidos não nulos usam relaxamento de 150 ms. Janelas realmente silenciosas
terminam uma rampa finita de relaxamento em 150 ms, evitando uma cauda numérica
que mantivesse pequenos traços por tempo excessivo. Isso é processamento de
apresentação, sem gate ou mudança do VAD.

O buffer circular contém somente 80 amplitudes e timestamps: 1,6 s incluindo
o histórico visível de aproximadamente **1,28 s**, o atraso de apresentação e
margem para entrega. São 32 posições espaçadas em 40 ms. A apresentação lê o
histórico em `agora − 200 ms`, interpolando entre pontos já medidos. Assim, as
dez medições de uma entrega de 200 ms se distribuem pelo tempo, e o histórico
desloca-se continuamente para a esquerda. Não se geram novas medições ou PCM
durante essa interpolação. O atraso nominal de 200 ms é em relação aos timestamps
dos subframes, acrescido do envelope visual, não um atraso imposto ao comando.

| Frequência | Comportamento |
| --- | --- |
| Captura e entrega | Leitura existente de até 3.200 amostras, aproximadamente 200 ms; inalterada |
| Medição acústica visual | 20 ms, 50 valores/s, entregues em lotes pela captura |
| Histórico | 32 barras, posições temporais a cada 40 ms, aproximadamente 1,28 s |
| Apresentação | `postOnAnimation`, sincronizada ao vsync; meta de 60 FPS em uma tela de 60 Hz, dependente da tela/carga real |
| Movimento reduzido | Atualização por entrega real, aproximadamente 5 Hz; sem loop decorativo |

Nenhuma tela física foi medida nesta rodada: a meta de apresentação não é uma
declaração de 60 FPS sustentados no telefone, nem de 60 medições acústicas/s.

O produtor não espera pela UI. Uma sequência de publicação volatile, ímpar/par,
permite ao consumidor copiar um snapshot pequeno em arrays prealocados. Se a
captura interferir nessa cópia, o frame visual é descartado e a última imagem
completa permanece até o próximo vsync. Cálculos do renderer ficam fora de
qualquer lock de captura. Não há alocação de arrays, Paint ou RectF por frame.

Silêncio medido esvazia naturalmente o histórico. Se a captura parar sem fornecer
mais dados, há fade de segurança e expiração, em vez de congelar a última fala.
Em movimento reduzido, um único callback de expiração também limpa o desenho;
ele não é um timer de animação contínua. Na sequência sintética final, a última
amplitude desapareceu 1.567 ms depois do fim da fala, incluindo histórico,
atraso e relaxamento, sem congelamento.

## Estados, preferências e ciclo de vida

Capturing/continuation mostram somente o histórico real da entrada. Processing
usa pontos discretos de estado e o texto “Processando”. Speaking usa um indicador
de reprodução com arcos, distinto da waveform: não mede nem simula amplitudes
do TTS. Erro mantém a apresentação curta existente; idle não agenda animação.

A preferência de movimento observa a escala global de animação e economia de
bateria. Uma escala explícita prevalece sobre cache atrasado de `ValueAnimator`;
a API é fallback se a configuração estiver indisponível. Um serviço de
acessibilidade habilitado não desliga a fluidez automaticamente. A waveform
continua transmitindo medições reais quando o movimento é reduzido.

O View mantém no máximo um callback de animação pendente. Preferências e dados
novos acordam a apresentação; silêncio não mantém um loop de vsync. Ao ocultar
janela/View, cancelar ou destruir, ele remove callbacks, observer e receiver,
desanexa o produtor e limpa as amplitudes. Reabrir começa com histórico limpo.
Trocar estado substitui o registro do sinal e aplica um limite temporal: dados
anteriores à transição ou de outra sessão não reaparecem. Cancelamentos antigos
continuam sujeitos ao snapshot da apresentação já existente.

Referências de API: [View/postOnAnimation](https://developer.android.com/reference/android/view/View#postOnAnimation(java.lang.Runnable))
e [ValueAnimator no AOSP](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/core/java/android/animation/ValueAnimator.java).

## Verificações

O lote Kotlin/JVM passou com **161 testes, zero falhas/erros/skips**. Inclui
21 testes de sinal/painel e quatro de preferência de movimento, além das
regressões hands-free anteriores: identidade Strong/baseline, importação e
rollback, coleta/metadados, continuidade, áudio e backend em fixtures.

Os testes de sinal cobrem silêncio/ruído baixo, volume, diferenças temporais
entre posições, frames intermediários dentro de uma entrega de 200 ms, sílaba
curta, pausas, cauda/expiração, leituras fragmentadas, duplicadas/fora de ordem,
reset, proprietário/sessão, reabertura, capacidade fixa e produtor/consumidor
concorrentes. Os 18 contratos JS de hands-free/IPC/R8/painel também passaram.

A inspeção visual usa o **núcleo Kotlin de produção** para exportar amplitudes de
PCM sintético determinístico; um harness Chromium reproduz a geometria do View,
gera vídeo e sequência de frames e mede requestAnimationFrame separadamente.
É uma inspeção temporal no host, com limites explícitos: o Canvas do harness
não é o renderer Android, e o FPS do arquivo de vídeo não comprova FPS físico.
Uma imagem isolada não é tratada como evidência de fluidez.

Foram inspecionados 781 frames em 13 s, incluindo 12 frames consecutivos de
16–17 ms atravessando uma entrega de áudio. Todos os 156 frames selecionados de
voz apresentaram mais de oito amplitudes distintas entre posições; houve
movimento em 100% dos frames interiores aos blocos de 200 ms. A variação média
entre frames foi 0,0633 dentro dos blocos e 0,0646 nas bordas, sem degrau
específico de 200 ms. Os 72 frames de silêncio selecionados tiveram amplitude
zero. Picos de voz baixa/normal/alta: 0,407/0,702/0,893.

O requestAnimationFrame medido separadamente no Chromium teve mediana/p95 de
16,7 ms, máximo de 16,8 ms e nenhum intervalo acima de 40 ms. É uma medição do
host; a cadência do Android depende do aparelho. O primeiro ensaio expôs uma
cauda longa do relaxamento exponencial até o silêncio; a rampa finita para
janelas silenciosas corrigiu esse comportamento antes da seleção final da UI.

Vídeo, HTML interativo, folhas temporais, dados Kotlin e procedência ficam em
`~/.cache/aegis-hands-free-waveform/visual-v3/`. Os ensaios intermediários foram
preservados em `/tmp/aegis-waveform-visual-v1` e `-v2`; o resultado final foi
copiado para armazenamento persistente. São somente sinais sintéticos, sem
áudio privado ou transcrições.

Não foi encontrado aparelho conectado por ADB. Também não havia emulador nem
`/dev/kvm` disponíveis. **A homologação visual física permanece pendente.**
Não se abriu outro microfone nem se alterou a cadência/tamanho de captura.
`NodeHandsFree.kt`, núcleo wake word, modelo/allowlist, VAD, STT e TTS permanecem
sem alteração nesta correção. A ausência de mudança desses caminhos e os testes
de regressão não substituem uma medição de latência física no telefone.

## Arquivos e reprodução

- Produção: `NodeHandsFreePanel.kt` e `NodeVoiceInteractionSession.kt`.
- Testes: `NodeHandsFreePanelTest.kt`, `NodeHandsFreePanelMotionTest.kt` e
  `hands-free-panel-contract.test.mjs`.
- Inspeção reproduzível: `HandsFreeWaveformVisualFixture.kt`,
  `export-waveform-visual.py` e `waveform-visual.mjs`, em
  `apps/aegis-node/scripts/`. São fixtures sintéticas fora dos assets do APK.

Comandos principais, reutilizando SDK/JDK e dependências já instalados:

```sh
# Em apps/aegis-node/src-tauri/gen/android:
ANDROID_HOME=/tmp/aegis-polish-sdk ./gradlew :app:testUniversalDebugUnitTest \
  --tests 'com.aegis.node.NodeHandsFree*Test' --console=plain --max-workers=2
# Na raiz:
node --test apps/aegis-node/scripts/hands-free-contract.test.mjs \
  apps/aegis-node/scripts/hands-free-panel-contract.test.mjs
# Destinos sempre novos; compilador Kotlin e Playwright/Chromium já existentes:
python3 apps/aegis-node/scripts/export-waveform-visual.py --output <novo-export>
AEGIS_PLAYWRIGHT_CORE=<modulo-playwright-core-existente> \
PLAYWRIGHT_CHROMIUM_EXECUTABLE=<chromium-existente> FFMPEG=<ffmpeg-existente> \
  node apps/aegis-node/scripts/waveform-visual.mjs \
  --frames <novo-export>/frames.json --output <novo-render>
```

## Preview oficial publicada

O plano oficial confirmou somente `native`, sem migrations. A operação
`20261105T222706Z-ab3b08e2` solicitou a distribuição da fonte
`f5af6e393092a9b0001d1340fb665e40e04f3293`. O ledger reservou
**0.8.0-unstable.14**, código Android **8000014**, sem reutilizar números.

O [run 42121625315](https://github.com/phcastello/Aegis/actions/runs/42121625315)
concluiu com sucesso: builds Android ARM64/Windows, verificações selecionadas,
assinatura oficial isolada, publicação e avanço do feed. A matriz extensa de
aceite de release estável foi ignorada pelo fluxo de preview; não é apresentada
como aprovada, e a v0.8.0 não foi promovida a estável.

- [Release oficial](https://github.com/phcastello/Aegis/releases/tag/node-v0.8.0-unstable.14).
- [APK Android ARM64](https://github.com/phcastello/Aegis/releases/download/node-v0.8.0-unstable.14/Aegis-Android-arm64.apk).
- SHA-256 do APK: `3286f994a4b20a1df77302b69d2668a762efad6baab00919c220880462bcf01e`.
- Certificado oficial: `df5286c556657ddcee6ce79c510b967678bac8ced18d082db0e318bc47debb31`.
- Feed recomendado: `https://raw.githubusercontent.com/phcastello/Aegis/aegis-distribution/latest.json`.

A verificação independente baixou o APK, manifesto e SHA256SUMS da release e
conferiu assinatura/certificado, manifesto real, pacote `com.aegis.node`, versão,
código, flags de produção, somente `arm64-v8a` e alinhamento de 16 KB. Os dois
hashes/versões autorizados continuam presentes no DEX; pesos privados, WAVs,
fixtures e keystores estão ausentes. Feed público/API, manifesto da release,
tag e `sourceCommit` coincidem. Os gates Firebase/BuildConfig são evidência do
workflow oficial, sem alegar repetição local com fontes ou configurações fabricadas.

Todos os containers previamente em execução mantiveram IDs, imagens e instantes
de início antes/depois. Não houve deploy/reinício de backend, PWA, TTS ou bancos.
Não houve instalação, desinstalação, limpeza de dados ou nova importação de
modelo em aparelho nesta rodada. A atualização preserva pareamento, conversas,
credenciais, configurações, áudios, rótulos e o modelo já instalado. O Strong e
o baseline continuam aceitos com os mesmos hashes e parâmetros da `unstable.13`.

Para verificar no telefone: atualizar pelo updater existente, fazer uma chamada
normal com pausas/volumes diferentes, observar deslocamento contínuo e retorno
ao silêncio, alternar processamento/reprodução, cancelar e reabrir e testar a
preferência de remoção de animações. Essa confirmação física continua pendente.

Evidências persistentes: `~/.cache/aegis-hands-free-waveform/`, incluindo
`visual-v3/`, `local-validation-v1.json`, `protected-source-hashes-v1.json`,
`official-0.8.0-unstable.14/verification.json` e comparação dos serviços.
[Resultados estruturados seguros](hands-free-waveform-results.json).

Commit da implementação: `f5af6e3`. O APK utiliza exatamente essa fonte; o
registro posterior acrescenta somente documentação. Branch enviada ao remoto
com Conventional Commits, sem PR, merge na `main` ou modificação de outras worktrees.

## Visualização comum a Android e Windows

O indicador Windows passa a mostrar um histórico de 32 amplitudes reais do microfone durante a captura e continuação, na própria interface do chat. O Windows mantém a escuta na bandeja e não abre nem ativa uma janela para mostrar os estados. A visualização para quando o app está oculto ou minimizado.

Durante o processamento, os dois clientes mostram três pontos em movimento suave. A revisão aprovada pelo proprietário em 25/11/2026 substitui o ciclo anterior de 1,8 s por um ciclo de 10,334 s, com deslocamentos individuais, aproximações e trocas de posições sob rotação do conjunto. A fase vertical perdeu uma repetição completa (1,198 s), sem acelerar as demais partes. Essa atividade interna usa como referência os princípios de movimento apresentados pela equipe de design do Gemini e a prévia Lottie avaliada pelo proprietário. [Illustrating the Gemini App](https://design.google/library/gemini-ai-visual-design) · [Designing the Next Interface with Gemini](https://design.google/library/the-next-interface-gemini). A preferência de movimento reduzido deixa o indicador de pensamento estático e conserva atualizações acústicas reais. A implementação nativa preserva a identidade visual da Aegis: três círculos, cores e dimensões dos próprios indicadores. Uma trajetória normalizada de 597 pontos de controle (tempo, seis coordenadas) alimenta os keyframes CSS no Windows e a interpolação do Canvas no Android, sem incorporar um player Lottie ou o JSON de referência nos aplicativos. O desvio máximo medido contra a prévia renderizada foi de 0,016 unidade em uma escala de 28, inferior a 0,03 dp no painel Android. Os dois hosts compartilham duração, primeiro/último quadro e coordenadas; o teste `thinking-motion-parity.test.mjs` impede divergência. A animação existe somente na fase de processamento e não altera as barras da escuta, o espectro de fala ou a lógica de mãos livres.

Durante a reprodução, as barras passam a representar bandas de frequência, da esquerda (graves) à direita (agudos). A análise usa a PCM16 mono de 24 kHz enviada ao dispositivo, incluindo o ganho de reprodução aplicado no Android. Cada janela Hann de 1.024 amostras (42,67 ms) passa por uma FFT e agrupa a magnitude máxima de cada uma das 32 bandas de aproximadamente 80 Hz a 12 kHz, em escala fixa de −58 a −12 dBFS. Os métodos de janela, transformada e conversão em dB têm a mesma fundamentação dos analisadores de frequência da Web Audio API. [FFT analysis](https://www.w3.org/TR/webaudio/#fft-windowing-and-smoothing-over-time).

Cada quadro de espectro é associado à posição em amostras e só pode aparecer depois que o cursor de reprodução avançar sobre a janela. No Windows, usa-se `waveOutGetPosition` com tratamento das unidades retornadas pelo driver existente. No Android, usa-se a posição de `AudioTrack`. Quadros de um stream anterior, posição indisponível ou paralisada e painéis ocultos não geram amplitude visível. A atualização do espectro é limitada a aproximadamente 25 Hz. [Cursor Windows](https://learn.microsoft.com/en-us/windows/win32/multimedia/retrieving-the-current-playback-position) · [Cursor Android](https://developer.android.com/reference/android/media/AudioTrack#getPlaybackHeadPosition()).

Os dados de apresentação são derivados somente de amostras locais, não são enviados ao servidor e não modificam o áudio, o wakeword ou o barge-in. O Windows transporta apenas as amplitudes agregadas entre Rust e a interface. O cálculo FFT Windows foi medido em bancada otimizada em aproximadamente 20,2 µs por janela, sem incluir UI, áudio físico ou driver. Não houve medição de consumo de bateria ou latência de reprodução física neste ensaio.

A bancada nativa Windows passou com 115 testes aprovados e quatro ignorados (incluindo áudio físico). No Android, 222 testes selecionados de captura, espectro, reprodução, barge-in, painel, continuidade e backend passaram em 15 suítes. Tons sintéticos verificaram a separação entre faixas e a intensidade, a exclusão do áudio ainda na fila, limpeza, silêncio, fragmentação e proteção contra retorno do cursor. O harness visual de host exportou 781 frames com os valores dos núcleos Kotlin de produção e reproduziu a geometria do painel. A validação visual do Android é de bancada, não uma execução do Canvas em aparelho físico.
O frontend Node compilou e passou 199 testes, com 32 verificações de contrato hands-free/painel. O ensaio Chromium percorreu as fases, a atualização real de níveis sintéticos, a expiração, visibilidade, foco, alto contraste e movimento reduzido, com larguras de 320, 390 e 1.180 pixels. O cartão permaneceu com as mesmas dimensões na troca de estado.

Na revisão de 25/11/2026, a paridade da nova trajetória passou no teste compartilhado de keyframes, nos tres testes Kotlin de movimento e no ensaio Chromium de fases, continuidade e movimento reduzido. O frontend passou 199 testes, incluindo verificação de tipos e build; a suite de contratos e distribuição passou 92 testes. Os tres testes de espectro Rust e os seis testes de espectro Kotlin passaram em bancada independente. O fixture visual Kotlin exportou 781 quadros, com coordenadas do núcleo de produção em processamento. Esta validação não inclui compilação integral do APK, instalação ou teste físico.

### Suavização da apresentação durante a fala

A medição continua com 32 bandas de FFT do PCM já reproduzido. A interface mostra 16 bandas, agrupando cada par de faixas frequenciais vizinhas pelo maior valor medido, sem reduzir a resolução da análise. A altura máxima das barras de fala foi reduzida para aproximadamente 69% da anterior. A escuta conserva as 32 barras e a escala preexistente.

O filtro exponencial de apresentação segue o princípio de suavização temporal do [AnalyserNode da Web Audio API](https://www.w3.org/TR/webaudio/#fft-windowing-and-smoothing-over-time), mas com constantes de tempo de 75 ms na subida e 150 ms na descida. O coeficiente usa o tempo entre amostras, para que a resposta não dependa da frequência do monitor ou da tela. Não há interpolações aleatórias, alteração no PCM, ganho ou atrasos na reprodução. Quadros de silêncio válido recebem uma queda breve, limitada a 120 ms, para evitar saltos entre sílabas. A expiração (500 ms na interface Windows), o fim do estado e a ocultação limpam imediatamente a memória do filtro. O Android também descarta a memória na troca do stream/observador e quando o cursor não é válido. No Windows o evento existente não distingue um quadro silencioso de um cursor indisponível; ambos convergem a zero em até 120 ms enquanto os eventos chegam. O temporizador existente também limpa um último quadro silencioso mesmo sem novos eventos, sem manter uma animação independente. A preferência por movimento reduzido apresenta as amplitudes agregadas reais sem o filtro temporal.

Comparação no fixture PCM sintético (mesma sequência de 781 quadros, 78 durante a fala; referência de 32 bandas agrupada em pares para comparar as mesmas faixas): a variação absoluta média entre quadros de fala caiu de 0,02548 para 0,01384 de amplitude normalizada (−45,7%). O pico de altura previsto na área Android de 72 dp caiu de 53,78 para 32,53 dp. Esses valores são do núcleo Kotlin e da geometria, não de medição em aparelho físico.
