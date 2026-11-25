# Captura contínua e alternativas de consumo — 17 de novembro de 2026

Este documento registra pesquisa e propostas de comparação. Não altera a fonte
ativa, não promete economia e não homologa um novo capturador. A prioridade é
preservar a escuta contínua, corrigir a prontidão do início e medir mudanças
públicas do Android. Não usar duty cycling, pausas periódicas, API oculta, root,
hacks de fabricante ou uma concessão artificial de privilégios.

Contexto: [investigação inicial](battery-investigation-20261116.md),
[sequência automática](battery-automation.md) e
[guia de medição](battery-diagnostic-user-guide.md).

## Evidência disponível e alcance

O trio automático analisado começou em 16/11 e terminou em 17/11. Os valores
abaixo são diferenças do contador de carga do telefone inteiro, não energia
isolada de microfone, detector ou aplicativo.

| Cenário | Duração observada | Descarga do telefone | Taxa | Acordado | Tela atribuída |
| --- | --- | --- | --- | --- | --- |
| A · sem captura | 30 min 1,422 s | 8,158 mAh | 16,303 mAh/h | 4,04% | 0% |
| B · captura sem detector | 30 min 0,170 s | 53,027 mAh | 106,044 mAh/h | 95,69% | 2,00% |
| C · captura e detecção | 30 min 0,161 s | 53,027 mAh | 106,045 mAh/h | 95,55% | 9,60% |

A/B são elegíveis pelo critério atual; a diferença observada é aproximadamente
+90 mAh/h no telefone, associada à passagem para quase sempre acordado. C excede
o limite de tela de 5% e não serve para concluir o custo energético adicional do
detector. A igualdade dos contadores B/C não demonstra custo zero.

O contador avançou repetidamente em passos de 4,079 mAh: A perdeu dois passos,
B/C treze. Em 30 minutos, um passo representa aproximadamente 8,16 mAh/h na taxa
calculada. Essa resolução observada, temperatura e outros consumidores limitam
comparações pequenas; não inferir precisão superior pela quantidade de casas
decimais. C registrou 24,64 s de CPU do detector e 53,25 s do processo em 30 min;
esses tempos se sobrepõem. O primeiro equivale a aproximadamente 1,37% de um
núcleo, sem conversão válida para mAh.

O dump privado de AudioFlinger salvo antes desse trio já mostra, no S20 FE API 33,
a entrada ativa da Aegis com os seguintes valores. São uma observação daquele
momento, não uma nova captura simultânea à comparação longa:

| Campo de entrada observado | Valor |
| --- | --- |
| Fonte/dispositivo | MIC / microfone interno |
| Taxa da entrada e do track cliente | 16.000 Hz / 16.000 Hz |
| Formato/canais | PCM16 / mono; processamento também PCM16 |
| HAL frame count e buffer | 320 frames / 640 bytes; 20 ms nominais |
| Reserva do track cliente | 6.400 frames; 400 ms a 16 kHz |
| Flags e fast capture | AUDIO_INPUT_FLAG_NONE; sem FastCapture inicializado |
| Effect Chains reportadas | 0 |

Portanto não há uma conversão 48→16 kHz demonstrada nesse trecho AudioFlinger
para eliminar. As 50 unidades nominais de HAL por segundo também não se tornam
2,5 só porque o aplicativo leria em lotes de 400 ms; isso não é uma contagem
medida de despertares, mas distingue as escalas HAL/app. Zero Effect Chains
reduz a evidência de que retirar efeitos de framework seria uma otimização
imediata; não prova ausência de todo processamento OEM ou de diferenças entre
fontes. Não descrever ganhos de VOICE_RECOGNITION como efeitos já removidos.

Esses dados não descrevem todos os caminhos internos do fabricante nem garantem
a mesma configuração em outro aparelho ou rota. Não aumentar a taxa com base
em uma suposição de HAL a 48 kHz.

As esperas iniciais observadas de aproximadamente 78–81 s em B/C também precisam
ser separadas da janela medida. Reserva nominal de 400 ms e leituras de 200 ms
não explicam essa duração. Início solicitado, serviço pronto, captura iniciada,
primeiro áudio e início efetivamente medido são marcos distintos.

## Ordem das alternativas

