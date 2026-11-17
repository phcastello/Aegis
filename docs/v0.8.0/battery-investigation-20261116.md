# Investigação e diagnóstico de bateria em 16 de novembro de 2026

O diagnóstico separa tempo de CPU do aplicativo, contabilidade do Android e
carga/energia do telefone inteiro. Nenhuma dessas fontes permite publicar
'mAh do detector', 'mAh do ring' ou 'mAh do microfone' como medição direta.
Diferenças entre cenários são estimativas experimentais e conservam a variação
provocada por outros processos, sinal de rede, temperatura e suspensão.

## Fontes e disponibilidade

| Fonte | Disponibilidade e unidade | Atribuição e limite |
| --- | --- | --- |
| BatteryManager | API 21+: carga em µAh, corrente em µA e energia restante em nWh, se o fabricante fornecer | Telefone inteiro; sem permissão adicional. Valores indisponíveis são omitidos, não tratados como zero. Corrente positiva indica carga; negativa, descarga. |
| Process/Debug | CPU do processo em ms e da thread atual em ns; APIs públicas | Tempo de execução, não energia. Não cobre trabalho em audioserver/HAL nem outras threads quando se observa uma thread isolada. |
| SystemHealthManager/HealthStats | API 24+: snapshot do próprio UID, CPU user/kernel e timers disponíveis | Sem privilégio para o próprio UID. Outro UID exige BATTERY_STATS. A medição CPU_POWER_MAMS está obsoleta desde API 26 por não ser confiável. |
| PowerMonitor | API 35+: trilhos medidos ou consumidores possivelmente modelados, acumulados em µW·s desde boot | Hardware inteiro, inclusive conectado à energia. Enumeração pode ser vazia; nomes e cobertura variam. Não é energia por app/função. |
| Perfetto/ODPM | Perfetto desde Android 9; ODPM depende do hardware | CPU, despertares, frequência e suspensão; trilhos do aparelho quando presentes. Power Profiler/PowerMetric documentam Pixel 6 e posteriores. |
| Batterystats | Diagnóstico por shell/ADB | Timers e estimativas do sistema. A estimativa baseada em power_profile multiplica atividade por corrente calibrada; não equivale a um medidor exclusivo de cada função. |

