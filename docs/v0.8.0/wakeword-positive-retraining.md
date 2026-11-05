# Aegis v0.8.0 — segunda rodada com positivos reais

Experimento local de 05/11/2026, branch
`feat/v0.8.0-wakeword-positive-retraining`, worktree
`/home/phc/Aegis-wakeword-positive-retraining`, base `2c49052`.
A ponta remota de `feat/v0.8.0-wakeword-hardneg` também foi conferida em
`2c490528bd5f8699e23ae49d183b9ff176427fcc` antes dos fits.
As alterações locais do checkout original e as demais worktrees foram preservadas.

## Auditoria e recuperação

ZIP consolidado SHA-256
`4543631c6842a22708db9f4839109ed3ffdd979eb03274333009b22d201741bf`.
CRC, segurança dos caminhos, 524 hashes individuais, CSV/JSONL em todos os
campos, formato e tamanho dos WAVs passaram. São 260 eventos únicos e 520 WAVs
PCM16 mono/16 kHz: 107 `correct`, 153 `false_positive`. Os metadados dos 161
antigos coincidem com os originais; os sete parâmetros operacionais e a
configuração completa são compatíveis com o mesmo baseline nos 260 eventos.

Todos os pares compartilham exatamente 200 ms. A decisão situa-se antes dessa
margem no contexto. Nos novos positivos, o treino remove a margem **antes** do frontend. As
features negativas herdadas usam o frontend causal no contexto, com janelas
terminando até a decisão; amostras posteriores não entram nessas janelas.
O treino não abre `post_activation.wav`. Os originais permanecem intactos. Contextos somam
529,190 s; pós somam 909,502 s, com sobreposição. Não se somam esses valores
para calcular exposição contínua.

Há 61 contextos com menos de 2 s anteriores à decisão: dez antigos e 51 novos.
Nove dos novos têm menos de 1 s. Não houve clipping acima de 1%, silêncio quase
total, sequências zero de pelo menos 200 ms ou erros de captura/armazenamento. O único
`endedAt=null` tem WAVs íntegros e não foi excluído automaticamente. Metadados
temporais, versões, hashes do modelo e configuração do detector passaram.

Não há duplicatas PCM nem pares de contextos/predecisão com NCC ≥0,90 em
sobreposição ≥0,75 s. Dois trechos estritamente anteriores à decisão são
menores que esse mínimo: a ausência de match não prova independência. Nos
pós, 662 pares atingiram o critério; 95 persistiram retirando 500 ms. Nenhum
desses 95 persistiu retirando 1 s; todos concentram ≥70% da energia alinhada
em 40 ms, 94 concentram ≥90%. Isso sustenta influência do sinal de confirmação
tardio, sem classificar conversas como duplicadas. A reavaliação dos pós
recortados restringiu-se aos candidatos encontrados na busca bruta exaustiva.

Foram recuperadas e verificadas 4.779 relações de hash do corpus/artefatos
originais, sem divergências: 4.696 arquivos de treino, originais/derivados da
PoC, manifestos, baseline e público. O baseline `aegis-mww-fleurs-dev-2026-10-31` mantém 13.057 parâmetros,
39.552 bytes e SHA-256
`f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514`.
O checkpoint treinável é o original, não uma reconstrução do TFLite.
Fonte microWakeWord limpa em `4665173cd35f1cff9a61e06fc427f124766c488e`;
TensorFlow CPU 2.18.1, Keras 3.15.1, NumPy 2.0.2, LiteRT 2.3.0 e
pymicro-features 2.0.2, em ambientes locais recuperados.

## Splits congelados

Os 161 eventos antigos mantêm literalmente seus splits e papéis do protocolo
v3 anterior. Inclusive os quatro positivos de seu período de treino continuam
**avaliação somente**; nenhum dos 15 antigos passa a treinar ou calibrar.

| Origem | Split | Positivos | Negativos |
| --- | --- | ---: | ---: |
| Antigos 161 | Treino | 4, avaliação somente | 57 |
| Antigos 161 | Validação | 4 | 25 |
| Antigos 161 | Teste de regressão conhecido | 7 | 64 |
| Novos 99 | Treino | 40, sujeitos à inspeção | 0 |
| Novos 99 | Validação | 14 | 0 |
| Novos 99 | Teste | 28 | 7 |
| Novos 99 | Intervalos excluídos | 10 | 0 |

Na sessão nova, treino termina antes de 12:52:45; validação ocupa
12:53:15–12:54:45; teste começa às 12:55:15, horário UTC−03. Há intervalos de
30 s entre os blocos. O protocolo foi congelado antes de qualquer fit e a
divisão não mudou depois. IDs, origem, rótulos, grupos, hashes e critérios estão
no manifesto privado; verificações automáticas preservam papéis antigos e
bloqueiam vazamento de IDs/grupos/derivações.

