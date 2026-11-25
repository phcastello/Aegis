# Latência do aviso: medição no S20 FE (25/11/2026)

## Observação na prévia .38

O proprietário relatou cerca de três segundos até ouvir o aviso. A exportação
obtida por USB contém três ativações concedidas da `0.8.0-unstable.38`, com
`aegis-microwakeword-primario-v2-2026-11-24`, modo normal. A análise utilizou
somente `metrics.json`; nenhuma gravação foi lida ou transferida para esta
investigação. O tempo da tela foi restaurado depois da extração.

| Estágio registrado no aparelho (ms) | Média | Mín.–máx. |
| --- | ---: | ---: |
| Observação da decisão primária (estimativa por amostra) | 78,3 | 40–114 |
| Lookahead restante | 92,0 | 77–120 |
| Verificador local | 138,3 | 62–198 |
| Espera do coordenador (requisição completa) | 1.181,0 | 1.016–1.266 |
| Observação do grant pelo capturador | 48,7 | 39–58 |
| Preparação após observar o grant | 58,0 | 21–126 |
| Decisão primária estimada → solicitação do aviso | 1.596,3 | 1.567–1.642 |
| Enfileiramento → retorno de `AudioTrack.play()` | 38,3 | 28–57 |

A espera do coordenador corresponde a aproximadamente 74% do tempo entre a
decisão primária estimada e a solicitação do aviso. Isso inclui conexão, rede,
autenticação, inventário e eleição; não mede isoladamente cada parcela. A API
registrava dois nós pareados habilitados (Android e Windows), e a identidade
atual apresentou cinco candidatos, dos quais três foram concedidos e dois
suprimidos. A janela máxima de 650 ms só era eliminada quando todos os
participantes tinham respondido. Não há, nessa versão, evidência de qual tempo
foi efetivamente gasto dentro do árbitro ou se uma conexão TLS precisou ser
aberta para cada chamada.

O marco da decisão primária não é o início/fim acústico de “Aegis”. A chamada
`AudioTrack.play()` também não mede o som físico no alto-falante. Portanto, não
se contradiz o relato de aproximadamente três segundos contando toda a chamada.
A amostra é pequena e não é estimativa de confiabilidade geral do wakeword.

## Mudanças avaliadas até a prévia .40

O foco é reduzir a espera redundante depois de uma confirmação local, sem tocar
no detector, seus limiares, no cancelamento de eco ou no volume do aviso. O
protocolo mantém a exclusividade de um vencedor e o descarte do PCM dos demais
nós. Uma idade local estimada (relógio monotônico) permite aproveitar parte do
tempo já transcorrido desde a detecção; a janela continua limitada e conserva a
espera anterior para clientes sem a informação. As métricas adicionais separam
a espera medida no árbitro do restante da requisição. A conexão HTTP ociosa pode
ser reaproveitada por mais tempo sem enviar sondas ou manter heartbeats novos.

## Nova medição física na prévia .40

Os relatórios JSON foram exportados sem gravações. O S20 FE e o PC estavam
pareados; a primeira amostra contém três ativações já concedidas no Android.
Em seguida, o proprietário fez três chamadas com ambos ouvintes ativos: o PC
respondeu sozinho nas três. O proprietário desativou a escuta do PC, sem
despareá-lo, e fez mais três chamadas; o celular respondeu às três. A extração
por diferença dos contadores da identidade .40, antes/depois, isolou as três
concessões da segunda sequência e manteve as candidaturas perdedoras separadas.

| Estágio (média, ms) | .40 inicial, 3 grants | .40 PC sem escuta, 3 grants |
| --- | ---: | ---: |
| Observação primária estimada | 72,7 | 80,0 |
| Lookahead restante | 104,7 | 93,0 |
| Verificador | 165,7 | 170,7 |
| Coordenação completa | 867,3 | 906,3 |
| Dos quais espera no árbitro | 350,0 | 349,7 |
| Restante da requisição, conforme header | 499,7 | 544,7 |
| Observação do grant | 51,3 | 26,3 |
| Preparação após grant | 41,0 | 22,0 |
| Decisão estimada → solicitação do aviso | 1.302,7 | 1.298,3 |
| Enfileiramento → retorno de play() | 40,7 | 37,0 |

O tempo do árbitro foi praticamente idêntico quando o PC estava desligado do
hands-free. A coordenação era responsável por cerca de 70% da latência interna
na segunda sequência. Os tempos do header incluem apenas a chamada HTTP e não
necessariamente todo o trabalho de identidade feito antes dela. Não somar
coordenação e seus componentes como se fossem etapas independentes.

