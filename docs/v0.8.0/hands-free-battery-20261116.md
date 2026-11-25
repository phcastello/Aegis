# Aegis v0.8.0 — eficiência da escuta e diagnóstico de bateria

Correções de 16/11/2026 na branch
`fix/v0.8.0-wakeword-bateria`, derivada de `e13c6cd`.
A escuta permanece contínua enquanto habilitada pelo proprietário. Não foram
adicionados desligamento automático por bateria, pausas periódicas do microfone
ou descarte de blocos para economizar inferências.

## O que os dados permitem concluir

A exportação de 16/11 da `0.8.0-unstable.15` registra, no período retido:

- 316,14 horas de escuta solicitada;
- 312,40 horas de captura confirmada e 311,12 horas de detecção confirmada;
- 2,26 horas de wake lock explícito de sessão, cerca de 0,72% da captura;
- 399 falhas de captura e aproximadamente 98,9 minutos no estado de microfone
  indisponível.

Esses totais não demonstram vazamento do wake lock explícito da Aegis. O código
o adquire depois de uma ativação, com limite de dez minutos, e o libera ao
encerrar a sessão. A captura do Android pode sustentar outros recursos de áudio
e de sistema; isso não está medido pelo contador de wake lock da aplicação.

O histórico recente conserva somente 128 eventos. Ele mostra mudanças de modo
de áudio, uma ocorrência de microfone silenciado e alterações de rota, mas não
preserva a causa individual das 399 falhas. Não há evidência suficiente para
atribuir todas elas a uma única falha ou a um ciclo de retentativas. O controlador
já limita as tentativas após uma falha e usa eventos do sistema para retomar.

A piora de autonomia observada anteriormente compara consumo total do aparelho
antes e depois da implementação. Ela merece investigação, mas não permite
atribuir todo o aumento ao detector ou ao código Kotlin.

## Trabalho repetido removido

### Buffer circular de áudio

`HandsFreeRing.append` fazia uma operação de módulo e uma atualização do tamanho
para cada amostra, inclusive durante toda a escuta passiva. Agora o mesmo bloco
entra em até duas cópias contíguas. Blocos maiores que a capacidade preservam
exatamente o sufixo que caberia no buffer.

Os testes verificam o conteúdo depois de blocos parciais, voltas completas,
sobrescrita, blocos maiores que a capacidade e limpeza. As fronteiras de ativação
e o áudio enviado ao comando continuam com seus contratos existentes.

### Agregação das métricas de captura

Cada bloco atualizava de dois a cinco contadores por chamadas separadas, repetindo
a consulta ao relógio, ao dia e à identidade do modelo. Agora a captura resolve
essas referências uma vez e atualiza os mesmos contadores.

Uma observação permanece inteira no mesmo dia e identidade, inclusive na virada
da meia-noite. A persistência e as identidades históricas são preservadas.

## Medições de CPU das rotinas

Foram compiladas as fontes reais anteriores e candidatas em JVM no servidor 22,
com o mesmo JDK 25, Kotlin 2.3.21 e condições por comparação. O áudio usado no
microbenchmark é sintético e idêntico entre variantes.

| Rotina | Método | Mediana anterior | Mediana candidata |
| --- | --- | ---: | ---: |
| Buffer circular | Cinco rodadas de 50.000 blocos de 3.200 amostras | 1.365,66 ms de CPU | 19,07 ms de CPU |
| Métricas de captura | Dois processos por variante, ordem invertida e cinco rodadas de 500.000 chamadas por processo | 366,94 ms de CPU | 241,07 ms de CPU |

A rotina do buffer usou aproximadamente 71,6 vezes menos CPU nessa execução.
A rotina de métricas usou aproximadamente 34,3% menos CPU. Os checksums
correspondentes foram iguais.

Esses fatores não são reduções de consumo de bateria. Em termos absolutos no
host, o buffer passou de aproximadamente 27,31 para 0,381 microssegundo por
bloco; as métricas passaram de 0,734 para 0,482 microssegundo por chamada.
São otimizações de trabalho repetido, cujo peso no aparelho precisa ser medido
junto da captura e da inferência.

As medições de métricas usam `-Xbatch -XX:-TieredCompilation` para reduzir a
variação de aquecimento; permanecem observações de JVM no host, sem execução de
microfone Android. As evidências privadas ficam em:

`/var/tmp/aegis-wakeword-battery-20261116-toolchain/battery-microbench/`

- `ring-results.json`: fontes anterior/candidata e cinco rodadas;
- `metrics-results-stable.json`: duas execuções por variante em ordem invertida;
- `core-contracts-test.log`: 59 testes de áudio, métricas, modelo e bundle,
  com os dados privados presentes e os validadores nativos injetados.

Esses 59 testes não certificam ONNX, microfone ou energia no Android. A validação
integrada do aplicativo é registrada no relatório geral da correção.

## Medição do detector nativo no host