A alocação privilegia o bloco inicial maior para aprendizagem, reserva um bloco intermediário de validação e preserva o trecho final, onde se concentram os sete negativos, para teste. Os intervalos de 30 s superam o contexto de captura e a cadência típica da sessão; não se redistribuíram negativos para equilibrar artificialmente os splits. Depois da inspeção, somente seis dos 40 positivos de treino foram elegíveis, sem transferir exemplos reservados para compensar essa redução.

A sessão nova dura 568,817 s, com intervalos de 2,044–18,056 s entre eventos.
Os blocos não são sessões acústicas independentes. Nem os intervalos nem a
busca de duplicatas eliminam semelhança entre realizações repetidas. Os 14/28
positivos reservados medem generalização limitada dentro dessa mesma sessão.

## Inspeção dos positivos e revisão dos filtros

A auditoria de qualidade precedeu o treinamento; não utilizou scores de novos
pesos. Whisper small local, sem prompt, com timestamps, foi aplicado apenas ao
PCM anterior à decisão. Nenhum áudio foi enviado a serviço externo.

O filtro lexical estrito da revisão 2 aprovou somente 1/92, no teste, e 0/40
no treino. Ele também reconheceu uma forma explícita do nome em apenas duas
das seis referências humanas válidas já usadas na PoC. Portanto, suas omissões
não sustentam exclusão semântica nem demonstram ausência da palavra. A primeira
revisão parcial foi preservada; a segunda corrigiu um erro instrumental em que
ASR `start=0` era interpretado como início físico, apesar de silêncio anterior.

Uma comparação independente MFCC/DTW foi calibrada em seis referências humanas
com leave-one-out, 52 hard negatives antigos de treino e 32 FLEURS DEV de
treino, com seed fixa. A calibração foi selada antes de calcular distâncias
nos 92 novos positivos. O corte discriminativo conservador 0,157473 rejeitou
6/6 referências conhecidas: não demonstrou separabilidade e não foi usado como
um novo classificador de rótulos.

O adendo seguinte utiliza o **rótulo humano como âncora semântica**, atividade
acústica inteira isolada e suporte de compatibilidade com duas referências
até o máximo leave-one-out das positivas (0,225325). Quatro de 84 negativos de
calibração também são compatíveis nesse corte. Isso limita a especificidade do
DTW e impede chamá-lo de confirmação lexical independente. O novo adendo foi
registrado antes do primeiro fit, mas depois da auditoria dos áudios e dos
resultados dos filtros: não é um critério cego pré-áudio. Nenhum resultado de
treino ou replay final orientou essa revisão.

A inclusão exige componente único, duração plausível, ausência de cue tonal,
guardas de 80 ms e mais 40 ms livres nas bordas de captura. A inspeção visual
individual dos candidatos de treino verifica onset, cauda/fricativa e limites.
Não houve escuta humana ou anotação fonética certificada. Trata-se de supervisão
experimental por rótulo humano com corroboração instrumental conservadora;
incerteza de limites e identidade acústica permanece explícita.

## Desenho dos fits

Dois fits predefinidos, sem busca posterior de hiperparâmetros ou thresholds:

| Fit novo | Inicialização | Épocas | Learning rate |
| --- | --- | ---: | ---: |
| A | Checkpoint baseline | 4 | 0,00001 |
| B | Checkpoint da antiga A | 4 | 0,00003 |

Adam novo em ambos. BatchNormalization fica congelada, inclusive estatísticas:
learning rate baixo sozinho não impediria deslocamento das médias e variâncias
pelo novo balanceamento. Arquitetura MixConv e contrato streaming permanecem.

Cada época tem 32 minibatches de 64: 20 positivos sintéticos, quatro derivados
das seis referências, oito positivos reais novos, 16 negativos
sintéticos/prefixos de referência, oito FLEURS DEV e oito hard negatives.
São 32 positivos e 32 negativos por batch; 256 apresentações reais e 256 hard
por época. O corpus anterior tinha 1.512/8.869 positivos; acrescentar 1.024
hard negatives na rodada anterior baixava sua proporção de aproximadamente
17% para 15%. Isso é uma hipótese de contribuição para a supressão observada,
não uma causa demonstrada.

