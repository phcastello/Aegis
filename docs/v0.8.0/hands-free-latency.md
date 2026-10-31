# Latência hands-free e ganho do aviso — 31/10/2026

A preparação de pedidos repetidos ficou mais rápida, e o aviso local ganhou
14,895 dB preservando o PCM aprovado. **A meta de quatro segundos não foi
atingida nem comprovada no Android.** A matriz completa de servidor não teve
redução consistente: variação do modelo e início do TTS continuam dominantes.
O TTS de produção não foi alterado; seu acesso administrativo está pendente.

## Fontes e infraestrutura

O trabalho começou no HEAD remoto atualizado de `feat/v0.8.0-hands-free`,
`3c04ea255ec9cfbb05124c5e830f9305fd0e3340`, na worktree
`/home/phc/Aegis-hands-free`. A worktree original e suas alterações foram
preservadas. `git diff f916703 3c04ea2 -- backend` estava vazio.

| Componente antes dos testes | Fonte/identidade observada |
| --- | --- |
| API implantada | `f9167035572e58d771b87672a13db828f90107d3`; imagem `sha256:cf05e0f53b1112cc6ffc93734efb927f37c33efe64279cabe0b3af7f8156ddd3`; label de versão legado `0.7.2` |
| PWA implantada | `fec7c18ea067e66ca6cf63d2e294e3d36191c44f`; imagem `sha256:caa387d38b9985a0df7d0af961125c50378c4c1737ab3936677dd5a2aa9829af` |
| Android histórico | `0.8.0-unstable.4`, código `8000004`, informação do proprietário |
| Baseline instrumentado | código de comportamento `f916703`, instrumentação `4b71ef8`; imagem `sha256:1a6fdaf636ded548bc764b818c3438924ee114f8d2e684007bb34d7628d72e63` |
| Probe streaming corrigida | `b8d403a`; imagem `sha256:e3ce676bcb43119435edab66e0a965b59e2415a5028c8d522ff8d678f1ef9947` |
| Backend otimizado medido | `e727e71`; imagem `sha256:fee2a4e696d8e508c342a3f655155c930ecb3485692b5ee3d82dfdb961b4e6c9` |
| Android candidato | `c055b2c`, incluindo ganho do aviso e marcos VAD → PCM |
| TTS disponível por HTTP | perfil `AegisVoicev1.0-Q00-W04-P07-M02`; modelo `/models/Qwen3-TTS-12Hz-0.6B-Base`, `max_model_len=4096` |

O JSON sanitizado registra os SHAs completos. Os primeiros JSON privados do
harness herdavam `sourceCommit=f916703` da instalação: isso identifica a base
de comportamento, não o patch de instrumentação. O relatório público distingue
`baselineBehaviorCommit` e `measuredSourceCommit`. A correção do profiling após
`yield` foi medida separadamente, sem reatribuir os números anteriores.

Configuração efetiva preservada: `gpt-6-luna`, reasoning `medium`, limite de
7.000 tokens, tier `auto`, `store=false`; STT ElevenLabs batch `scribe_v2`,
idioma `por`, fallback existente; embedding `text-embedding-3-large`, 1.536
dimensões. Memória semântica, grafo, escritas automáticas e contexto automático
estavam habilitados. O timeout automático era 2.500 ms, sem evidência de ser
consumido integralmente nos turnos medidos.

Host Aegis: VM Linux, Xeon E5-2640 v4 a 2,40 GHz, oito CPUs lógicas/quatro
cores, aproximadamente 20 GiB de RAM; Docker reportou limite de 7,5 GiB.
Não havia GPU disponível para estes experimentos locais. O conjunto temporário
consumiu aproximadamente API 268 MiB, PostgreSQL 65 MiB, Qdrant 209 MiB e
Neo4j 614 MiB em uma observação; isso não é um perfil de pico.

Endpoints usados: API real em `http://127.0.0.1:8090`, proxy habitual
`https://aegis.phcastello.com`, TTS privado `http://10.1.1.47:8001`, provedores
OpenAI e ElevenLabs já configurados. APIs de benchmark usaram portas efêmeras
somente em loopback. Configuração efetiva, imagens, endpoints e logs completos
sem exposição de credenciais ficaram privadamente em
`~/.local/state/aegis/latency-v080/`.