Fontes oficiais: [BatteryManager](https://developer.android.com/reference/android/os/BatteryManager),
[Process](https://developer.android.com/reference/android/os/Process),
[Debug](https://developer.android.com/reference/android/os/Debug),
[SystemHealthManager](https://developer.android.com/reference/android/os/health/SystemHealthManager),
[UidHealthStats](https://developer.android.com/reference/android/os/health/UidHealthStats),
[HealthStats](https://developer.android.com/reference/android/os/health/HealthStats),
[BATTERY_STATS](https://developer.android.com/reference/android/Manifest.permission#BATTERY_STATS),
[PowerMonitor](https://developer.android.com/reference/android/os/PowerMonitor),
[PowerMonitorReadings](https://developer.android.com/reference/android/os/PowerMonitorReadings),
[Power Profiler](https://developer.android.com/studio/profile/power-profiler) e
[power_profile AOSP](https://source.android.com/docs/core/power/values).

No AOSP atual, a interface pública PowerMonitor usa cache de até 30 segundos e
ruído por UID. Isso impede interpretar uma diferença curta como energia precisa
de uma única inferência. A disponibilidade e essas características podem variar
com a versão/OEM. Trilhos e consumidores podem representar hardware sobreposto;
não somar ambas as listas como consumo total nem comparar nomes entre aparelhos
sem conhecer o hardware. Fonte: [PowerStatsService](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/services/core/java/com/android/server/powerstats/PowerStatsService.java).

## Consulta PowerMonitor implementada

`HandsFreePowerMonitors` oferece `request(force = false)` e `state()`; o coletor
existente pode solicitar atualizações ao observar endpoints. Não há timer,
alarme, wakelock, microfone, upload ou conversão para consumo por aplicativo.

- A API 35 fica isolada em um backend; em API 24–34 a resposta é
  `unsupported_api`, com `supportedApi=false` e lista vazia. O S20 FE atualmente
  conectado usa API 33; esse aparelho não terá esta fonte opcional de dados.
- No máximo uma tentativa por minuto, também quando `force=true`. O parâmetro
  identifica uma solicitação manual e não ignora o limite.
- Um executor com no máximo um worker; thread ociosa expira após 30 segundos e
  fila limitada. Nenhuma chamada do serviço ocorre na thread de captura.
- Prazo de dez segundos observado passivamente em `state()`, `request()` ou
  chegada da resposta. Ao vencer, o token anterior é invalidado: callbacks
  atrasados não substituem leituras mais recentes. Não existe alarme de timeout.
- Estado limitado a 64 medidores e nomes de até 128 caracteres. Exporta
  `scope=whole_device`, `energyUnit=uWs`, tipo `measurement` ou `consumer`,
  timestamp local do resultado e timestamp elapsed de cada medidor.
- Energia negativa/indisponível e timestamp inválido não viram dados numéricos.
  Uma falha pode conservar a lista anterior com `stale=true` e timestamps
  originais. Uma lista vazia é `unavailable/no_power_monitors`.
- Dados ficam stale em erro/timeout/fechamento ou após dois minutos; timestamp
  antigo do hardware também marca a linha e, se nenhuma linha estiver fresca,
  o snapshot inteiro. `observedAt` registra o último resultado/timeout observado;
  `sampledAt` identifica a última lista recebida; `checkedAt` identifica a consulta.

## Protocolo comparável dos três cenários

1. **Desligado:** hands-free sem captura nem detector; registrar se o observador
   permanece no processo. Isso não deve ser chamado de 'aplicativo encerrado'.
   Medir processo realmente encerrado exige observação externa.
2. **Captura somente:** mesma fonte, formato, blocos, buffer e observador, com a
   análise de wake word desabilitada. PCM transitório permanece local.
3. **Detecção local:** mesma captura e detector/verificador completos, com os
   efeitos de ativação suprimidos: sem beep, painel, coleção, STT ou resposta.
   O objetivo deste ensaio é isolar captura e detecção. Investigar sessões reais
   de STT/TTS exige outra janela identificada, pois elas alteram o trabalho.

Usar mesma duração configurada, tela apagada, telefone sem carregamento,
condições de rede/temperatura/ambiente comparáveis e ordem alternada entre
repetições. O observador não deve manter um wakelock na referência, pois isso
poderia esconder o custo de impedir suspensão. Na sequência automática, um alarme desperta o aparelho somente na fronteira
entre cenários. Não há polling que acorde a referência desligada. Usar os
relógios efetivamente observados nos limites e registrar o custo das transições
separadamente; não subtrair energia estimada do alarme.

Guardar snapshots nos limites e CPU por trecho em acumuladores separados.
Leituras de HealthStats/PowerMonitor ficam fora do loop do microfone. Corrente
amostrada é auxiliar e seu próprio custo deve ser igual nos três cenários.
Comparar captura−desligado e completo−captura como estimativas do custo adicional,
com dispersão entre repetições. Não suprimir resultados negativos/pequenos como
se fossem zero físico. Invalidar janelas com carregamento, interrupções,
reinício ou interferência relevante.

Conversões: diferença em µAh / 1.000 = mAh do telefone; diferença em nWh /
1.000.000 = mWh do telefone; diferença de µW·s / 3.600.000 = mWh. Integrar corrente
é uma estimativa dependente da amostragem. Nenhuma conversão cria atribuição por
função onde o sensor não fornece essa informação.

## Áudio, suspensão e interpretação do aparelho

A documentação pública descreve `UidHealthStats.TIMER_AUDIO` como playback.
Entretanto, no AOSP Android 13, `RecordThread` usa o mesmo caminho de
`ActiveTracks.updatePowerState`/`BatteryNotifier.noteStartAudio` da contabilidade
por UID. Logo, a categoria OS 'audio' pode incluir gravação. O
`AudioPowerCalculator` aplica o timer a uma corrente média `POWER_AUDIO`.
Isso é estimativa do sistema, não medição isolada do microfone. A implementação
Samsung pode conter alterações e deve ser validada no próprio aparelho.

`AudioIn` é a tag do wakelock parcial do RecordThread no AOSP, mantido por
audioserver com atribuição aos UIDs clientes. Um UID atribuído a esse wakelock
não significa que o aplicativo chamou `PowerManager.acquire()` diretamente.
A thread de gravação libera seu wakelock quando não há tracks ativos; trocar o
tamanho de leitura do app não equivale a encerrar a captura/HAL.

Fontes primárias: [AudioFlinger Android 13](https://android.googlesource.com/platform/frameworks/av/+/refs/heads/android13-release/services/audioflinger/Threads.cpp),
[BatteryNotifier Android 13](https://android.googlesource.com/platform/frameworks/av/+/refs/heads/android13-release/media/utils/BatteryNotifier.cpp),
[HealthStatsBatteryStatsWriter Android 13](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android13-release/services/core/java/com/android/server/am/HealthStatsBatteryStatsWriter.java) e
[AudioPowerCalculator Android 13](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android13-release/core/java/com/android/internal/os/AudioPowerCalculator.java).

Não há opção pública universal de AudioRecord que mantenha a captura contínua
e garanta remover `AudioIn`. `AudioTrack.PERFORMANCE_MODE_POWER_SAVING` se aplica
a playback. Fontes como MIC/VOICE_RECOGNITION/UNPROCESSED podem mudar o caminho
e processamento conforme o OEM; não garantem DSP de baixa energia nem ausência
de wakelock. Parar/liberar a captura permite standby quando não há outro cliente,
mas criar intervalos cegos perde a garantia de escuta contínua e não deve ser
aplicado silenciosamente como economia.

Fontes: [AudioRecord.Builder](https://developer.android.com/reference/android/media/AudioRecord.Builder),
[AudioSource](https://developer.android.com/reference/android/media/MediaRecorder.AudioSource),
[AudioTrack](https://developer.android.com/reference/android/media/AudioTrack#PERFORMANCE_MODE_POWER_SAVING).

SoundTrigger/DSP é uma alternativa estrutural, porém o modelo é opaco ao framework
e depende do fornecedor. `createAlwaysOnHotwordDetector` e a detecção confiável
usam APIs de sistema; escolher o assistente padrão não torna nosso TFLite
compatível nem concede os privilégios. Nenhum hack de fabricante, root ou API
privada foi introduzido. Fontes: [SoundTrigger](https://source.android.com/docs/core/audio/sound-trigger),
[VoiceInteractionService AOSP](https://android.googlesource.com/platform/frameworks/base/+/master/core/java/android/service/voice/VoiceInteractionService.java).

## Investigação externa complementar

Perfetto pode capturar `sched_switch`, `sched_wakeup`, frequência, idle e
`suspend_resume`, além de `android.power` quando disponível, para correlacionar
CPU do app, audioserver e suspensão real. Perfil detalhado de release pode exigir
`profileable`; não usar um build debuggable como medição final de desempenho.
Batterystats é complementar. Battery Historian deixou de receber manutenção
ativa e a documentação recomenda tracing/Power Profiler. Reset de batterystats
apaga o histórico anterior e não deve ser feito incidentalmente.

Fontes: [Perfetto Android](https://perfetto.dev/docs/learning-more/android),
[CPU/suspensão](https://perfetto.dev/docs/data-sources/cpu-freq),
[energia no Perfetto](https://perfetto.dev/docs/data-sources/battery-counters),
[profileable](https://developer.android.com/guide/topics/manifest/profileable-element),
[Batterystats](https://developer.android.com/topic/performance/power/setup-battery-historian).

Os resultados de testes automatizados e do aparelho são registrados somente após
execução. Suporte ausente no API 33 não é uma falha nem evidência de consumo zero;
o diagnóstico continua usando as fontes efetivamente disponíveis.

## Validação executada do PowerMonitor

Em 16/11, o arquivo de produção e seus testes compilaram diretamente com
Kotlin 2.3.21, JVM 25 e android.jar oficial de API 37. Os oito testes JUnit
passaram sem skips, cobrindo API 33 sem trabalho, despacho assíncrono, limite
de uma tentativa por minuto, capacidade da lista, omissão de sentinelas, timeout
passivo, descarte de respostas antigas, stale de timestamp/erro e fechamento.
Logs e harness ficaram privados em
`/var/tmp/aegis-wakeword-battery-20261116-toolchain/power-monitor-tests/`.
Isso não é execução da API PowerMonitor em hardware API 35+. A compilação Gradle
integrada foi concluída depois, conforme a seção a seguir.


## Validação integrada e escopo físico

A compilação Kotlin/Java real e a suíte Gradle passaram com 217 testes em 19 suítes,
sem falhas, erros ou testes ignorados. Incluem 20 testes do coletor, oito do
PowerMonitor e a paridade de 164 janelas privadas do verificador. Na interface e
ponte: 188 testes Node, 79 contratos, typecheck/build Vue e browser mobile/desktop
passaram. O helper Rust puro passou em dois testes; o crate completo é compilado
no fluxo nativo oficial, não nessa prova portátil.

Na entrega unstable.17, a coleta não cria timer, alarme ou wake lock.
A automação posterior adiciona um alarme por fronteira e proteção de CPU de no
máximo dez segundos durante a transição; nenhum desses recursos é mantido
pelos 30 minutos da referência. Veja [sequência automática](battery-automation.md). O estado normal do usuário não é
reescrito pelo ensaio. Um segundo gate durável precede o início, mantém a escuta
pausada após reinício/falha e só é removido por término explícito após parar o
worker. Consultas de plataforma e gravação ocorrem fora do thread de áudio;
contadores e consultas de modo/gate não esperam o monitor do sampler. O fim da
janela aguarda também o finally real do verificador, após liberar o microfone.

As seções fixas AegisWakeDetector/AegisWakeVerifier e profileable por shell
permitem investigação por Perfetto num computador autorizado. O release continua
não-debuggable. Não se adicionou controle remoto do teste nem comando que permita
ativar microfone por um Intent de outro aplicativo.

Uma leitura inicial do telefone e um trace exploratório de 30 segundos foram
obtidos com a versão anterior instalada. Isso comprova acesso às ferramentas,
mas não constitui o comparativo A/B/C nem homologa energia do código novo.
Dados brutos do aparelho permanecem privados, fora do Git. O ensaio longo deve
seguir o [guia do usuário](battery-diagnostic-user-guide.md).

## AAudio/Oboe: possibilidades e limites

O enum AAUDIO_PERFORMANCE_MODE_POWER_SAVING não dá uma entrada de áudio com
offload de baixo consumo: a referência NDK informa que INPUT usa NONE nesse
caso. No AOSP 13, os backends legados AAudio e OpenSL também convertem essas
solicitações para AUDIO_INPUT_FLAG_NONE. O novo modo POWER_SAVING_OFFLOADED
é de reprodução. Não foi acrescentada dependência nem reescrito o capturador.

Uma comparação nativa futura ainda pode medir diferenças de buffering, cópias,
conversões e backend, sem presumir economia pelo nome do modo. MMAP também
adquire wake lock durante a captura. Qualquer experimento deve preservar fonte
MIC/Generic, regras de permissão/concorrência, integridade do áudio e latência.
Ele deve ser separado das três janelas atuais, que mantêm o capturador igual.

Fontes primárias: [AAudio NDK](https://developer.android.com/ndk/reference/group/audio),
[AAudioRecord Android 13](https://android.googlesource.com/platform/frameworks/av/+/refs/heads/android13-release/media/libaaudio/src/legacy/AudioStreamRecord.cpp),
[Oboe AAudio](https://github.com/google/oboe/blob/main/src/aaudio/AudioStreamAAudio.cpp),
[OpenSL Android 13](https://android.googlesource.com/platform/frameworks/wilhelm/+/refs/heads/android13-release/src/android/AudioRecorder_to_android.cpp).