Os arrays originais de 8.869/1.857 janelas são reutilizados com igualdade de
hash. São amostrados por fonte, sem afirmar que todos passam por cada época.
Permutações reduzem repetições desnecessárias nas fontes originais; fontes
reais e hard são balanceadas por grupo/evento com teto
`2×ceil(apresentações/eventos elegíveis)`. Cada época registra exposições e
fontes únicas. Augmentações não aumentam o número de eventos independentes.

Positivos reais recebem ganhos 0,85/1/1,15 e duas posições de janela após o
limite guardado da palavra, sempre limitadas à decisão. Não recebem máscaras,
ruído, speed perturbation ou conteúdo posterior à decisão. O frontend C
mantém seu estado no contexto bruto, em blocos de 160 amostras; a janela tem
186 frames com padding à esquerda quando necessário. A calibração INT8 mantém
exatamente as primeiras 500 janelas do corpus de treino anterior, sem
validação/teste. Essa calibração é enviesada por fonte, mas permanece constante
entre comparações.

Cada época é exportada e reavaliada em streaming INT8 na validação, em LiteRT
e no núcleo nativo host. A ordem de seleção é: perdas pareadas de positivos
reproduzidos pelo baseline; violação do limite de dois pontos percentuais na
validação positiva original; FP de validação; perdas sintéticas pareadas; BCE.
Elegibilidade exige zero perdas reais pareadas nos dois runtimes e preservação
sintética dentro do limite. A seleção é selada antes do replay final. BCE menor
ou novos acertos não compensam chamadas do baseline perdidas.

O export usa um processo descartável: o helper upstream altera o modo das
camadas recebidas. Reutilizar diretamente o modelo de treino no export de cada
época poderia contaminar as épocas seguintes. Os checkpoints e relatórios
anteriores não foram alterados.

## Dados efetivamente apresentados e execução

Dos 40 positivos novos reservados para aprendizagem, **seis originais** passaram
pelo suporte e pela revisão visual, sem exclusões visuais adicionais. Geraram
36 janelas derivadas (três ganhos × duas posições); não são 36 observações
independentes. Os outros 34 ficaram fora do gradiente. Motivos podem acumular:
16 componentes não isolados, 18 sem suporte de duas referências, duas margens
insuficientes, um componente com duração inadequada e um possível cue tonal.
Isso não declara que os 34 sejam chamadas falsas ou inválidas para replay.

Os 14 positivos de validação e 28 de teste novos são avaliados integralmente,
independentemente de sua adequação a recortes de treino; um e cinco,
respectivamente, também passaram pelo suporte conservador. Dez positivos ficam
nos intervalos de separação, fora de treino e seleção, com diagnóstico separado.
Os 15 antigos continuam fora do fit. As seis referências humanas originais do
baseline continuam representadas por seu pool preservado de 480 derivações
positivas. Nenhum positivo antigo de teste foi transferido à aprendizagem.

Cada fit completo apresenta os seis novos 1.024 vezes ao todo, em quatro
épocas (cerca de 171 reutilizações por original, em média). O limite por evento
é aplicado por época; não cria novas condições acústicas. Os 52 hard negatives
antigos efetivamente elegíveis fornecem 468 janelas derivadas. Os cinco eventos
falsos do período de treino antigo excluídos anteriormente continuam excluídos.
Os sete negativos novos pertencem ao teste e nunca são treinados.

As duas primeiras execuções foram interrompidas após a primeira época: exportar
uma terceira instância Keras no mesmo processo produziu 39.720 bytes, recusados
pela guarda nativa literal de 39.552 bytes. A comparação dos FlatBuffers sustenta
mesma estrutura computacional, com diferenças de nomes/serialização. A correção
foi exportar o checkpoint em subprocesso limpo com nomes canônicos, preservando
arquitetura, quantização e runtime. Os artefatos interrompidos permanecem em
`model-{A,B}-v1`; os dois fits completos estão em `model-{A,B}-v2`.

Os checkpoints da primeira época dos reinícios são **byte a byte idênticos**
aos dos respectivos fits interrompidos. Não houve nova configuração nem busca
motivada pelo teste. O replay canônico da validação confirmou aceitação pela
biblioteca nativa sem alteração de código Android. As oito épocas completas
foram exportadas e avaliadas nos dois runtimes. O registro auxiliar
`training-launch-v1.json` foi criado após iniciar os processos, pois o primeiro
comando de registro usou Python sem NumPy e falhou. Protocolo, features e
revisões visuais já estavam selados; o registro não é apresentado como anterior
ao início. A correção mecânica tem seu próprio registro pré-reinício.