Com consentimento explícito, um microfone do PC foi usado para estimar a
distância temporal entre o fim da fala (energia acústica) e o primeiro tom de
um aviso do Android (casamento dos dois tons de 40 ms). Apenas uma ativação
teve identificação acústica suficientemente confiável, com intervalo de
aproximadamente **1.990 ms**; as demais não foram mensuráveis por esse método.
O PCM foi processado em memória, sem arquivo de gravação e sem transferi-lo.
O resultado não é média das três chamadas, nem identifica separadamente o
tempo entre o fim da palavra e a decisão primária ou a latência da saída física.
A configuração do tempo de tela foi restaurada para 30 s e a escuta no PC foi
reativada pelo proprietário.

## Decisão atual: primeiro candidato confirmado

O proprietário priorizou eliminar a espera por outros dispositivos, inclusive
quando mais de um está escutando. A API concede imediatamente a primeira
candidatura confirmada, autenticada e válida, sem consultar o inventário nem
abrir janela de comparação. Conserva a exclusividade por dois segundos, recibos
idempotentes, cancelamento e quarentena após reinício. Não há registro de presença,
sondas novas, chamadas especulativas, alterações do wakeword ou sinal antes da
concessão. Quando vários nós ouvem, o primeiro a chegar vence; o de melhor SNR
ou maior proximidade não é necessariamente o vencedor. A autenticação consulta
credencial e nó numa única operação relacional, sem cache de credenciais.
Assim removem-se os ~350 ms medidos no árbitro para chamadas Android desta
amostra; a economia total continua sujeita à rede, ao detector e à saída física.

No Android, o verificador Open v2 omite somente os embeddings e predições
anteriores que já teriam saído do histórico relevante ao máximo de score; os
grafos exatos são identificados por SHA-256, com replay integral para os demais.
Os mels, janela de 160 ms, limiares, amostras e estratégia de captura não mudam.
No host Kotlin/ONNX Runtime, 297/297 janelas de 147 clipes held-out com dois
arranjos e três controles tiveram score máximo bit a bit idêntico e as mesmas
decisões no limiar .0692. Tempo médio 38,46 → 30,41 ms (-20,9%); 150 janelas
completas 42,35 → 30,32 ms (-28,4%). Não corresponde a uma nova estimativa de
precisão ou economia medida no S20 FE.

O cliente Android registra durações de autenticação no servidor, DNS, conexão
(inclusive TLS), TLS, envio do corpo até recepção dos headers, corpo da resposta,
reutilização da conexão e leituras de identidade antes/depois da concessão.
A observação usa o EventListener do OkHttp na mesma requisição e o mesmo pool,
sem registrar URL, endereço, credencial, fala ou PCM, sem pings/heartbeat, nem
alterar a resposta a headers de diagnóstico ausentes ou inválidos. A documentação
primária do [Android Keystore](https://developer.android.com/privacy-and-security/keystore)
orienta não bloquear threads sensíveis com suas operações; as recomendações de
[acesso à rede no Android](https://developer.android.com/develop/connectivity/network-ops/network-access-optimization)
motivam medir a conexão real sem introduzir trabalho periódico. Os novos
estágios são diagnósticos e podem se sobrepor (por exemplo, TLS está dentro
de conexão; autenticação e árbitro estão dentro da espera pelos headers).

## Verificações desta revisão

- Backend: 176 testes de nós, arbitragem e transcrição passaram, zero falhas;
  dez integrações dependentes de PostgreSQL descartável ficaram ignoradas.
  Incluem 47 testes selecionados, relógio determinístico sem agendamento de
  timer, 32 candidatos concorrentes com um único grant, recepção tardia,
  idempotência e cancelamento, reinício, autenticação, e API com nove nós
  registrados sem espera de outros candidatos. A consulta unificada foi
  verificada no SQL produzido pelo provedor Npgsql sem acessar produção.
- Android: 51 testes JVM de áudio, verificação de janela, candidatura e
  concessão passaram, usando OkHttp real em loopback, inclusive conexão
  reutilizada, tempos inválidos e compatibilidade. Cinco testes adicionais do
  verificador passaram com ONNX Runtime real e a coleção privada de teste.
- Contratos de aplicativo/distribuição: 96 testes passaram, inclusive o
  contrato Windows de não pausar mídia nem emitir sinal antes da concessão.
  A homologação física da nova fonte ainda não foi feita.

## Aceitação física

Comparar a mesma sequência de chamadas com um nó audível e com ambos em alcance.
Registrar mediana, pior caso, tempo da fronteira estimada até o aviso, espera no
árbitro, restante da requisição e ocorrências de respostas duplicadas. Separar
conexão fria de chamadas consecutivas. Não extrapolar o ganho de uma bancada
para o tempo ouvido pelo usuário ou para o consumo de bateria. Preservar a
capacidade de falar imediatamente após o sinal, sem perder o início do pedido.
