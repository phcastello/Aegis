# Aegis v0.8.0 — compromisso entre chamadas e falsas ativações

Rodada local de 05/11/2026, branch `feat/v0.8.0-wakeword-tradeoff`, worktree
`/home/phc/Aegis-wakeword-tradeoff`, base `9a688d7`. Os experimentos anteriores,
dados, branches paralelas e produção foram preservados.

## Política e protocolo

A seleção anterior exigia zero perdas de chamadas antes de considerar a rejeição
de negativos. Essa prioridade foi substituída por diferenças **pareadas por
evento** contra o baseline:

`utility = FP_eliminados − FP_novos − 4 × positivos_perdidos + 4 × positivos_recuperados`.

O custo quatro é uma preferência de engenharia explícita. Não é uma equivalência
científica. Positivos contam reconhecimento perto da decisão original
(−500/+200 ms); um negativo só é rejeitado se não houver evento **em qualquer
posição** do contexto. Contagens perto da decisão também são preservadas.
Recuperações e novos falsos contextos são contados separadamente, sem inferi-los
de percentuais agregados.

O plano foi registrado em `round-plan-v1.json` antes de consultar os resultados
desta rodada. Ordem: maior utilidade LiteRT; empate por maior utilidade mínima
entre runtimes; persistindo empate, preferir o forte sem novo treinamento,
threshold 0,5 ou mais próximo dele e época mais cedo. BCE é diagnóstico.
Corrupção, falha de carregamento, estado contaminado e regressão técnica grave
continuam sendo impeditivos; perder uma chamada isolada não é um veto.

Reutilizaram-se a preparação e os arrays da rodada humana: treino 91 positivos e
130 negativos, validação 11/15, teste 5/8. Não houve alteração de splits,
reconstrução de features, filtros acústicos/ASR/DTW ou descarte de rótulos.
Todos os 221 originais foram apresentados em cada época do novo fit.

## Recuperação e calibração

O forte foi recuperado de
`~/.cache/aegis-wakeword-human-label-training/model-v1/epoch-02/`, conferindo
`training.json`, `export.json`, o TFLite e os pesos float. Não foi substituído
pelo conservador. O snapshot privado `strong-epoch02-snapshot-v1` tem arquivos
somente leitura, hash e procedência; o original continua intacto.

| Artefato | SHA-256 |
| --- | --- |
| Baseline INT8 | `f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514` |
| Forte época 2 INT8 | `1e7c686420f65d8da892f02c71d945f03a8dd528a1005de8c9c8482dce52ad2e` |
| Forte época 2 float | `e3af8c9157411b689fd4d9273dde845d9d158a07a3619349dacd8628df47b4d4` |
| Conservador anterior INT8 | `c05d071f33465397d50216c6f1ab9af7a70b6f303ca689934239cae4be410567` |

O replay reproduziu **10/11 positivos e 7/15 FP** no forte a 0,5, nos dois
runtimes: seis falsos contextos eliminados, zero novos, uma chamada perdida,
zero recuperadas, utilidade **+2**. O baseline reproduziu 11/11 e 13/15 FP.
Isso é uma redução pareada de 6/13 (46,15%) nos contextos ativados pelo baseline.

Grid predefinido de apenas sete thresholds; média de cinco scores, latch,
liberação abaixo de 0,3, refratário de 16.000 amostras, frontend e rede em estado
streaming, reset por contexto e nenhum silêncio artificial ao fim. A avaliação
usou o detector completo, não comparação de máximos com thresholds.

| Threshold forte | Positivos LiteRT/nativo /11 | FP LiteRT/nativo /15 | Utility LiteRT/nativo |
| --- | ---: | ---: | ---: |
| 0,40 | 10/10 | 9/9 | 0/0 |
| 0,45 | 10/10 | 9/9 | 0/0 |
| **0,50** | **10/10** | **7/7** | **+2/+2** |
| 0,55 | 10/9 | 7/7 | +2/−2 |
| 0,60 | 9/9 | 7/7 | −2/−2 |
| 0,65 | 8/8 | 7/6 | −6/−5 |
| 0,70 | 8/8 | 6/6 | −5/−5 |

