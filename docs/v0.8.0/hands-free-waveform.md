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
```

As evidências de inspeção temporal, publicação oficial, versão e APK serão
registradas depois da validação e distribuição. O publicador deve confirmar
somente `native`, sem migrations, e reservar a próxima `0.8.0-unstable.N` no
ledger. Pesos privados não entram no Git nem no APK. Pareamento, conversas,
credenciais, configurações, áudios e rótulos existentes são preservados.
