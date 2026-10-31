# Aegis v0.8.0 — protocolo naturalístico de três dias

O objetivo é observar utilidade, disponibilidade e bateria no uso normal.
O [baseline de 25–30/10](baseline-2026-10-25_30/baseline.md) é naturalístico;
três dias posteriores não provam causalidade nem significância estatística.

## Preparação e ativação

1. Instalar a distribuição de desenvolvimento assinada sobre o Node existente,
   pelo fluxo normal de atualização ou pelo APK oficial. Não desinstalar,
   limpar dados ou trocar identidade/assinatura. Conferir versão/build em
   Configurações e preservar o pareamento e a configuração do backend.
2. Conceder microfone e as permissões de notificação solicitadas. Pela interface
   oficial de assistente padrão do Android, selecionar Aegis. A escolha é do
   proprietário. Não usar root, Accessibility, overlay genérico ou desligar
   globalmente as otimizações de bateria.
3. Em **Configurações → Áudio e voz → Hands-free**, com a escuta desligada,
   tocar **Importar modelo privado** e selecionar o arquivo bruto
   `aegis.int8.tflite` de 39.552 bytes pelo seletor oficial do Android. Não
   selecionar o JSON nem um ZIP. A cópia e seu hash estão nas
   [instruções do detector](../detector-parity.md). O Node verifica hash,
   dimensões/quantização e carregamento antes de gravar na área privada.
   Modelo ausente ou rejeitado é indisponibilidade, não escuta ativa.
4. Na mesma tela, habilitar **Escutar “Aegis”**, deixando **Diagnóstico**
   desmarcado para interação real. O diagnóstico pertence ao
   mesmo pipeline, mas não envia comandos e não substitui os três dias de uso.
5. Fazer uma verificação breve: falar “Aegis, o que tem de importante hoje?”
   sem pausa obrigatória após o nome, com outro app aberto e depois bloqueado;
   ouvir resposta completa e testar continuação. Confirmar que a gravação manual
   continua coordenada e que desabilitar interrompe a captura.

A primeira tentativa da `0.8.0-unstable.2` ficou bloqueada antes da configuração
e **não constitui dia válido de hands-free operante**. A coleta só começa após
ativação funcional da preview corrigida. Registrar mudanças de build e excluir
os respectivos dias parciais; atualizar não reclassifica os dados anteriores.

A homologação física permanece necessária. Se a verificação falhar, registrar
build, estado e erro; não classificar o período como uso funcional da feature.
Para interromper uma falha persistente, desabilitar hands-free ou selecionar
outro/nenhum assistente nas configurações do Android. Reabrir o Node para
conferir permissões, modelo, conexão e ativação antes de retomar.

## Coleta sem computador

Usar normalmente por cerca de **três dias completos**, sem manter ADB,
profiling, traces ou logs verbosos. Não resetar `batterystats`, simular bateria
nem alterar as estatísticas para esse acompanhamento. Não impor carregador ou
tela ligada. Anotar mudanças relevantes do uso e configuração sem registrar
conteúdo de comandos ou dados de outros aplicativos.

Excluir **31/10/2026** e também qualquer dia parcial da instalação/ativação
efetiva. Se o primeiro dia completo operante for 01/11, a janela será
**01–03/11/2026**. Se começar depois, mover a janela; não preencher intervalos
sem evidência. Após cada dia concluído:

- Obter captura equivalente em “Atividade da bateria”, com o dia selecionado,
  uso diário, tela ligada e tela desligada. Manter original em privado.
- Registrar versão/build, configuração do detector, períodos de uso deliberado
  sem hands-free e falhas percebidas. Não atribuir ícones do instante de captura
  ao dia inteiro nem presumir que os tempos somam 24 horas.
- Exportar o resumo operacional agregado da Aegis ao fim da janela e guardar
  junto das capturas privadas. Usar **Configurações → Áudio e voz → Hands-free
  → Exportar métricas**; o seletor oficial salva
  `aegis-hands-free-metrics.json` no destino escolhido. Não há áudio nem texto
  de comandos na exportação.

## Formato da exportação