A seleção foi selada em **NO-GO**, antes do replay final: nenhuma época dos
dois fits preservou todas as chamadas reais da validação nos dois runtimes.
A época 2 de A foi conservada como referência de pesquisa pela regra
lexicográfica; isso não aprova seu uso. B também conservou a época 2. Não houve
novos fits, mudança de checkpoint ou ajuste de threshold após abrir o teste.

## Resultados por época e checkpoint

| Fit/época | BCE original val. | Positivos originais val. | Chamadas reais val. LiteRT/nativo /18 | FP val. LiteRT/nativo /25 | Elegível |
| --- | ---: | ---: | ---: | ---: | --- |
| A/1 | 0.000513 | 100% | 17/17 | 17/17 | Não |
| A/2 (selecionada) | 0.000717 | 100% | 17/17 | 13/13 | Não |
| A/3 | 0.000821 | 100% | 17/17 | 13/13 | Não |
| A/4 | 0.000774 | 100% | 17/17 | 14/14 | Não |
| B/1 | 0.003843 | 100% | 16/16 | 15/15 | Não |
| B/2 (selecionada) | 0.006556 | 100% | 17/17 | 13/13 | Não |
| B/3 | 0.006129 | 100% | 16/16 | 9/9 | Não |
| B/4 | 0.005751 | 100% | 16/15 | 6/6 | Não |

A validação original contém 264 positivos; todos foram preservados nas janelas float. Isso não impediu perdas no replay INT8 streaming real. A época 4 de B reduziu mais negativos, mas perdeu duas chamadas no LiteRT e três no nativo, ficando inelegível. A/B demoraram 68,45/70,35 s nos fits completos com export/replay, executados simultaneamente; esses tempos não incluem auditoria ou ASR.

## Comparação final

Referência principal: LiteRT/default, threshold 0,5, média de cinco scores, liberação abaixo de 0,3 e refratário de 16.000 amostras. Contextos sem cauda artificial e com reset individual. Contagens perto da decisão original (−500/+200 ms).

| Modelo | Antigos legítimos /15 | Novos val. /14 | Novos teste /28 | FP antigos teste LiteRT/nativo /64 | PoC /23 | Público /35 min |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline | 15 | 14 | 28 | 58/58 | 23 | 4 |
| A anterior | 11 | 8 | 24 | 9/10 | 23 | 1 |
| C anterior | 12 | 11 | 26 | 21/21 | 23 | 2 |
| Nova A | 14 | 13 | 28 | 40/40 | 23 | 4 |
| Nova B | 13 | 13 | 28 | 38/38 | 23 | 3 |

Os positivos das três primeiras colunas têm as mesmas contagens no host nativo. Os 15 antigos são um diagnóstico agregado de regressão, com quatro pertencentes a períodos de treino e quatro já observados em validação; não formam teste independente. Os sete antigos de teste continuam 7/7 em todos os modelos.

| Grupo de contextos, LiteRT | Baseline | A anterior | C anterior | Nova A | Nova B |
| --- | ---: | ---: | ---: | ---: | ---: |
| Antigos positivos, período treino /4 | 4 | 1 | 2 | 3 | 2 |
| Antigos positivos, validação /4 | 4 | 3 | 3 | 4 | 4 |
| Antigos FP, treino /57 | 57 | 6 | 13 | 37 | 20 |
| Antigos FP, validação /25 | 23 | 1 | 3 | 13 | 13 |
| Novos positivos, bloco treino /40 | 40 | 32 | 34 | 40 | 37 |
| Novos positivos, intervalos excluídos /10 | 10 | 8 | 8 | 10 | 10 |
| Novos FP, teste /7 | 7 | 6 | 7 | 7 | 7 |

O bloco novo de treino é diagnóstico de ajuste: seis dos 40 eventos alimentaram o gradiente, e os outros compartilham sua sessão. Não é validação independente. No nativo, A anterior reconhece 33/40, C anterior reconhece 9/10 nos intervalos e B reconhece 38/40; os demais positivos da tabela coincidem. Para FP de treino, B tem 16/57 no nativo, contra 20/57 no LiteRT; A anterior tem 7/7 FP novos no nativo, contra 6/7 no LiteRT. Todos os splits/origens e scores constam no JSON estruturado.

## Chamadas antigas difíceis, por evento

Aliases em ordem temporal; a correspondência com IDs reais fica somente no manifesto privado. Cada célula traz reconhecimento perto da decisão e máximo da média móvel no LiteRT. O JSON inclui também o nativo e diferenças de tempo.

