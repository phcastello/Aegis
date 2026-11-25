# Latência do aviso de ativação — Android

## Diagnóstico

O aviso sonoro de 40 ms é local. A rede não precisa entregar o som e o STT é iniciado **depois** da solicitação do aviso. O `AudioRecord` permanece capturando durante toda a decisão: a fronteira da palavra reconhecida, o áudio que já pertence ao comando e a janela do verificador não são reiniciados. A espera observada antes do aviso inclui outras etapas:

1. A captura ociosa trabalha com blocos de 3.200 amostras a 16 kHz (200 ms). A chamada Android `AudioRecord.read(..., READ_BLOCKING)` aguarda as amostras solicitadas ([documentação Android](https://developer.android.com/reference/android/media/AudioRecord#read(short[],int,int,int))). A detecção primária pode estar pronta antes de o bloco ser entregue ao Kotlin.
2. O verificador recebe exatamente 2.560 amostras (160 ms) de lookahead depois da decisão primária. O bloco seguinte pode ultrapassar essa fronteira mesmo que ela já seja suficiente.
3. O worker executa a verificação local e aguarda a eleição autenticada (contexto em [hands-free Windows e multinó](windows-hands-free.md)). Mesmo quando a internet/servidor já devolveu o vencedor, o recorder só observava esse resultado no fim do próximo bloco de 200 ms.
4. O Android confirma novamente o vencedor, inicializa a captura de comando a partir do PCM retido, solicita foco da mídia e marca o estado de escuta. Só então solicita a reprodução local. O worker do aviso prepara um `AudioTrack` estático; `play()` inicia o caminho de reprodução, mas não mede o instante acústico no alto-falante ([documentação Android](https://developer.android.com/reference/android/media/AudioTrack#play())).

Essas operações são sequenciais na implementação anterior. A diferença de tempo de detecção entre Android e Windows registrada em bancada (mediana de 320 ms, p95 de 400 ms) motiva conservar a arbitragem entre nós quando outros participantes ainda podem aparecer. Não se reduz janela, limiar nem quantidade de etapas do modelo de wakeword nesta alteração Android.

## Ajuste local

Enquanto existe uma candidatura automática normal aguardando o lookahead ou a eleição, o bloco solicitado ao mesmo gravador cai de 200 para **40 ms** (640 amostras). Com referência de mídia externa ativa, usa-se **80 ms** (1.280 amostras), pois a sincronização existente é limitada a 45 ms por leitura. Ambos são múltiplos do frame de 20 ms do cancelador de mídia. Fora da candidatura, inclusive na escuta ociosa, gravação de comando, continuação, diagnóstico e estudo isolado de bateria, permanece o bloco de 200 ms. Não há timer adicional, busy polling, novo gravador nem alteração da janela de captura.

O PCM recebido é anexado integralmente ao comando pendente antes de observar o resultado. A janela verificada continua contendo os mesmos dados: 40.000 amostras anteriores (limitadas pelo início da gravação) e 2.560 posteriores. A eleição, verificação de identidade/deadline, descarte dos perdedores, cancelamento, foco e reprodução do aviso mantêm os contratos anteriores. A alteração não antecipa o aviso para um nó ainda não eleito e não exige foco nem output de áudio em standby.

Em um fluxo contínuo ideal, o teto nominal da quantização de leitura para observar o resultado cai de 200 para 40 ms (80 ms com referência externa). Cada uma das duas esperas de leitura — lookahead e resultado da eleição — pode economizar até aproximadamente 160 ms na comparação de seus tetos (120 ms com referência externa); não se deve somar isso como garantia por ativação. Uma varredura simulada das 20 fronteiras primárias de 160 amostras dentro de um bloco demonstrou economia máxima de 2.560 amostras, ou 160 ms, na chegada ao lookahead no modo de 40 ms, verificando a igualdade de toda a janela PCM em ambas as cadências. Não se alterou a cadência do trabalho ocioso; o custo adicional de wakeups do candidato ativo continua limitado à curta janela da decisão.

## Diagnóstico no aparelho

O export de métricas hands-free passa a separar, para ativações automáticas efetivamente aceitas:

- `wake_primary_read_observation_estimate`: fronteira estimada por amostra até a observação primária; inclui efeitos de buffering, agendamento e inferência, não uma medição acústica.
- `wake_lookahead_wait`: observação primária até a submissão da janela ao worker.
- `wake_local_verification_wait`: submissão até a conclusão local do verificador (inclui espera da fila).
- `wake_coordinator_wait`: conclusão local até o retorno da decisão autenticada.
- `wake_grant_observation_wait`: retorno da decisão até a observação pelo gravador.
- `wake_grant_observed_to_cue_request`: observação até a solicitação do aviso (captura, foco, apresentação e estado).
- `wake_decision_to_cue_request_estimate`: tempo total aproximado da fronteira primária até a solicitação do aviso.

O worker também mede `activation_cue_enqueue_to_play`, tempo de enfileiramento/preparação do `AudioTrack` até retornar de `play()`, inclusive em avisos manuais/diagnóstico. A métrica é publicada depois da liberação da conclusão do aviso, evitando interferir no gate da captura. Cada estágio mantém contagem, total, mínimo e máximo por identidade no export existente; não registra PCM, texto, credenciais nem nomes de nós. A métrica anterior `wake_verification_and_election_wait` foi preservada. Nem o relógio estimado da captura nem a chamada `AudioTrack.play()` provam o instante acústico ouvido pelo proprietário.

## Validação selecionada

- 47 testes JVM passaram, incluindo as suítes de áudio, janela do verificador, candidato assíncrono e eleição. Foram compilados os arquivos Kotlin de produção correspondentes e o `NodeHandsFreeCue.kt` contra o SDK local; no host, os limites Android de identidade/transportador foram simulados, mantendo transporte OkHttp real contra um coordenador de teste loopback.
- Os novos testes exercitam cadência de leitura, igualdade de janela e sufixo do comando para 20 offsets, PCM retido durante a verificação, observação da conclusão, relógios do worker, e contratos estáticos de integração/ordem do aviso; os testes de eleição cobrem perdedores, decisões inválidas, deadline e alteração de identidade.
- 22 testes de contrato estático selecionados passaram (`hands-free-contract.test.mjs` e `hands-free-wake-cadence-contract.test.mjs`).
- Medição física no S20 FE, inclusive reprodução de mídia e arbitragem com outro nó, ainda pendente. Não se afirma economia de bateria nem latência acústica medida; a compilação integral Android não foi executada nesta rodada.
