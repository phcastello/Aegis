# Comparação automática A → B → C

[Publicação e verificação física: unstable.18](battery-automation-publication-unstable18.md).

A sequência nativa aguarda a tela apagar e executa escuta desligada, microfone
sem detector e detecção local sem comandos, com 30 minutos por etapa. O
WebView pode ficar suspenso; não controla os prazos. A configuração normal
só retorna após a última etapa ou cancelamento explícito.

## Agendamento e integridade

Um PendingIntent privado por etapa usa ELAPSED_REALTIME_WAKEUP.
setExactAndAllowWhileIdle é usado quando a autorização já existe;
setAndAllowWhileIdle é a alternativa. Não se adiciona USE_EXACT_ALARM nem
altera a otimização de bateria. A previsão é aproximada e atrasos ficam no JSON.
Cada prazo parte do início efetivo da própria janela, sem comprimir B/C.

O receiver não exportado valida uma URI canônica com sequência, etapa, corrida
e prazo. Callbacks antigos/duplicados não avançam uma sequência nova.
goAsync e um wake lock parcial de no máximo dez segundos protegem somente o
handoff da transição, com liberação idempotente em conclusão, erro ou timeout.
O início por SCREEN_OFF usa a mesma proteção limitada. Não há wake lock nem
timer de polling durante a etapa desligada.

Antes de cada fase com áudio, o app confirma assistente selecionado e serviço
VoiceInteractionService vinculado/onReady, permissão e disponibilidade do
microfone e modelo. O alarme por si só não concede acesso ao microfone.
Recusa de foreground/captura interrompe a sequência e conserva a pausa de
segurança. Não existe retomada por BOOT_COMPLETED.

O supervisor persiste estado e tokens antes de publicar uma transição.
O gate normal continua fechado enquanto para/junta captura e verificador,
fecha a janela anterior e inicia a próxima. Não há chamada ao encerramento
público entre etapas. Falha de armazenamento, processo reiniciado ou condições
alteradas não fabricam observações ausentes. A recuperação exige término
explícito e uma sequência nova.

O relatório conserva runIds de exatamente esta tentativa, horários planejados
e efetivos, atrasos, transições e duração real. O comparativo não mistura uma
fase faltante com corridas antigas da mesma configuração. O modo de validação
de um minuto por etapa é sempre inelegível, mesmo se um alarme atrasado fizer
a janela durar mais de vinte minutos.

## Prontidão de captura

No ensaio longo da unstable.18, B e C incluíram aproximadamente 78 e 81
segundos de suspensão antes da primeira captura. Os prazos já estavam em
andamento enquanto o Android voltava a dormir entre a transição e a leitura.

A transição agora mantém a proteção de CPU já usada na fronteira até receber
o primeiro bloco positivo e sem silenciamento do microfone. Esse bloco
confirma prontidão. A medição e o próximo alarme partem do snapshot obtido
logo depois. A exportação registra o instante da confirmação e o custo de CPU
da preparação separadamente. A preparação tem prazo absoluto de até nove
segundos desde o início da proteção de dez segundos. Fila, abertura do
microfone e carregamento do modelo contam nesse prazo.

Ao expirar, cancelar ou perder a captura durante a preparação, a sequência
interrompe e conserva o bloqueio necessário para retomada explícita. Uma
leitura tardia não agenda outra etapa. A janela desligada continua sem
captura. O fluxo descarta o áudio da preparação e não abre comandos durante
a medição.

[Pesquisa sobre consumo com escuta contínua](battery-capture-alternatives-20261117.md).

## Fontes e limites

[AlarmManager](https://developer.android.com/reference/android/app/AlarmManager),
[Doze](https://developer.android.com/training/monitoring-device-state/doze-standby),
[alarmes exatos no Android14](https://developer.android.com/about/versions/14/changes/schedule-exact-alarms),
[restrições de foreground/microfone](https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start),
[VoiceInteractionService](https://developer.android.com/reference/android/service/voice/VoiceInteractionService).

Mesmo alarmes exatos podem atrasar. Morte do processo, reinício, força parada,
revogação de permissões e restrições adicionais do fabricante podem impedir
conclusão; o estado interrompido não é um resultado válido. A documentação
Android distingue a exceção de iniciar foreground e a de usar microfone.
A vinculação do assistente ativo inclui capacidades em
[AOSP13](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android13-release/services/voiceinteraction/java/com/android/server/voiceinteraction/VoiceInteractionManagerServiceImpl.java#791);
o app verifica a vinculação real antes de retomar a captura.

O custo de despertar/transição aparece no contexto da janela e não é convertido
em mAh por função. As diferenças entre cenários continuam estimativas do
telefone inteiro, com as limitações descritas na
[investigação de bateria](battery-investigation-20261116.md).

## Verificações de software

A compilação Android real e 240 testes em 21 suítes passaram, sem falhas,
erros ou skips. Incluem os testes do supervisor, coletor/alarmes e as 164
janelas privadas de paridade ONNX já existentes. A interface/cliente passou
em 194 testes Node e os contratos passaram em 85
verificações. O typecheck/build Vue e quatro testes Rust portáveis passaram.
A navegação mobile/desktop em browser passou, incluindo início único, pausa da
atualização visual com a tela oculta, cancelamento, instruções manuais e histórico.
Sete estados reais exportados pelo supervisor JVM, inclusive recuperação de
estado inválido, foram aceitos pelo parser TypeScript. Essas provas não
substituem a execução com tela apagada no aparelho nem medem economia.