| Evento | Split | Baseline | A anterior | C anterior | Nova A | Nova B |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| old-P01 | test | sim / 0.996 | sim / 0.981 | sim / 0.995 | sim / 0.996 | sim / 0.996 |
| old-P02 | test | sim / 0.996 | sim / 0.996 | sim / 0.996 | sim / 0.996 | sim / 0.996 |
| old-P03 | test | sim / 0.996 | sim / 0.991 | sim / 0.996 | sim / 0.996 | sim / 0.996 |
| old-P04 | test | sim / 0.996 | sim / 0.996 | sim / 0.996 | sim / 0.996 | sim / 0.996 |
| old-P05 | test | sim / 0.991 | sim / 0.791 | sim / 0.890 | sim / 0.981 | sim / 0.996 |
| old-P06 | test | sim / 0.996 | sim / 0.990 | sim / 0.995 | sim / 0.996 | sim / 0.996 |
| old-P07 | validation | sim / 0.996 | sim / 0.995 | sim / 0.996 | sim / 0.996 | sim / 0.996 |
| old-P08 | validation | sim / 0.996 | sim / 0.996 | sim / 0.996 | sim / 0.996 | sim / 0.996 |
| old-P09 | validation | sim / 0.996 | sim / 0.996 | sim / 0.996 | sim / 0.996 | sim / 0.996 |
| old-P10 | validation | sim / 0.804 | não / 0.060 | não / 0.105 | sim / 0.714 | sim / 0.741 |
| old-P11 | train | sim / 0.996 | não / 0.420 | sim / 0.741 | sim / 0.996 | sim / 0.991 |
| old-P12 | train | sim / 0.821 | não / 0.060 | não / 0.059 | sim / 0.559 | não / 0.277 |
| old-P13 | train | sim / 0.996 | sim / 0.960 | sim / 0.991 | sim / 0.996 | sim / 0.996 |
| old-P14 | train | sim / 0.668 | não / 0.060 | não / 0.059 | não / 0.333 | não / 0.062 |
| old-P15 | test | sim / 0.996 | sim / 0.996 | sim / 0.996 | sim / 0.996 | sim / 0.996 |

A nova A recupera old-P10, P11 e P12 perdidos pela antiga A, mas continua perdendo P14; a nova B recupera P10/P11, mas perde P12/P14. O baseline reconhece todos. Ambas as novas perdem também uma chamada nova de validação. Os 28/28 de teste não compensam essas perdas. Detalhes pareados de cada evento novo permanecem privados em `eval-*-final/evaluation.json` e nos scores por frame.

## Falsos positivos: supressões e novos eventos

| Modelo, teste antigo | Contextos perto /64 | Contextos em qualquer posição | Eventos totais | Suprimidos do baseline perto | Novos contextos perto | Redução líquida perto |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline | 58 | 58 | 58 | 0 | 0 | 0.00% |
| A anterior | 9 | 9 | 9 | 49 | 0 | 84.48% |
| C anterior | 21 | 21 | 21 | 37 | 0 | 63.79% |
| Nova A | 40 | 40 | 40 | 18 | 0 | 31.03% |
| Nova B | 38 | 40 | 41 | 21 | 1 | 34.48% |

A reduz 58→40 contextos (31,03%), com 18 supressões e nenhum contexto novo. B reduz 58→38 perto da decisão (34,48%): 21 supressões e **um falso contexto novo**. Ao contar qualquer posição, B tem 40 contextos, redução de 31,03%, e 41 eventos totais, redução de 29,31%. O nativo repete esses números para as novas candidatas. A anterior tem dez contextos/eventos no nativo, contra nove no LiteRT; C anterior tem 21 em ambos.

Nos sete negativos novos, ambas as novas mantêm 7/7 reativações. A antiga A tinha 6/7 no LiteRT e 7/7 no nativo. Esses dados são condicionados às ativações originalmente capturadas: supressão por replay não é falso positivo por hora. Não foram minerados novos exemplos a partir do teste nem ajustados thresholds após esses resultados.

## Runtime, reset, sequência contínua e recursos

Todos os oito exports completos passaram no contrato INT8 `[1,3,40]`, UINT8 `[1,1]`, escala de entrada `0.10196078568696976`, zero point −128; saída `1/256`, zero point 0; sem tensores float. Mantêm 39.552 bytes, 13.057 parâmetros e estado streaming. São 12.545 parâmetros treináveis neste fit, após congelar quatro BatchNormalizations. A biblioteca nativa preservada aceitou todos os exports canônicos.

| Modelo, 260 contextos | Maior diferença de score em LSB | Contextos acima de 1 LSB | Diferença de contexto ativado | Contextos com eventos fora de um stride/contagem distinta |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 21 | 215 | 0 | 0 |
| A anterior | 24 | 128 | 3 | 3 |
| C anterior | 31 | 199 | 1 | 1 |
| Nova A | 20 | 222 | 2 | 2 |
| Nova B | 29 | 200 | 5 | 6 |