| Prioridade | Proposta | Classificação | Benefício a verificar |
| --- | --- | --- | --- |
| 1 | Registrar configuração e prontidão reais | Diagnóstico que pode ser implementado sem mudar a fonte | Identificar espera, silenciamento, efeitos, rota e conversões antes de escolher uma otimização |
| 2 | Comparar MIC com VOICE_RECOGNITION; depois UNPROCESSED suportado | Próximo candidato experimental, opt-in | Verificar se outro caminho de captura reduz processamento e consumo sem regressão acústica |
| 3 | Reduzir trabalho repetido por leitura; comparar reserva e tamanho de lote separadamente | Otimização orientada por perfil | Menos contabilização, chamadas/cópias ou overrun; não significa liberar o wakelock de áudio |
| 4 | Comparar taxa do dispositivo e conversão explícita somente se houver evidência de conversão redundante | Pesquisa condicionada à configuração observada | Reduzir conversões; o S20 atual não apresenta esse alvo no trecho AudioFlinger observado |
| 5 | Comparar backend AudioRecord com AAudio/Oboe | Pesquisa posterior de maior escopo | Verificar cópias, conversões e backend; não há offload público de INPUT pelo modo PowerSaving |
| — | SoundTrigger/DSP proprietário | Integração de sistema/fornecedor, fora da solução pública genérica | Pode mudar estruturalmente o custo, mas não existe uma rota portável demonstrada para nosso APK/modelo |

Nenhum benefício de energia dessas propostas foi medido. A fonte normal continua
MIC até existir evidência acústica e energética suficiente para outra decisão.

## Diagnóstico público útil

No S20 API 33, é possível observar a captura da própria Aegis sem nova permissão:

- `AudioRecordingConfiguration.getClientFormat()` e `getFormat()`, API 24,
  distinguem o formato do cliente e o usado pelo dispositivo. A documentação
  exemplifica uma conversão de taxa feita pelo framework.
- `getClientEffects()`, `getEffects()` e `isClientSilenced()`, API 29, ajudam a
  observar efeitos reportados e silêncio imposto pela política de concorrência.
  A lista não comprova ausência de todo processamento interno do fabricante.
- `AudioRecord.getTimestamp(..., TIMEBASE_BOOTTIME)`, API 24, pode registrar
  posição de frames e tempo de entrega quando disponível. Não inventar timestamp
  se a API retornar indisponível. O contador é reiniciado ao parar/iniciar.
- `getBufferSizeInFrames()` informa a reserva efetiva, que pode superar a pedida;
  a configuração/rastreamento de rota deve ser observada após iniciar a captura.

Coletar nos limites e callbacks relevantes, fora do loop de áudio. Não guardar
PCM nem identificadores pessoais de periféricos para esse diagnóstico. Formato,
fonte, tipos de efeitos, disponibilidade e marcos de tempo são suficientes.

`AudioRecord.getSampleRate()` descreve a taxa configurada do cliente; retornar
16 kHz não comprova por si só a taxa do HAL. `PROPERTY_OUTPUT_SAMPLE_RATE` e
`PROPERTY_OUTPUT_FRAMES_PER_BUFFER` descrevem saída, não o microfone. Não usar
essas propriedades como taxa/buffer nativos de INPUT.