Threshold menor não recuperou a chamada perdida e devolveu dois falsos contextos.
0,55 não melhora o LiteRT e perde outra chamada no nativo. **Mantém-se 0,5**;
nenhuma variante calibrada adicional é levada ao teste como vencedora.

O adapter Python passou a aceitar threshold e liberação opcionais, preservando
os defaults anteriores. Isso permite variar somente o threshold, mantendo
liberação em 0,3. O núcleo nativo fixa o threshold em 0,5: para o grid foi
compilada uma **cópia experimental privada** com campo por instância, comparação
parametrizada e setter. Os objetos existentes de frontend/TFLM foram reutilizados.
Nenhum arquivo nativo de produção foi editado. A cópia a 0,5 foi comparada à
biblioteca intocada nos 26 contextos da validação: scores e eventos exatamente
iguais, incluindo verificações de reset. Isso não demonstra paridade ARM64.

## Fine-tuning direcionado

Uma configuração, três épocas, LR `2e-6`, seed `20261105`, Adam novo, quatro
BatchNormalizations congeladas, inicialização nos **pesos float do forte época
2**. Arquitetura MixConv, frontend, export streaming e calibração INT8 herdada
permaneceram. O manifesto de linhagem liga hashes float, INT8, export, treino
pai, protocolo e features. O export ocorre em subprocesso limpo por época.

Cada época tem 32 minibatches de 64: 12 positivos sintéticos, quatro referências,
16 positivos reais, 16 negativos sintéticos, oito FLEURS DEV e oito negativos
reais. São 32/32 por classe. A única mudança de proporção em relação ao forte é
substituir oito sintéticos positivos por oito reais positivos. IDs originais são
percorridos antes de escolher a derivação de ganho: 512 apresentações positivas
reais (5–6 por evento) e 256 negativas reais (1–2), cobrindo 91/130 em toda época.
Não se confunde repetição ou ganho com novos exemplos independentes.

| Época C | Positivos LiteRT/nativo /11 | FP LiteRT/nativo /15 | Utility LiteRT/nativo |
| --- | ---: | ---: | ---: |
| **1, melhor do fit** | **10/10** | **8/8** | **+1/+1** |
| 2 | 10/10 | 8/8 | +1/+1 |
| 3 | 10/10 | 8/7 | +1/+2 |

Nenhuma época recuperou a chamada perdida pelo forte. As duas primeiras
devolveram um FP nos dois runtimes; a terceira o devolveu somente no LiteRT.
O desempate previamente definido escolheu C/1. Seu SHA-256 é
`7208f3e7e0deaf692180110b7979da7b54d8d5de343753fb1088706500e3d14c`.
Não há justificativa técnica independente para um segundo fit de camadas finais;
a configuração opcional não foi executada. Não se fez busca indiscriminada.

`/home` atingiu ENOSPC somente ao gravar o manifesto final do fit, depois de
salvar todas as épocas, exports, avaliações e `history.json`. Os manifestos foram
concluídos a partir desses arquivos e hashes, **sem repetir gradientes**. O tempo
total exato ficou indisponível e não foi inventado. A fonte efetivamente executada
foi preservada antes de endurecer guardas de IDs duplicados/rótulos/splits.
Para liberar espaço, apenas o cache de downloads pip foi copiado para
`/tmp/aegis-pip-download-cache-preserved-20261105`, com 818 hashes verificados e
symlink no caminho anterior; ambientes instalados e todos os artefatos de voz
permaneceram no armazenamento privado persistente em `/home`.

## Seleção, teste e decisão