A tolerância anterior de 1 LSB falha inclusive no baseline e não foi ampliada. Compatibilidade de carregamento não equivale a paridade numérica. Divergências de eventos permanecem relatadas; Linux x86_64 não homologa ARM64 físico.

A regressão PoC independente e contínua mantém 23/23 positivos e 1/8 negativos ativados nos cinco modelos. No contínuo PoC preservam-se prefixo público de 30 s e intervalos de 3 s, sem reset. O público inteiro de 35 min teve baseline/A nova quatro eventos, B nova três, A anterior um, C anterior dois, nos dois runtimes. São testes já conhecidos.

Também foram reproduzidos os 260 contextos em ordem temporal sem reset, com intervalos artificiais de 3 s, preservando estado. Essa sequência é um stress de regressão, não reconstrução da conversa gravada. Não houve evento nos intervalos. Comparada ao baseline nesse mesmo protocolo, A ainda perde uma chamada nova de validação; B perde uma antiga e duas novas de validação. O JSON separa origem/split e os resultados de cada runtime. O estado pode alterar reconhecimento, e o resultado contínuo não substitui o teste com inicialização limpa.

Nos positivos antigos/novos reservados reconhecidos por ambos, A teve mediana de deslocamento versus baseline de 0 ms, com extremos de −30/+30 ms. No novo teste, B teve mediana de −60 ms, com extremos de −150/+120 ms. São diferenças entre decisões do detector em chamadas comuns, sem referência a final fonético verificado; chamadas perdidas ficam fora dessa comparação e não são compensadas por detecção antecipada em outras.

Reset após ruído e reset repetido produziram scores e eventos exatamente iguais a uma instância limpa, dentro de cada runtime, para os cinco modelos. Silêncio de 30 s, ruído branco de 30 s em RMS 100/1000 e 1,2 s de fundo ambiente real não produziram eventos. O fundo real vem de seis lead-ins de treino anteriores ao onset instrumental, sem ganho ou repetição; é um controle curto e restrito, insuficiente para afirmar robustez ambiental.

No host, a razão tempo de processamento/tempo de áudio foi: baseline 0,00544 no LiteRT e 0,00509 no nativo; A 0,00412/0,00518; B 0,00406/0,00525. O nativo ficou aproximadamente 1,02×/1,03× o baseline. Não há evidência local de grande regressão de custo, mas as medições incluem harness/frontend e carga do host; não provam ganho de desempenho ou custo ARM64. RSS é de processo compartilhado, com retenção de alocadores: não foi medido arena isolada por modelo ou consumo físico no celular.

## Decisão e questionamento técnico

**NO-GO para substituir o baseline.** A aprendizagem com positivos reais
melhorou a preservação das chamadas em relação às antigas A/C, mas não
preservou integralmente a sensibilidade do baseline. A perde uma chamada antiga
e uma de validação nova; B perde duas antigas e uma nova de validação. A redução
no teste antigo ficou abaixo da meta aproximada de 50%, e B introduziu um
contexto falso novo. A escolha de pesquisa permanece A/época 2, exatamente como
selada antes do teste; nenhuma outra época foi promovida depois de observar
resultados favoráveis ou desfavoráveis.

A hipótese recebeu evidência parcial, sem confirmar uma solução: acrescentar
positivos em um treinamento balanceado pode recuperar chamadas, mas o custo
observado foi devolver parte das ativações indevidas. Como proporções, learning
rate, BatchNormalization e inicializador também mudaram, este desenho não
isola causalmente a contribuição de cada fator. Não há justificativa para
substituir o modelo atual pela melhor opção de um conjunto ainda inadequado.

Decisões e riscos que merecem investigação:

- São apenas seis novos originais no fit, da mesma sessão curta; 1.024
  apresentações por fit favorecem memorização. O balanceamento por minuto é um
  agrupamento de amostragem, não prova de ambientes diferentes. Os limites
  por evento restringem concentração, mas não criam diversidade.
- A qualidade instrumental usa rótulos humanos de eventos, envelopes e suporte
  de templates. Sem escuta e alinhamento fonético humano, margens de 80 ms podem
  ainda não capturar todos os fonemas fracos. Rever recortes com anotação local
  é mais útil que relaxar automaticamente filtros para admitir todos os 92.
- Os filtros de ASR e DTW foram corrigidos/revisados durante a preparação. Os
  controles positivos demonstram suas falhas; isso justifica a revisão técnica,
  mas impede alegar um plano de qualidade totalmente cego aos novos áudios.
  Todos os pais e exclusões foram preservados.