Fontes primárias: [AudioRecordingConfiguration](https://developer.android.com/reference/android/media/AudioRecordingConfiguration),
[AudioRecord](https://developer.android.com/reference/android/media/AudioRecord) e
[AudioManager](https://developer.android.com/reference/android/media/AudioManager).

## Fontes MIC, VOICE_RECOGNITION e UNPROCESSED

Uma AudioSource define uma configuração, não um contrato universal de consumo.
A política do produto escolhe dispositivo, ganho e processamento. A orientação
oficial indica que VOICE_RECOGNITION não deve aplicar AGC/supressão de ruído por
padrão. UNPROCESSED existe desde API 24; verificar
`PROPERTY_SUPPORT_AUDIO_SOURCE_UNPROCESSED` antes de tratar o resultado como raw.
Sem suporte, a API permite solicitar a fonte, mas não garante ausência de
processamento e pode comportar-se como DEFAULT.

Próximo candidato concreto: mesmo PCM16, mono, 16 kHz, leituras e modelos,
comparando MIC com VOICE_RECOGNITION. Incluir UNPROCESSED se suportado. Não mudar
limiar/modelo ao mesmo tempo. Menos preprocessing pode reduzir trabalho ou
selecionar outro caminho OEM; também pode piorar ruído, sensibilidade, falsos
disparos e comandos imediatamente após a wakeword. O ganho permanece hipótese.

Não incluir VOICE_COMMUNICATION nessa primeira comparação: ela pode ativar
AEC/AGC e é privacy-sensitive por padrão, alterando concorrência e comportamento.
O controle público de privacy-sensitive, API 30, não é um controle de energia.
Preservar permissões, foreground service de microfone e política de concorrência.

Fontes primárias: [MediaRecorder e fontes raw](https://developer.android.com/media/platform/mediarecorder),
[AudioSource](https://developer.android.com/reference/android/media/MediaRecorder.AudioSource),
[preprocessing no AOSP](https://source.android.com/docs/core/audio/implement-pre-processing) e
[AudioRecord.Builder](https://developer.android.com/reference/android/media/AudioRecord.Builder).

## Reserva, leitura e conversões

A leitura de 3.200 amostras a 16 kHz representa 200 ms: aproximadamente cinco
retornos/s e 9.000 em 30 min. Uma leitura de 400 ms pode reduzir esses retornos
pela metade, mas acrescenta até 200 ms de espera de lote frente à atual. Ela não
reduz amostras ou frames do detector por segundo, e não interrompe a captura.
Esses números descrevem chamadas e latência, não economia prevista.

Aumentar a reserva mantendo o tamanho de leitura pode proteger contra overrun.
O mínimo de criação não garante gravação estável sob carga. Usar a reserva do
constructor/Builder, conferir a efetiva e medir. Não transportar recomendações
de `AudioTrack.setBufferSizeInFrames()` para AudioRecord: essa API é de playback.
Não substituir leitura bloqueante por polling ocupado para ganhar latência.

PCM16 mono a 16 kHz transporta 32 kB/s; a 48 kHz, 96 kB/s. Se uma conversão do
framework realmente existir, solicitar taxa nativa pode apenas transferir a
conversão para o aplicativo, pois o detector continua precisando de 16 kHz.
Preservar filtragem antialiasing e medir o conjunto. AAudio permite taxa não
especificada e consulta da concedida; Oboe pode inserir conversões próprias.
Nenhum desses contratos escolhe automaticamente a configuração de menor energia.

Fontes primárias: [AudioRecord](https://developer.android.com/reference/android/media/AudioRecord),
[AudioRecord.Builder](https://developer.android.com/reference/android/media/AudioRecord.Builder),
[AAudio NDK](https://developer.android.com/ndk/reference/group/audio) e
[guia Oboe](https://github.com/google/oboe/blob/main/docs/FullGuide.md).

## AAudio/Oboe, offload e suspensão

AAudio existe desde API 26. A referência NDK é explícita:
`AAUDIO_PERFORMANCE_MODE_POWER_SAVING` não suporta INPUT e usa NONE quando
solicitado para entrada. `POWER_SAVING_OFFLOADED`, API 36, também é de reprodução.
`AudioTrack.PERFORMANCE_MODE_POWER_SAVING` igualmente é OUTPUT. Não há aqui um
modo público documentado que transfira nosso capturador contínuo para offload
por mudar o nome de uma opção.

Ainda é possível pesquisar backend, cópias e buffering. Isso exige verificar o
modo/formato concedidos e os erros de rota; não é uma economia demonstrada.
Oboe usa VoiceRecognition por padrão, o que deve ser controlado em uma
comparação com MIC. A configuração de input preset do AAudio chega na API 28;
privacy-sensitive chega na API 30. Preservar fallback e regras do Android.

No AOSP 13 e 16, o caminho MMAP de captura também adquire wakelock; a tag pode
ser MmapCapture em vez de AudioIn. Essa mudança de nome não prova que o telefone
passou a suspender. Os dois caminhos exigem medição no aparelho e não autorizam
liberar à força um wakelock mantido por audioserver.

Fontes primárias: [referência AAudio](https://developer.android.com/ndk/reference/group/audio),
[Oboe](https://github.com/google/oboe/blob/main/docs/FullGuide.md),
[AudioFlinger 13](https://android.googlesource.com/platform/frameworks/av/+/refs/heads/android13-release/services/audioflinger/Threads.cpp) e
[AudioFlinger 16](https://android.googlesource.com/platform/frameworks/av/+/refs/heads/android16-release/services/audioflinger/Threads.cpp).

## SoundTrigger e limites de privilégios

O framework trata modelos/dados SoundTrigger como opacos, com contrato entre
aplicativo e fornecedor. Selecionar Aegis como assistente não torna nosso TFLite
compatível com o DSP. As APIs de detecção correspondentes permanecem SystemApi/
ocultas; não são uma base SDK pública genérica para esta implementação.

Há uma nuance: no AOSP 16, CAPTURE_AUDIO_HOTWORD e MANAGE_SOUND_TRIGGER incluem
proteção por papel. Portanto não afirmar que o papel de assistente nunca pode
conceder nenhuma permissão relacionada. Isso tampouco demonstra uma exceção
suficiente para nossa integração: MANAGE_HOTWORD_DETECTION é internal|preinstalled,
e permanecem as restrições de API, modelo, fornecedor e disponibilidade. Uma
integração OEM seria um projeto distinto, sem garantia universal entre Androids.

Fontes primárias: [SoundTrigger](https://source.android.com/docs/core/audio/sound-trigger),
[VoiceInteractionService AOSP](https://android.googlesource.com/platform/frameworks/base/+/c8de8c708fd74a1d8d784ce630cc9857f6b4d698/core/java/android/service/voice/VoiceInteractionService.java) e
[permissões AOSP 16](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android16-release/core/res/AndroidManifest.xml).

## Comparação pequena no aplicativo assinado existente

O capturador da versão analisada fixa MIC, 16 kHz, PCM16 e mono. A ponte de
controle aceita os modos do ensaio, mas não uma fonte arbitrária. Assim, consultar
a configuração atual é possível; abrir a variante VOICE_RECOGNITION dentro do
mesmo aplicativo exige uma nova compilação com backend diagnóstico opcional.
Não há comando público existente que faça essa troca read-only.

Um backend futuro pode usar uma seleção fechada de perfis, permitida somente em
ensaio explícito, com MIC como fallback e sem reescrever a configuração normal.
As fontes propostas usam as mesmas permissões já necessárias para a captura;
não pedem acesso a alarmes exatos, privilégios de DSP ou mudanças de otimização
do telefone. Isso é proposta, não implementação deste documento.

Um helper externo ou app de teste teria outro UID e possivelmente outra política
de foreground/assistente, portanto não comprovaria equivalência com a Aegis.
Consultas shell de AudioFlinger/policy podem descrever o estado atual e perfis
declarados, mas não substituem abrir e medir o perfil alternativo. Não usar
instrumentação, injeção de código ou mudança de settings para contornar isso.

## Critérios do próximo experimento

1. Preparar captura e confirmar áudio antes de iniciar a janela. Registrar espera
   e falha de preparação separadamente; não preencher a duração com tempo sem
   disponibilidade comprovada.
2. Observar fonte, formatos, efeitos reportados, reserva e rota nos limites. No
   S20, manter 16 kHz enquanto não houver evidência contrária no caminho real.
3. Comparar uma variável por vez, começando pela fonte em captura sem detector.
   Nenhuma variante pode parar periodicamente ou descartar áudio para economizar.
4. Validar positivos e negativos de fala, música/TV e ruído com modelo/limiar
   fixos. Conferir latência e comandos colados em "Aegis". Rejeitar regressões,
   silenciamento, perda de amostras e overrun; não confundir ausência de candidatos
   com teste do custo de chamadas ao verificador.
5. Repetir opções promissoras com tela apagada, sem carga, temperatura estabilizada,
   ordem alternada e mesma configuração de energia/rede. Ensaios curtos detectam
   falhas; melhorias pequenas exigem janelas maiores e repetições, considerando o
   passo de 4,079 mAh observado no contador.
6. Comparar CPU do aplicativo e audioserver, suspensão e descarga do telefone.
   Não converter CPU em mAh por função, nem interpretar a ausência de medidor
   PowerMonitor no API 33 como consumo zero. Conservar resultados inelegíveis
   com seus motivos.

A hipótese só pode virar padrão após ganho reproduzido e validação acústica.
Este trabalho de pesquisa não executou nova captura, teste energético, instalação,
alteração de serviço ou publicação.