**GO PARA PILOTO restrito, comparativo e reversível do forte época 2 a 0,5.
Produção não aprovada. O ganho na experiência real ainda não foi demonstrado.**
O teste final foi desfavorável segundo a própria função de custo: reconheceu
3/5 positivos, contra 5/5 do baseline, eliminando três FP no LiteRT e dois no
nativo. A utilidade foi **−5/−6** nesse split. Esse resultado limita a confiança
no ganho observado na validação e impede recomendar substituição diária sem
acompanhamento. Não é descartado automaticamente apenas por perder chamadas.

A escolha foi selada às `20:25:16 UTC`, antes do lançamento do replay final,
com cópias somente leitura dos quatro modelos, pesos e configurações em
`frozen-v1/`. A melhor época C tinha sido escolhida por validação. Apesar de C
ter depois rejeitado mais negativos de teste, **não foi promovida pelo teste**.
Não houve novo fit, calibração, troca de época ou configuração após a seleção.

A fronteira de Pareto contém os pontos `(positivos, FP)` **(11,13), (10,7),
(8,6)** nos dois runtimes. O ponto (8,6) economiza somente um FP adicional ao
custo de duas chamadas frente a (10,7), portanto perde oito unidades de benefício
de chamadas para ganhar uma de FP. No LiteRT, 0,55 empata em (10,7), mas piora
o nativo; C/3 empata com o forte apenas no nativo. O JSON registra os nomes de
todas as configurações não dominadas, incluindo os checkpoints fortes anteriores.
Estar na fronteira não significa ser a melhor escolha para o custo quatro.

### Comparação final por split e runtime

Cada célula apresenta **LiteRT/nativo**. Positivos são reconhecidos perto da
decisão; negativos são rejeitados no contexto inteiro. Todos os conjuntos já
foram examinados em rodadas anteriores: o teste atual é exploratório conhecido,
não avaliação cega inédita. Treino mede ajuste.

| Split e métrica | Baseline | Forte época 2 | C época 1 | Conservador anterior |
| --- | ---: | ---: | ---: | ---: |
| Treino positivos /91 | 91/91 | 84/84 | 84/84 | 90/90 |
| Treino negativos rejeitados /130 | 6/6 | 77/78 | 80/79 | 16/15 |
| Validação positivos /11 | 11/11 | 10/10 | 10/10 | 11/11 |
| Validação negativos rejeitados /15 | 2/2 | 8/8 | 7/7 | 2/2 |
| Teste positivos /5 | 5/5 | **3/3** | 3/3 | 5/5 |
| Teste negativos rejeitados /8 | 0/0 | **3/2** | 4/4 | 0/0 |

| Utility relativa ao baseline | Baseline | Forte época 2 | C época 1 | Conservador |
| --- | ---: | ---: | ---: | ---: |
| Treino, diagnóstico LiteRT/nativo | 0/0 | +43/+44 | +46/+45 | +6/+5 |
| **Validação, seleção LiteRT/nativo** | 0/0 | **+2/+2** | +1/+1 | 0/0 |
| Teste, avaliação LiteRT/nativo | 0/0 | **−5/−6** | −4/−4 | 0/0 |

No teste do forte, as mesmas duas chamadas foram perdidas nos dois runtimes;
nenhuma foi recuperada. Não houve falso contexto introduzido. Os oito negativos
do baseline produziram nove eventos; o forte produziu cinco/seis eventos em
cinco/seis contextos, sem evento adicional fora da margem da decisão. O
conservador manteve oito contextos, com oito eventos: reduzir um evento repetido
não equivale a rejeitar um contexto falso.

Aliases neutros preservam a análise individual sem publicar IDs ou conversas:

| Mudança do forte | Aliases |
| --- | --- |
| Chamada perdida na validação | E101 |
| FP eliminados na validação, ambos runtimes | E079, E111, E132, E137, E142, E154 |
| Chamadas perdidas no teste, ambos runtimes | E045, E210 |
| FP eliminados no teste, LiteRT | E070, E080, E146 |
| FP eliminados no teste, nativo | E080, E146 |
| FP novos / chamadas recuperadas, validação ou teste | nenhum |