- A fonte sintética e a pronúncia real podem diferir. Deve-se verificar
  localmente a realização do nome e sua diversidade acústica antes de aumentar
  volume sintético ou complexidade da arquitetura.
- O rótulo negativo é de ativação, não uma transcrição de cada janela. Supervisão
  próxima à decisão reduz, sem eliminar, ruído de rótulo. Recortes negativos e
  foneticamente próximos precisam de revisão contextual, sem minerar o teste.
- BCE e recall de janelas originais foram excelentes enquanto o streaming real
  perdia chamadas. Seleção por evento pareado é necessária; ajustar threshold
  após teste ocultaria regressão.
- Congelar BatchNormalization preserva estatísticas, mas pode limitar adaptação
  de ambiente. Um próximo ensaio deve isolar poucas mudanças com novos dados
  reservados, em vez de uma busca ampla sobre os testes já consultados.
- A calibração INT8 herdada usa uma fatia inicial de uma fonte. Um futuro ensaio
  pode investigar calibração representativa de treino, com frontend e contrato
  verificados; não se modificou isso para melhorar esta avaliação.
- A guarda nativa de tamanho exato torna o export sensível a nomes de tensores.
  A correção desta rodada isola o processo de exportação. Uma eventual revisão
  desse contrato deve ser uma tarefa própria, sem mudar allowlist ou runtime
  para acomodar pesos rejeitados durante este experimento.
- Scores LiteRT/nativo divergem além de 1 LSB inclusive no baseline. A origem
  frontend/kernels merece investigação separada e testes físicos; não se
  resolveu a divergência aumentando tolerâncias.

Os positivos foram selecionados por ativações já reconhecidas. Redetecção não
é recall de todas as tentativas; faltam chamadas que o baseline nunca registrou.
Negativos também foram selecionados por ativação e não têm denominador de
exposição contínua. A futura coleta prospectiva deve ter duração conhecida,
chamadas tentadas/perdidas anotadas e sessões variadas reservadas antes do
ajuste. Os testes antigos foram inspecionados em experimentos anteriores:
são regressão conhecida, não avaliação cega inédita.

Continuam pendentes: escuta/anotação local adicional dos recortes, homologação
física Android ARM64, custo/memória no dispositivo e coleta prospectiva com
exposição conhecida. Nenhum modelo foi importado no celular. Não houve APK,
deploy, release, alteração de allowlist, backend, `aegis-tts`, runtime Android,
merge ou mudança em branches paralelas.

## Artefatos e reprodução

Raiz privada: `/home/phc/.cache/aegis-wakeword-positive-retraining/`, fora do
Git. Áudios, ZIP, transcrições, IDs reais, hashes individuais de voz, features,
checkpoints e pesos permanecem privados. O relatório versionado e o
[resumo estruturado](wakeword-positive-retraining-results.json) contêm somente
aliases e métricas/proveniência sem conteúdo pessoal.

| Artefato | Local privado ou SHA-256 |
| --- | --- |
| Baseline preservado | `~/.cache/aegis-wakeword-poc/comparison/microwakeword/model-realneg/aegis.int8.tflite` |
| Nova A, pesquisa NO-GO | `model-A-v2/aegis.int8.tflite` |
| SHA-256 A | `5b198451df030dbc2650cbfd20f9e29ed3ad88aecc52213d2dcf24e45f9594ff` |
| Nova B, pesquisa NO-GO | `model-B-v2/aegis.int8.tflite` |
| SHA-256 B | `55d09e196d6bfe747ba51c9f0668c2282f3965d9210f6ed0526ab89cb055392e` |
| Inventário auditado | `audit-v1/inventory.json`, SHA `05b3e33ec098b7788c7d7b87c97a807a8da4679c6a7a2a1f6da9bbf95b6c3e1c` |
| Protocolo | `protocol-v1.json`, SHA `b81d28e40ce1f1e3949a0659411c683f434942d30c38d469e3200294964eeeb1` |
| Regras de suporte | `frozenrulesupport-v1.json`, SHA `9f10a0782a33880d27c8c77c187172c90d7fb92c432667549cd8818b4d43644b` |
| Relatório de suporte | `support-inspection-v1/report.json`, SHA `7b07245a97a87f3f5dee6688e795c410276cf33fd96e305dc5f0786ef73ca37e` |
| Revisão acústica adicional | `acoustic-review-v1.json`, SHA `3e2180f2695bed1f8b7074ac2c57b7b398d52c1db9b325f25d9233e683c257b4` |
| Checkpoints de todas as épocas | `model-{A,B}-v2/epoch-01` a `epoch-04` |
| Seleção anterior ao teste | `selection-v1.json`; lançamento `final-replay-launch-v1.json` |
| Replay dos cinco modelos | `eval-{baseline,previousA,previousC,A,B}-final/` |
| Controles e contínuo | `controls-v1/controls.json`, com arrays de scores privados |
| Pareados por evento e aliases | `summary-v1/private_summary.json` |