O JSON tem `schema: 2`. Versão/build/modelo/configuração no nível geral descrevem
somente a observação atual (`identityScope`). O catálogo `identities` e
`identityBoundaries` identificam as fronteiras observadas; cada dia contém
`byIdentity` com seus agregados atribuídos. Habilitação e diagnóstico integram
a configuração identificada. Mudanças preservam os totais existentes. Dados
anteriores à migração ficam copiados em `unattributedBeforeSchema2`, sem versão
inventada; `unknown` e `unknown_overflow` representam atribuição desconhecida.
O catálogo retém até 64 identidades e 128 fronteiras, com contadores de descarte.
Permissões e papel de assistente continuam como observações operacionais. `days` é um objeto por
`AAAA-MM-DD` no fuso local do Android. Tempos são milissegundos derivados de
relógio monotônico; `events[].at` e `checkpointAt` têm fuso explícito.

- `requestedMs`: tempo solicitado com a configuração habilitada.
- `captureAudioMs`: duração de PCM lido; `captureConfirmedMs` e
  `detectionConfirmedMs`: disponibilidade efetivamente confirmada;
  `captureLockedConfirmedMs` e `captureScreenOffConfirmedMs`: respectivos
  intervalos com bloqueio/tela desligada.
- `state_<estado>Ms`: duração em cada estado, incluindo indisponibilidade e
  pausas. `captureUnknownGapMs` representa lacunas observadas sem confirmação.
- Contadores: `activations`, `sessions_started`, `commands_transcribed`,
  `commands_empty`, `commands_failed`, `commands_no_speech`, cancelamentos,
  falhas e ausência de fala na continuação.
- Durações agregadas de captura, STT, backend e TTS, e `sessionWakeLockMs`.
  `tts_first_audio{Ms,Count,MinMs,MaxMs}` mede a primeira aceitação de PCM pelo
  player desde o início da requisição TTS; não mede DAC acústico. `tts_total*`
  inclui o drain real; `tts_fetch*` inclui a transferência com backpressure e
  `tts_playback*` vai do primeiro PCM entregue até o drain. A identidade de um
  turno assíncrono permanece fixada na observação do seu início.
  Campos existem quando há observação; ausência não deve ser interpretada como
  atividade retroativamente confirmada.
- `device` conserva a observação normal mais recente de bateria/carga,
  bloqueio e tela, com instante. `events` retém até 128 eventos recentes;
  agregados diários retêm 14 dias. Exportar ao fim da janela evita perder o
  histórico anterior à retenção.

Após reinício há evento `process_restart_unknown_interval` desde o último
checkpoint, com disponibilidade desconhecida. Não inferir cobertura contínua
pelo fato de `requestedMs` ser alto. A persistência ocorre por mudança de estado
e a cada cerca de um minuto durante operação; não há gravação de áudio ambiente
nem polling do WebView por frame.

## Como comparar

Para cada dia, confrontar a configuração habilitada com captura/detecção
**confirmadas**, intervalos desconhecidos, indisponibilidades e pausas
intencionais. Após morte do processo não contar retroativamente a lacuna como
escuta. Consumo baixo com serviço interrompido é falha de disponibilidade.
Avaliar ativações percebidas/indevidas, comandos transcritos/vazios/falhos,
latências agregadas de captura/STT/backend/TTS e continuação, sem guardar texto.
Tela desligada não equivale a CPU suspensa.

Calcular a média posterior `P` do uso diário acumulado e compará-la com
`B = 88,16666666666667%` (seis dias):

- Diferença em pontos percentuais: `P − B`.
- Diferença relativa percentual: `(P − B) / B × 100`.

Exemplo puramente aritmético, sem resultado de teste: `P=90%` corresponderia a
`+1,83 p.p.` e `+2,08%` relativos. Não confundir essas unidades. Preservar dias
acima de 100% e não interpretar soma de percentuais como quantidade de recargas.
Comparar também minutos de tela ligada/desligada, faixas de carregamento,
dias da semana e disponibilidade real. Não dividir consumo diário por tela
ligada para estimar o detector; não derivar mAh, watts, capacidade ou saúde.

Se a janela for domingo–terça, 01–03/11, comparar secundariamente com
25–27/10: média **77,33333333333333%** (apresentada 77,33%). Manter a comparação
com os seis dias e explicitar que seleção por dia da semana não controla outros
fatores. A análise final deve registrar limitações e experiência, sem prometer
que uma diferença pequena vem da wake word.

## Medição técnica separada

Uma sessão breve com `dumpsys` de serviços/áudio/bateria ou tracing suportado
pelo aparelho pode investigar uma falha. Guardar dumps completos em privado e
encerrar a instrumentação antes de continuar a coleta normal. ODPM/Power
Profiler depende de hardware e não deve ser prometido para o aparelho sem
verificação. A exportação agregada cotidiana não depende dessas ferramentas.