E101 teve máximo da média móvel 0,185/0,197, compatível com não ser recuperada
pelo grid 0,40–0,70. E045 teve 0,127/0,159 e E210 0,142/0,130. Esses máximos
descrevem as falhas; não substituíram o mecanismo real na calibração. Os pareados
completos de treino e demais diferenças estão no JSON por aliases; a
correspondência com os IDs reais fica somente no resumo privado.

### Regressões conhecidas e execução contínua

| Métrica LiteRT/nativo | Baseline | Forte época 2 | C época 1 | Conservador |
| --- | ---: | ---: | ---: | ---: |
| Antigos positivos reconhecidos /15 | 15/15 | **12/12** | 12/12 | 15/15 |
| Antigos negativos ativados /64 | 58/58 | **26/26** | 26/26 | 51/52 |
| PoC isolada positivos /23 | 23/23 | **22/22** | 22/23 | 23/23 |
| PoC isolada negativos ativados /8 | 1/1 | 0/0 | 0/0 | 1/1 |
| PoC contínua positivos /23 | 23/23 | **23/22** | 23/23 | 23/23 |
| PoC contínua negativos ativados /8 | 1/1 | 1/1 | 1/1 | 1/1 |
| Eventos no público completo /35 min | 4/4 | 3/3 | 3/3 | 4/4 |

Nos 64 negativos antigos, o forte eliminou **32** dos 58 falsos contextos
reproduzidos (55,17%), sem contexto novo. Perdeu três dos 15 positivos antigos.
Esses conjuntos **se sobrepõem ao treino atual**: 55/64 negativos antigos estão
no treino, oito na validação e um no teste; dos 15 positivos, 12 estão no treino,
dois na validação e um no teste. Os resultados não são holdouts adicionais nem
contraprova independente às perdas do teste atual. Não somar esses grupos aos
260 eventos como se fossem gravações diferentes.

A PoC preserva cauda de 0,5 s nos arquivos isolados; no contínuo, prefixo
público de 30 s, três segundos entre tentativas e nenhum reset intermediário.
O forte reconheceu uma chamada a mais no contínuo LiteRT do que isoladamente;
o nativo continuou em 22/23. Estado e kernels influenciam decisões perto do limite.

Também se processaram os 260 contextos em ordem temporal, com três segundos
artificiais de silêncio entre eles e um único reset inicial. Baseline reconheceu
105/107 chamadas e ativou 132/131 negativos; forte reconheceu 99/107 e ativou
67/68. Pareadamente, foram seis chamadas adicionais perdidas e 65/63 falsos
contextos eliminados, zero recuperações e zero novos FP; utilidade +41/+39.
C ficou em 99/98 chamadas e 66/65 FP; conservador em 104/104 e 122/124 FP.
Nenhum evento surgiu nos intervalos. Essa sequência é um stress de estado com
junções artificiais, não reconstrução das conversas nem exposição para FP/hora.

### Compatibilidade, latência e custo

Todos os modelos mantêm 39.552 bytes, 13.057 parâmetros, entrada INT8 `[1,3,40]`
(escala `0.10196078568696976`, zero point −128), saída UINT8 `[1,1]`
(escala `1/256`, zero point 0), sem tensores float. Carregaram no núcleo nativo.
Reset depois de estado sujo e resets repetidos deram scores/eventos exatamente
iguais a instâncias limpas, para cada modelo/runtime. Silêncio de 30 s e ruídos
de 30 s em RMS 100/1000 produziram zero eventos.

| Divergência nos 260 contextos | Baseline | Forte | C | Conservador |
| --- | ---: | ---: | ---: | ---: |
| Maior diferença de score, LSB | 21 | 21 | 21 | 22 |
| Contextos acima de 1 LSB | 215 | 221 | 220 | 220 |
| Contextos com ativação divergente | 0 | 2 | 1 | 5 |
| Contextos com eventos fora de um stride/contagem distinta | 0 | 2 | 1 | 5 |