O `visual_train_review_pending=true` do relatório original foi preservado como
estado histórico. As revisões independentes em `root-acoustic-review-v1.json`
e `acoustic-review-v1.json` encerram a pendência sem reescrever aquele artefato.
O resumo seguro inicial está em `summary-v1/public_summary.json`; o JSON
versionado acrescenta informações de execução e proveniência. Hashes de ambos
são distintos por essa razão.

Pontos de entrada em `scripts/wakeword-poc/`, sempre com destinos novos:

1. `positive_audit.py --zip <ZIP> --output <raiz>/audit-v1` recupera e verifica os
   originais; `positive_protocol.py --audit <inventário> --output
   <raiz>/protocol-v1.json` sela papéis e configurações.
2. `inspect_positive.py --help`, `inspect_positive_templates.py --help` e
   `prepare_positive_support.py --help` documentam a inspeção local. Conservar
   seus protocolos e relatórios de controle; a revisão visual privada precisa
   acompanhar a reprodução, sem assumir que resultados sejam anotação humana.
3. `train_positive.py prepare --protocol <protocolo> --audit <inventário>
   --inspection <support-inspection-v1/report.json> --output <raiz>/features-v1`
   confere baseline/corpus e reutiliza os arrays antigos por hash.
4. Antes de treinar, executar `evaluate_hardneg.py` para o baseline com
   `--split validation`, usando o inventário e o protocolo novos e a biblioteca
   host preservada. Não executar `--split all` nesta etapa.
5. `train_positive.py train --protocol <protocolo> --audit <inventário>
   --features <features-v1> --baseline-evaluation <baseline-validation/evaluation.json>
   --strategy A --output <raiz>/model-A-v2`; repetir B. O exportador
   `export_positive.py` é chamado automaticamente em subprocesso.
6. `select_positive.py --root <raiz>` sela a escolha, inclusive NO-GO. Só então
   executar `evaluate_hardneg.py --split all --legacy` para os cinco modelos,
   em `eval-<nome>-final`, com a mesma biblioteca nativa.
7. `positive_controls.py --help` executa silêncio/ruído/reset/contínuo e aceita
   `--environment-wav` para o pequeno controle ambiental com proveniência.
   Exige seleção correspondente ao protocolo antes de abrir o teste.
8. `positive_summary.py --root <raiz> --output <novo-resumo>` confere hashes,
   resume todos os resultados e aplica gates à escolha já selada; não escolhe
   outra época depois do teste. O JSON versionado adiciona contexto de execução
   e hashes dos relatórios auxiliares, sem alterar métricas.

Para treino, auditoria e replay, usar
`~/.cache/aegis-wakeword-poc/comparison/microwakeword/venv/bin/python`.
Para Whisper, usar `~/.cache/aegis-wakeword-poc/tools/venv/bin/python` com o
modelo local `tools/whisper-small`, em modo offline. Para `inspect_positive_templates.py`, usar
`~/.cache/aegis-wakeword-poc/comparison/classic/venv/bin/python`, com
python_speech_features e rhasspy-wake-raven preservados. O helper
`prepare_positive_support.py` usa `tools/venv/bin/python` para os gráficos;
versões e proveniência constam dos relatórios privados de calibração.
Pacote privado de pesquisa, explicitamente **NO-GO**:
`~/.cache/aegis-wakeword-positive-retraining/aegis-positive-retraining-research-2026-11-05.zip`,
SHA-256 `3c76482694bc74664970e0b62b28d9cf47f337f11a4e292f46f249e0d298e164`.
Contém os dois TFLites, manifesto de modelo/compatibilidade, seleção e resultados,
sem áudios ou transcrições. Não constitui pacote aprovado para produção.
Todos os arquivos privados novos foram restringidos a 0600 e diretórios a
0700 ao concluir.

Verificações: nove testes unitários de integridade/correspondência temporal,
preservação de papéis e limites do sampler; `py_compile` dos scripts novos;
`git diff --check`; oito exports INT8 e replays de validação; cinco replays
finais completos LiteRT/nativo; controles de reset/silêncio/ruído/estado contínuo;
conferência de privacidade do JSON versionado. Nenhum check físico ARM64 foi
declarado aprovado.