O clone TTS disponível era `2564fc7`. **Não foi possível comprovar o SHA,
imagem/digest, configuração de processo ou hardware do serviço TTS implantado**:
o SSH com a chave existente foi recusado. A imagem base fixada no repositório
não é apresentada como digest efetivamente implantado. A candidata TTS foi
enviada em `perf/v0.8.0-first-audio-latency`, SHA
`d3b4c076e1b7cb4e832febb7f24b6c9e55063539`, e exige reconciliação com a fonte
efetiva antes de qualquer deploy. Detalhes e rollback estão na
[documentação TTS](https://github.com/phcastello/aegis-tts/blob/perf/v0.8.0-first-audio-latency/docs/v0.8.0/first-audio-latency.md).

## Método e limites

O arquivo original `aegis-hands-free-metrics.json` não foi localizado nas áreas
inspecionadas. Os números fornecidos pelo proprietário foram preservados no
JSON: STT 7 amostras, média 1.406,57 ms; chat 6, média 4.409,83 ms; primeiro
áudio 6, média 1.937 ms. Os seis intervalos `processing → speaking` têm média
aproximada de 7,78 s. Não são sete trios pareados e não incluem endpointing.

O corpus fixo contém horário, matemática, capital do Japão, explicação em três
frases, pergunta com continuação e `place_list` somente de leitura. O fluxo usa
modelo, ferramentas, persistência e contrato de voz reais. Não há respostas
fixas nem endpoint de texto arbitrário para contornar mensagens persistidas.

Foram usados PostgreSQL/Qdrant/Neo4j descartáveis e API temporária, com os
provedores e configurações da instalação. Nenhum token Google, FCM, identidade
ou dado pessoal foi copiado para os testes. Recursos de memória permaneceram
habilitados. Baseline e otimizado começaram em bancos vazios separados; os
testes de memória sintética ocorreram depois da matriz de latência. Isso testa
o caminho real com contexto sintético, sem reproduzir tamanho ou conteúdo das
memórias do proprietário. Não foi criada instalação permanente nem outra GPU.

Cada duração usa relógio monotônico dentro do processo correspondente. Não se
subtraíram relógios entre máquinas. No cliente servidor de benchmark, primeiro
byte, primeiro PCM e chegada do bloco com `abs(PCM16)>=64` são marcos distintos.
Esse último é sinal digital, sem comprovação auditiva de um fonema. O probe
direto do TTS usou `abs(PCM16)>100`, declarado separadamente. Todos os áudios
eram respostas sintéticas de fala; não se antecipou silêncio ou preenchimento.

As chamadas foram sequenciais e limitadas. A fila estava vazia nas seis
sínteses diretas observadas. Não houve teste de carga ou concorrência artificial;
a infraestrutura TTS compartilhada não foi reservada exclusivamente. A primeira
síntese direta já encontrou readiness aquecida, após ociosidade desconhecida.
As primeiras chamadas de API descartável incluem inicialização/JIT; não são
chamadas frias do modelo TTS. Não houve reinício deliberado do TTS, keep-alive
artificial ou aquecimento periódico. Não se estimam percentis com este N.

## Antes/depois do caminho chat → primeiro sinal PCM

Tempos em ms, sem STT, endpointing, rede móvel ou alto-falante. Repetição 1:
primeira consulta daquele texto; repetição 2: mesmo texto com serviço aquecido.
No otimizado, a repetição 2 encontra o embedding exato em cache. As respostas
do modelo podem variar em tamanho, registrado no CSV.

| Caso | Repetição | Chat antes | Chat depois | TTS antes | TTS depois | Total antes | Total depois |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| horário, primeira API | 1 | 5.235 | 5.559 | 1.761 | 1.747 | 6.996 | 7.306 |
| matemática | 1 | 2.702 | 2.450 | 1.672 | 1.656 | 4.374 | 4.106 |
| factual | 1 | 2.468 | 2.247 | 1.974 | 1.907 | 4.443 | 4.154 |
| três frases | 1 | 3.288 | 2.925 | 1.708 | 1.664 | 4.996 | 4.589 |
| continuação | 1 | 2.542 | 3.012 | 1.641 | 1.665 | 4.183 | 4.677 |
| ferramenta de leitura | 1 | 3.595 | 3.904 | 1.660 | 1.975 | 5.255 | 5.879 |
| horário | 2 | 2.662 | 2.256 | 1.686 | 1.639 | 4.348 | 3.895 |
| matemática | 2 | 2.286 | 1.882 | 1.637 | 1.617 | 3.922 | 3.499 |
| factual | 2 | 1.678 | 1.656 | 1.919 | 1.901 | 3.598 | 3.557 |
| três frases | 2 | 2.934 | 3.150 | 1.690 | 1.653 | 4.625 | 4.803 |
| continuação | 2 | 2.303 | 2.923 | 1.609 | 1.658 | 3.912 | 4.581 |
| ferramenta de leitura | 2 | 3.327 | 3.482 | 1.672 | 1.926 | 4.998 | 5.408 |

| Recorte do total | N por configuração | Média antes/depois | Mediana antes/depois | Máximo antes/depois |
| --- | ---: | ---: | ---: | ---: |
| toda a matriz | 12 | 4.637 / 4.705 | 4.408 / 4.585 | 6.996 / 7.306 |
| excluindo somente primeira API | 11 | 4.423 / 4.468 | 4.374 / 4.581 | 5.255 / 5.879 |
| todos os repetidos | 6 | 4.234 / 4.291 | 4.135 / 4.238 | 4.998 / 5.408 |
| três simples repetidos | 3 | 3.956 / 3.650 | 3.922 / 3.557 | 4.348 / 3.895 |

Portanto há ganho observado nos simples repetidos, de aproximadamente 306 ms
na média, mas **não há ganho global comprovado nesta matriz**. Chamadas mais
lentas não foram descartadas. Uma primeira execução sem instrumentação, com a
imagem implantada, também está preservada: N=6, média 5.015 ms, mediana 4.738 ms,
máximo 7.042 ms. Não foi misturada com o baseline instrumentado para melhorar
o resultado.

## Atribuição das etapas e otimizações

| Etapa aquecida | Condição/N | Média | Mediana | Máximo |
| --- | --- | ---: | ---: | ---: |
| embedding antes | sem cache, 11 | 450,1 | 443,8 | 578,2 |
| preparação antes | 11 | 481,0 | 477,8 | 605,9 |
| embedding depois | miss, 5 | 430,5 | 427,7 | 528,4 |
| embedding depois | hit, 6 | 0,212 | 0,210 | 0,266 |
| memória automática depois | hit, 6 | 17,0 | 16,3 | 20,9 |
| preparação depois | hit, 6 | 28,9 | 28,0 | 34,0 |
| persistência antes | 11 | 51,3 | 37,2 | 183,7 |
| persistência depois | 11 | 32,8 | 32,6 | 41,7 |
| modelo + ferramentas antes | 11 | 2.146 | 1.990 | 3.088 |
| modelo + ferramentas depois | 11 | 2.427 | 2.404 | 3.523 |

O cache conserva somente vetores determinísticos de entradas exatas, tanto de
consultas quanto de projeção. São até 128 entradas, TTL monotônico de 15 minutos
desde inserção e LRU. URL, modelo, dimensão e hash do texto formam a chave;
vetores são copiados para evitar mutação. Falhas/cancelamentos não são
armazenados. Vetores podem codificar informação da entrada: ficam somente na
RAM limitada, sem persistência, texto original ou logs de chaves. Expiração é
verificada no acesso; entradas não acessadas podem permanecer até expulsão ou
reinício, sempre dentro do limite. Com 1.536 dimensões, dados dos vetores ocupam
aproximadamente 0,75 MiB, além de pequenos metadados/cópias temporárias.

Qdrant, PostgreSQL, busca lexical e grafo continuam executando e validando o
estado atual em cada busca. Horário, agenda, permissões, ações pendentes e
resultados de memória não são cacheados. Nos seis pedidos repetidos da matriz,
seis chamadas de embedding foram evitadas; entradas novas continuam pagando
seu custo. Não há novo timer nem tarefa de manutenção.

A persistência sem atividades agora faz três consultas em vez de sete para
carregar o resumo. `SaveChanges` mantém sua transação atômica; atividades reais
continuam usando a transação explícita necessária. Nenhuma escrita durável foi
deslocada para tarefa descartável. O teste PostgreSQL comprova o número de
consultas e os contratos de estado pendente/ausência de atividade.

As consultas pendentes aquecidas somavam aproximadamente 8 ms: abrir outro
scope/contexto de banco para sobrepô-las não justificaria complexidade. Não
houve paralelização de uma mesma instância EF. RuntimeContextProvider era local,
identidade já usava Lazy e conexões já usavam HttpClientFactory.

O payload inicial tinha cerca de 72,6 mil caracteres, aproximadamente 12,4 mil
tokens de entrada com 98% em cache. Não se encontrou duplicação inicial da
identidade nem falha geral do cache de prompt. Não se cortaram ferramentas ou
contexto para ganhar o benchmark. Pedidos simples tiveram uma rodada; a
consulta de lugares teve duas, com ferramenta de leitura concluída antes da
resposta. `hands_free_reply` encerrou sem rodada adicional.

Os probes `/stream` receberam somente a fala terminal validada ao final da
rodada. Com o profiling corrigido, primeira delta de argumentos → conclusão
foi 278 ms no horário e 816 ms nas três frases. Esses argumentos parciais não
eram fala validada. O ganho potencial para pedido curto não justificou alterar
persistência/elegibilidade da voz e introduzir um novo contrato de segmentos.
Não se fez uma simples troca de endpoint nem se expôs texto de outro turno.

## STT e pipeline completo executável no servidor

STT permanece batch. O WAV eSpeak sintético de 1.779 ms, PCM16 mono/16 kHz,
foi o mesmo em todas as chamadas. As três amostras isoladas antes foram
706,3 / 467,4 / 394,1 ms; depois, 834,4 / 1.647,2 / 531,2 ms. A média mudou
de 522,6 para 1.004,3 ms por variação externa; não houve otimização de STT.
O provedor registrou suas próprias durações. `stt.enter` ocorre depois do model
binding multipart; não representa o instante de chegada do primeiro byte HTTP.
Os marcos Android separam escrita de upload e espera sem misturar relógios.

O pipeline abaixo executou WAV → ElevenLabs → chat real → TTS real, medindo
do início da chamada de STT no servidor até a chegada do primeiro sinal PCM:

| Configuração | Repetição | STT | Chat | Pipeline até sinal PCM |
| --- | ---: | ---: | ---: | ---: |
| antes | 1 | 494,4 | 1.893,4 | 4.100,4 |
| antes | 2 | 596,5 | 4.207,9 | 6.354,7 |
| depois | 1 | 772,8 | 2.914,4 | 5.372,3 |
| depois | 2 | 697,7 | 2.489,6 | 4.899,2 |

A segunda transcrição anterior virou “Qodhas são” e gerou uma pergunta de
esclarecimento. Foi preservada como exceção de qualidade, sem descarte. As duas
posteriores transcreveram corretamente e responderam horário pelo contexto real.
Essas quatro amostras pequenas não sustentam ganho causal do pipeline completo.

**Todos os quatro casos ultrapassam quatro segundos antes mesmo de incluir o
VAD.** Os 800 ms aprovados de silêncio e a quantização dos blocos de até 200 ms
continuam parte do alvo. Somá-los como estimativa coloca os dois posteriores
por volta de 6,17–6,37 s e 5,70–5,90 s, ainda antes dos trechos Android/rede/
alto-falante não medidos. Isso é uma aproximação por configuração, não medição
física nem limite garantido: hangover, aquisição e escalonamento permanecem.

A preview exporta `vad_frame_to_first_useful_pcm_delivered_estimate` por turno,
incluindo endpointing e declarando `speakerAcousticsMeasured=false`.
[Marcos e limites Android](hands-free-client-latency.md) documentam primeiro
byte/PCM/sinal, entrega ao player, chamada de play e head drenado. A janela de
continuação segue começando após reprodução real, com dois segundos de início.

## TTS e regressão acústica

No corpus TTS isolado, os mesmos três textos completos foram usados em todas
as rotas, duas repetições cada. Os áudios brutos podem variar entre sínteses;
não se confundiu isso com comparação DSP no mesmo PCM.

| Rota | N | Primeiro PCM: média | Mediana | Máximo |
| --- | ---: | ---: | ---: | ---: |
| TTS privado direto | 6 | 1.620,6 | 1.612,9 | 1.657,5 |
| endpoint de voz Aegis | 6 | 1.643,8 | 1.637,7 | 1.683,0 |
| proxy habitual | 6 | 1.713,4 | 1.691,3 | 1.820,1 |

O proxy acrescentou cerca de 70 ms na média em relação à API, com variação;
não explica sozinho vários segundos. O maior tempo até sinal PCM não silencioso
do proxy foi 1.877,7 ms. As mensagens sintéticas exatas usadas para testar as rotas
foram persistidas temporariamente e removidas por IDs exclusivos, com verificação
de remoção. Não geraram extração de memória nem usaram mensagem pessoal.

O TTS emite PCM incremental antes de EOF. No código disponível, a primeira
janela DSP exige aproximadamente 1,75 s de áudio (target 1,25 + lookahead 0,20
+ busca 0,30), sem equivaler a 1,75 s de relógio. A finalização libera material
curto. Sem acesso ao processo não se atribui quanto dos 1,62 s observados
pertence ao raw do modelo, fila ou WORLD: a instrumentação antiga do caminho
nativo não contabilizava essas etapas corretamente.

A candidata TTS instrumenta o caminho nativo real, move DSP para um worker
sequencial e oferece a variante **desativada** `initial-quiet-900ms-v1`.
Ela pode decidir com 1,25 s de áudio apenas se encontrar fronteira quieta;
caso contrário, usa a janela estável. O default `stable` permanece. A variante
que forçava cortes menores foi rejeitada por acrescentar uma emenda de 10 ms.

Nos três PCM sintéticos eSpeak, o caminho estável foi bit a bit idêntico à base;
fragmentação, sample dividido, estado, finalização e cancelamento passaram.
Com WORLD real local, duas observações por modo reduziram gaps máximos do event
loop de 655/1.487 ms para 133/137 ms. Isso demonstra responsividade do worker,
sem provar ganho de primeiro áudio em produção. Não houve escuta humana,
captura do mesmo raw Qwen ou avaliação auditiva de consoantes/prosódia reais.
Golden regression comprova integridade do perfil, sem substituir essa avaliação.
Nenhuma variante acústica foi implantada ou promovida como equivalente.

## Aviso, custo e regressões

O ganho exclusivo do aviso passou de `0.18` para `1.0`, +14,895 dB. Frequências,
envelope, duração de 40 ms, intervalo e sequência permanecem byte a byte iguais.
Pico efetivo 899,46 → 4.997 PCM16; RMS 401,638 → 2.231,322; margem de pico
16,3 dB, sem clipping. O timeout agora verifica callback ou playback head;
cancelamento impede play tardio. [Evidência e testes do aviso](activation-cue-gain.md).
Volume global, TTS, canais de notificação, silencioso e Não Perturbe não mudaram.

As chamadas conhecidas dos experimentos somaram 53 HTTPs do modelo de chat,
44 de títulos, 13 de STT e 52 sínteses TTS, além de 46 jobs de extração de
memória criados nos bancos descartáveis. Os jobs/background existentes foram
mantidos na comparação; seus tokens e embeddings/projeções não foram todos
exportados. Não houve polling novo ou chamada periódica para aquecer modelos.

Uso disponível é **parcial**: 18 respostas auditadas antes somaram 223.871 tokens
de entrada (219.002 cached) e 1.250 de saída; 14 depois, 174.134 (170.354 cached)
e 916 de saída. Os conjuntos incluem probes diferentes e não devem ser usados
para declarar queda percentual de custo. Respostas privadas/redigidas e outros
provedores não forneceram todos os tokens ao relatório. Não houve valor monetário
reportado; não se inventou uma fatura. O cache evita custo de embedding em hits,
sem adicionar chamadas ao modelo.

Não foi medido consumo elétrico/bateria novo. A escuta passiva ganhou uma
leitura monotônica por bloco já capturado, normalmente cinco por segundo; não
ganhou inferência, conexão, timer, leitura de microfone ou varredura adicional.
CPU/GPU do TTS implantado continuam sem atribuição. Não houve GPU duplicada.

Verificações executadas:

- Backend completo: 763 passaram, 100 ignorados por fixtures externas/opt-in,
  zero falhas. Seleção com PostgreSQL descartável real: 52 passaram, zero skips.
- Android: Kotlin de produção compilado e 63 testes `NodeHandsFree*` passaram;
  20 testes host com JNI/libfvad real passaram. Incluem cue isolado em ganhos/
  alinhamentos variados e frase contínua sobreposta, sem perder samples ou mudar VAD.
- Node: 160 testes, build e dois contratos JS capability/IPC passaram.
- PWA: 91 testes e build passaram. Rust portátil: 90 passaram, dois ignorados;
  primeira tentativa com rustc 1.92 foi recusada, repetição com 1.94 passou.
- TTS candidato: 42 passaram, um GPU opt-in ignorado; perfil, léxico, corpus de
  pronúncia, golden integrity e Compose passaram. Sem avaliação auditiva Qwen.

Os contratos cobrem PCM antes de EOF, amostras divididas, buffers limitados,
backpressure, silêncio/áudio curto, cancelamento, ausência de áudio residual,
continuação após head e compatibilidade de chat manual/capability/configuração.
Windows físico, Samsung bloqueado, audibilidade/conforto e masking acústico
continuam dependentes de homologação no aparelho. Identidade, dados, modelo
privado, pareamento, wake word e preferências não foram alterados.

## Reprodução e publicação

Os comandos não imprimem credenciais. `prepare` herda somente as configurações
necessárias da API em memória e grava Compose **privado** com modo 0600,
removido pelo cleanup após encerrar os recursos.
Não versionar esse arquivo nem anexar diretórios privados aos relatórios.

```sh
python3 scripts/hands-free/benchmark-latency.py prepare --directory <diretorio-privado-novo>
python3 scripts/hands-free/benchmark-latency.py start --directory <diretorio> \
  --image <imagem-experimental> --source-commit <SHA-exato>
python3 scripts/hands-free/benchmark-latency.py measure --directory <diretorio> \
  --label <antes-ou-depois> --repetitions 2 --stt
python3 scripts/hands-free/benchmark-turn-probes.py full --directory <diretorio> --label <rotulo>
python3 scripts/hands-free/benchmark-turn-probes.py stream --directory <diretorio> --label <rotulo>
python3 scripts/hands-free/benchmark-turn-probes.py quality --directory <diretorio> --label <rotulo>
python3 scripts/hands-free/parse-latency-profile.py <log-privado-da-api> > <CSV-sanitizado>
python3 scripts/hands-free/benchmark-latency.py cleanup --directory <diretorio>
```

O WAV sintético fica em `continuous.wav` no diretório pai do experimento. Foi
gerado por `espeak-ng -v pt-br -s 165` e reamostrado a 16 kHz PCM16, sem dados
do proprietário. As rotas fixas foram medidas com o helper privado preservado
`~/.local/state/aegis/latency-v080/bench_voice_routes.py baseline`. O probe direto
e o replay do mesmo PCM estão documentados e versionados na branch TTS.

Dados sanitizados completos: [JSON](hands-free-latency-results.json),
[amostras antes/depois](hands-free-latency-samples.csv) e
[marcos por turno](hands-free-latency-stages.csv). Áudio, raw Qwen/referência,
logs completos, credenciais e identificadores privados permanecem fora do Git.

A publicação usa `publish-dev`, sem merge na main ou release estável. Não há
migration nesta alteração. O estado efetivo da publicação, seus SHAs, versão,
checks de identidade e rollback será registrado após o fluxo existente terminar.
O TTS não é implantado por esse fluxo e permanece explicitamente separado.