No forte, as diferenças de evento são E068 (treino) e E070 (teste). A tolerância
de 1 LSB permaneceu inalterada e falha inclusive no baseline. Carregamento
não comprova paridade numérica. A biblioteca parametrizada privada foi usada no
replay final a 0,5; sua equivalência à intocada foi verificada na validação,
não se alega execução ARM64 ou identidade bit a bit entre kernels.

Nas chamadas comuns de teste, a decisão do forte atrasou mediana de **30 ms**
em ambos runtimes (intervalo 0–30 ms). Na validação, mediana 30/45 ms e máximo
120 ms. São diferenças frente à decisão do detector original, sem anotação
do fim fonético da palavra. Não são latência acústica absoluta e excluem
chamadas perdidas, que continuam contabilizadas.

O primeiro controle LiteRT mediu fator tempo de processamento/tempo de áudio
0,01079 no forte versus 0,00490 no baseline; no nativo, 0,00524 versus 0,00538.
A medição inicial foi preservada. Um diagnóstico com três repetições alternadas,
mesmo ruído e pesos, sem outros replays simultâneos, encontrou LiteRT forte
0,00511–0,00687, baseline 0,00507–0,00854; nativo forte 0,00562–0,00700,
baseline 0,00604–0,00712. Não sustenta regressão estável de custo nem ganho de
desempenho. RSS do processo compartilhado não isola arena por modelo, memória
ARM64 ou consumo de bateria. Nenhum peso/configuração foi alterado pelo diagnóstico.

## Homologação, riscos e questionamentos

O forte merece **homologação monitorada contra o baseline**, pois a utilidade
positiva na validação e a rejeição de FP justificam investigar o compromisso.
O teste atual negativo e a perda em PoC/old15 impedem afirmar que ele já é melhor
na experiência diária. Produção exige evidência física e prospectiva favorável;
se ela não confirmar o compromisso, retornar ao baseline. Não há autorização
de substituição automática por esta decisão.

Uma perda em 11 observações não estima uma taxa populacional de falha de 9,1%;
6/13 supressões não estima redução de FP/hora. Positivos são chamadas que o
baseline já registrou; faltam tentativas nunca reconhecidas. Negativos também
foram coletados por ativação, sem denominador de exposição. Sessões curtas,
correlação acústica, repetição de validação e consultas anteriores limitam a
generalização. A ponderação quatro precisa ser reavaliada com uso prospectivo,
não ajustada para tornar esta rodada favorável.

Questionamentos de código permanecem documentados: export depende de nomes
canônicos e da guarda literal de tamanho; calibração INT8 herdada usa as primeiras
500 janelas; kernels/frontend divergem além de 1 LSB; `process` nativo retorna
no máximo um evento por chamada, por isso o harness usa blocos de 160 amostras;
o default Python associa histerese ao threshold e precisou de parâmetro explícito
para isolar o experimento. As guardas novas impedem parear IDs duplicados ou
rótulos/splits alterados silenciosamente. Nenhum desses pontos foi ocultado
por BCE ou tolerâncias ampliadas.

Não foi encontrado ADB no PATH nem nos locais pesquisados, e nenhum aparelho
Android ARM64 acessível foi estabelecido. **Homologação física pendente.** O
seletor atual permite somente o SHA do baseline e threshold fixo 0,5, portanto
recusará o candidato. O pacote explica a necessidade de harness/build privado
separado para homologação; não promete importação direta no app atual e não
contorna a allowlist. Esse preparo não altera o aplicativo de produção.

## Artefatos, reprodução e verificações

Raiz privada persistente: `~/.cache/aegis-wakeword-tradeoff/`. Conteúdo pessoal,
áudio, features, IDs reais, scores por frame, pesos float e INT8 ficam fora do Git.
Os relatórios versionados contêm apenas métricas, aliases e hashes de procedência.