Foi reutilizada a biblioteca de teste nativa TFLM já disponível no servidor 22,
sem recompilar ou alterar produção. A fonte C++ de referência registrada no
build tem SHA-256
`d960305b50de5e2b1b102a1cd724d8b75ec477fb8980641dd2842583a9d57e4a`,
igual à fonte atual do aplicativo. A cópia de teste acrescenta somente um
limiar configurável; o teste preservou o valor de 0,5 usado no aplicativo,
o frontend e a inferência.

Cada modelo foi medido em três repetições alternadas de 60 segundos de áudio
por condição, com chamadas de 3.200 amostras, como no controlador Android.
Foram usados silêncio, ruído branco determinístico e os primeiros 30 segundos
do corpus público de fala repetidos duas vezes. Houve aquecimento de três
segundos e reset do detector antes de cada condição. A leitura dos dados, a
inicialização e o reset ficaram fora do intervalo medido.

| Modelo | Silêncio | Ruído | Fala pública |
| --- | ---: | ---: | ---: |
| Baseline | 4,307 ms CPU/s áudio | 4,932 ms CPU/s áudio | 4,328 ms CPU/s áudio |
| Strong | 4,546 ms CPU/s áudio | 4,706 ms CPU/s áudio | 4,415 ms CPU/s áudio |

Os valores são medianas de CPU do thread. A CPU total do processo coincidiu
praticamente com a do thread. O fator de tempo real do Strong ficou entre
0,00444 e 0,00473 nas condições medidas. Os tempos incluem a pequena chamada
Python/ctypes ao C++, mas não incluem JNI, `AudioRecord`, serviço de áudio do
Android ou o verificador ONNX.

Na cadência de cinco chamadas por segundo de áudio, a economia absoluta das
duas rotinas Kotlin medida anteriormente equivale a cerca de 0,136 ms CPU/s
áudio no host. Essa comparação de escala mostra que as cópias e métricas têm
peso pequeno perto dos aproximadamente 4,4–4,7 ms CPU/s do detector nativo
nessa máquina. As execuções JVM e C++ são medições separadas; a comparação não
atribui percentuais do aplicativo nem estima autonomia.

Não foi encontrada uma diferença consistente de custo relevante entre os
pesos Baseline e Strong nesses testes breves. Isso não certifica consumo no
telefone: o servidor usa x86_64 e kernels genéricos, enquanto o aparelho usa
ARM64, outro escalonamento, clocks e recursos de áudio. Não havia telefone
conectado ao ADB para obter medição física nesta correção.

Os resultados completos, hashes dos modelos/biblioteca e condições estão no
arquivo privado `native-detector-results.json`, no diretório de evidências
acima. A conclusão permanece: foram removidos custos evitáveis e acrescentado
diagnóstico para a próxima exportação; a causa dominante e a melhora de
autonomia ainda precisam ser demonstradas no aparelho.

## Diagnóstico cotidiano sem profiling permanente

O runtime mede um bloco do detector a cada cinco segundos de captura passiva,
sem mudar a execução dos outros blocos. A amostra envolve a chamada nativa de
frontend e inferência, no próprio thread de captura.

A exportação inclui os agregados diários e por identidade:

| Campo | Significado |
| --- | --- |
| `detectorProfileSamples` | Quantidade de blocos medidos |
| `detectorProfileInputSamples` | Quantidade de amostras PCM nos blocos medidos |
| `detectorProfileWallNanos` | Tempo decorrido nas chamadas medidas |
| `detectorProfileThreadCpuNanos` | Tempo de CPU do thread nessas chamadas |

A configuração identifica o formato como
`detectorProfiling=thread-cpu-sample-5s-v1` e registra
`detectorProfileIntervalMs=5000`. Isso cria uma identidade de observação nova
sem reatribuir os totais anteriores. Relógios indisponíveis ou valores negativos
não produzem uma medição válida e não interrompem a captura.

Os contadores não representam CPU de todo o aplicativo, energia elétrica,
consumo do AudioFlinger ou recursos mantidos pelo driver. Não devem ser
extrapolados para porcentagem de bateria sem uma medição do aparelho. O
verificador adicional de wake word, acionado somente após um candidato, também
não está incluído nesse tempo do detector principal.

## Como avaliar o resultado no telefone

1. Usar a versão corrigida normalmente, preservando a disponibilidade desejada
   da escuta. Registrar a versão e o modelo efetivamente instalados.
2. Comparar dias completos com o histórico de bateria, levando em conta tela,
   carregamentos, condições de rede e mudanças relevantes de uso.
3. Exportar as métricas pelo fluxo normal do aplicativo. Conferir captura e
   detecção confirmadas, falhas, ativações e tempo de sessão junto do consumo.
4. Usar os novos contadores para avaliar quanto trabalho o detector principal
   exige e se as chamadas permanecem reconhecidas. Menos consumo com escuta
   indisponível não é ganho de eficiência.
5. Se o custo continuar alto, separar em uma investigação breve o consumo da
   captura do Android e o da inferência. Não manter ADB ou traces permanentes
   durante a comparação naturalística.

Reduzir falsas ativações pode diminuir também o trabalho de captura de comandos,
rede e reprodução provocado por enganos. Esse possível ganho deve ser medido
depois da mudança; não é uma economia de bateria já demonstrada.