| Artefato privado | Caminho relativo à raiz |
| --- | --- |
| Planos anteriores à avaliação/gradiente | `round-plan-v1.json`, `threshold-plan-v1.json`, `finetune-v1/plan.json` |
| Snapshot forte recuperado | `strong-epoch02-snapshot-v1/` |
| Checkpoints C e histórico completo | `finetune-v1/epoch-{01,02,03}/`, `training.json` |
| Seleção e modelos congelados | `selection-v1.json`, `frozen-v1/{baseline,strong,finetune,conservative}/` |
| Replays finais e scores | `eval-*-final/` |
| Controles/contínuo e diagnóstico de custo | `controls-v1/`, `resource-diagnostic-v1.json` |
| Pareados brutos e mapa de aliases | `summary-v1/private_summary.json` |
| Resumo final seguro e decisão | `summary-final-v1.json`, `final-review-v1.json` |
| Pacote de piloto com rollback | `piloto-v1/`, `piloto-v1.zip` |

O pacote privado contém candidata, baseline para rollback, `SHA256SUMS.txt`,
manifesto de contrato/threshold, métricas e instruções de importação/homologação
e rollback. O hash do ZIP está no JSON versionado e em `piloto-v1-package.json`.
Sem áudio, features ou checkpoints float. Os hashes dos payloads e CRC do ZIP
são verificados. Diretórios privados novos são 0700 e arquivos 0600, com
snapshots congelados ainda mais restritos (0500/0400).

Reprodução usa o ambiente existente
`~/.cache/aegis-wakeword-poc/comparison/microwakeword/venv/bin/python`.
Não houve instalação de bibliotecas. Usar sempre destinos novos; nunca substituir
os relatórios selados:

1. `tradeoff_eval.py grid`: recupera snapshot, compila cópia privada e reproduz
   baseline/forte/grid somente na validação.
2. `train_tradeoff.py --prepared <preparation-v1> --baseline-evaluation
   <validation-baseline-v1/evaluation.json> --output <nova-raiz>/finetune-v1`:
   um fit curto a partir do forte, com export/validação por época.
3. `seal_tradeoff.py` e `summarize_validation_tradeoff.py`: conferem procedência,
   utilidade e fronteira, congelam escolha antes dos testes. Os helpers desta
   rodada usam a raiz privada predefinida; para outra rodada, configurar raiz
   e caminhos em uma cópia preservada, sem sobrescrever a existente.
4. `final_tradeoff.py --root <raiz>`: todos os replays finais e controles,
   somente depois do selo. `summarize_tradeoff.py --root <raiz> --output
   <novo-resumo>`: agregados seguros e pareados privados. A revisão final foi
   preservada em `finalize-review-v1.py`, sem reescolher candidata.
5. `package_tradeoff.py --summary <summary-final-v1.json> --output
   <novo-pacote>`: exige decisão de piloto, verifica contrato real e hashes.

Verificações: **53 testes unitários passaram**, incluindo 20 da política/sampling/
streaming desta rodada; compilação Python, `git diff --check`, sete thresholds,
três exports/validações C, quatro replays finais nos dois runtimes, controles
de reset/silêncio/ruído/contínuo e diagnóstico alternado de custo. Relatórios
seguros foram inspecionados contra todos os 260 IDs privados e campos sensíveis.
Nenhum teste Linux foi apresentado como homologação física. Testes de backend,
TTS e APK não se aplicam porque nenhum código desses componentes mudou.

Branch preservada com Conventional Commits; hashes dos commits e estado de push
são informados no relatório de conclusão. Não houve PR, merge, APK, instalação,
publicação de pesos, alteração de allowlist, backend, `aegis-tts` ou deploy.

Resultados completos: [wakeword-tradeoff-results.json](wakeword-tradeoff-results.json).
