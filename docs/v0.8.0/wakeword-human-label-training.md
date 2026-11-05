# Aegis v0.8.0 — treinamento direto com rótulos humanos

**Resultado: NO-GO para substituir o baseline. Todos os 221 eventos foram treinados; nenhum filtro acústico foi aplicado.**

Treinamento local em branch isolada
`feat/v0.8.0-wakeword-human-label-training`, worktree
`/home/phc/Aegis-wakeword-human-label-training`, base `9e0ca26` da rodada
anterior. As branches, modelos e evidências anteriores foram preservados.

Foi usado `aegis-dataset-260-split-85-10-5.zip`, SHA-256
`740f5df69c1bb08e96ce83331a4a6e7418b9db77c3ccaa152e71c2be319d21d2`.
O arquivo foi disponibilizado pelo proprietário depois da preparação inicial
do código. Não se substituiu o ZIP nem se reconstruíram splits a partir do
consolidado anterior.

## Pipeline executado

A nova trilha usa `prepare_direct_training.py` e
`train_direct_training.py`. Não chama ASR, MFCC/DTW, seleção de componentes,
limites ideais de duração/margens, RMS ou filtros de ruído. Os scripts antigos
permanecem como histórico, sem participar da elegibilidade desta rodada.

Os rótulos humanos e splits registrados em `dataset.jsonl` são a autoridade:

| Split fornecido | Corretos | Falsos positivos | Total |
| --- | ---: | ---: | ---: |
| Treino | 91 | 130 | 221 |
| Validação | 11 | 15 | 26 |
| Teste | 5 | 8 | 13 |

Essas quantidades foram conferidas nos inventários e preservadas integralmente.
**Todos os 221 eventos de treino participaram de cada época, sem exclusões.**
CRC, segurança dos caminhos, SHA-256 dos membros, inventários CSV/JSONL,
formato PCM16 mono/16 kHz, tamanho do payload e sobreposição exata de 200 ms
são verificações técnicas. Corrupção ou inconsistência interrompe a preparação;
nenhum evento é excluído silenciosamente. WAVs originais permanecem intactos.

O frontend existente recebe somente `trigger_context.wav` anterior à decisão.
Os últimos 200 ms compartilhados com `post_activation.wav` ficam fora das
features. O pós serve apenas à conferência técnica, nunca como exemplo
independente. Cada evento de treino produz três janelas com ganhos
0,85/1/1,15, terminando na decisão. Contextos curtos recebem padding, mesmo
quando não há frame completo do frontend; intensidade ou silêncio não são
motivos de exclusão. Foram geradas 663 janelas derivadas de **221 originais**, sem
atribuir independência às augmentações. Validação não é augmentada; teste não
gera features de aprendizagem ou calibração.

Os arrays sintéticos, as seis referências humanas originais e FLEURS DEV são
reutilizados por hash. Não se adiciona separadamente o pool antigo de hard
negatives: os 130 negativos reais fornecidos já têm seu próprio papel de treino.
As novas atribuições substituem as separações experimentais anteriores para
este ensaio, conforme autorização explícita do proprietário.

`evaluate_hardneg.py --provided-splits` mantém os grupos/sessões originais e
relata cruzamentos entre splits, sem bloquear, excluir ou redistribuir eventos.
O modo anterior continua sendo o padrão para preservar sua reprodução.

## Configurações e seleção

O primeiro fit partiu do checkpoint baseline, quatro épocas, Adam novo,
learning rate `0,00001`, seed `20261105`, BatchNormalization congelada.
Arquitetura, calibração INT8, frontend e regras operacionais permanecem.
A configuração foi registrada com hashes antes do primeiro gradiente.
Os quatro checkpoints perderam chamadas da validação. Antes de abrir o teste,
foi selado um único follow-up conservador: duas épocas, learning rate
`0,000003`, reiniciando do mesmo baseline com Adam novo; dados, seed, sampler,
BN, calibração e regras permaneceram. O adendo está em `followup-plan-v2.json`.
Não houve outra configuração ou busca extensa. Ambos os fits foram concluídos.
As duas versões executadas do script estão em snapshots privados cujos
hashes conferem com os relatórios. A versão inicial foi recuperada por
reversão estrita do adendo, com igualdade de SHA verificada, preservando
ambos os históricos sem sobrescrita.

Cada época contém 32 minibatches de 64:

| Fonte | Exemplos por batch |
| --- | ---: |
| Sintéticos positivos | 20 |
| Referências positivas originais | 4 |
| Reais positivos fornecidos | 8 |
| Sintéticos/prefixos negativos | 16 |
| FLEURS DEV negativos | 8 |
| Reais negativos fornecidos | 8 |

São 32 positivos e 32 negativos. O sampler percorre permutações dos 91 IDs
positivos e 130 negativos antes de repetir cada classe; só depois escolhe uma
derivação de ganho. **Todos os 221 eventos devem aparecer em toda época**,
com verificação automática e registro privado de exposições. Há 256
apresentações de cada classe real por época, sem confundi-las com novos eventos.

Cada época foi exportada em subprocesso limpo pelo `export_positive.py`,
preservando o tamanho nativo de 39.552 bytes. A seleção usa exclusivamente o
replay streaming da validação fornecida e a validação sintética/pública
original. Prioridade: perdas pareadas de positivos do baseline, preservação
original, negativos da validação e BCE. Nenhum teste ou regressão antiga
escolhe checkpoint. Threshold 0,5, média de cinco scores, liberação abaixo de
0,3 e refratário de 16.000 amostras permanecem fixos.

## Modelos comparados

| Referência existente | SHA-256 do TFLite privado |
| --- | --- |
| Baseline | `f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514` |
| Melhor candidata anterior, A/época 2, NO-GO | `5b198451df030dbc2650cbfd20f9e29ed3ad88aecc52213d2dcf24e45f9594ff` |

Os hashes e a biblioteca host existente foram conferidos. O novo modelo,
seu SHA-256 e seu melhor checkpoint estão documentados nos resultados abaixo.

Depois de selar a escolha pela validação, os três modelos foram avaliados
nos mesmos eventos, por split, separando positivos reconhecidos/perdidos e
negativos rejeitados/reativados. Comparar LiteRT/núcleo nativo e registrar
scores, novos falsos contextos e diferenças de eventos. Os 23 positivos e oito
negativos antigos, o público completo de 35 min e o contínuo legado são
regressões já conhecidas, executadas somente depois da seleção.

Todos os 260 eventos já tiveram replay anterior. A exposição efetiva da comparadora anterior foi conferida: seus seis positivos
reais estão no treino atual; seus 52 negativos se dividem em 43 no treino,
quatro na validação e cinco no teste atual. Os splits não foram alterados. A independência temporal
não é garantida pelos splits fornecidos. Os 13 eventos de teste permitem uma
comparação exploratória; não comprovam comportamento em produção, recall de
todas as tentativas ou falsos positivos por hora. Homologação ARM64 física e
coleta prospectiva continuam necessárias para promover pesos.

## Reprodução

Usar o Python preservado:
`~/.cache/aegis-wakeword-poc/comparison/microwakeword/venv/bin/python`.
A raiz privada utilizada é
`~/.cache/aegis-wakeword-human-label-training/`; sempre usar destinos novos.

1. `prepare_direct_training.py --zip <ZIP-fornecido> --output
   <raiz>/preparation-v1` verifica os arquivos e prepara todos os eventos.
2. `evaluate_hardneg.py --model <baseline> --audit
   <preparation-v1>/audit/inventory.json --split-manifest
   <preparation-v1>/protocol.json --provided-splits --split validation
   --library <biblioteca-host> --output <raiz>/baseline-validation-v1`.
3. `train_direct_training.py --prepared <preparation-v1>
   --baseline-evaluation <baseline-validation-v1>/evaluation.json
   --output <raiz>/model-v1` executa o plano e sela `model-v1/selection.json`.
4. Para reproduzir o único follow-up registrado, repetir o comando de treino
   com `--conservative --output <raiz>/model-v2` (duas épocas, LR0,000003).
   Comparar os `best_key` dos dois relatórios apenas pela validação e selar a
   escolha global em `<raiz>/selection-v1.json`, conforme o manifesto preservado.
5. Somente após a seleção, repetir o avaliador com `--provided-splits --split
   all --legacy` para baseline, candidata anterior e novo modelo. As saídas
   privadas conservam resultados individuais e scores de ambos os runtimes.

## Verificações e limites

Quatorze testes locais com fixtures sintéticas passaram: inclusão de todos os
221 originais antes de repetição, balanceamento, preservação de rótulos/splits,
contextos curtos/silenciosos, frontend causal sem áudio posterior, CRC/hash/CSV,
interrupção por corrupção e política explícita de splits. Também passaram
`py_compile` e `git diff --check`. A preparação real conferiu 260 eventos,
520 WAVs PCM16 mono/16 kHz, hashes declarados, CSV/JSONL, CRC e sobreposição
exata de 200 ms em todos os pares. Foram geradas 663 janelas reais de treino,
273 positivas e 390 negativas, e 26 de validação; zero features de teste.

Os seis exports (quatro do primeiro fit, dois do segundo) carregaram no LiteRT
e na biblioteca nativa preservada sem alterar o runtime. Os originais e
checkpoints anteriores ficaram intactos. A validação comprovou exposição dos
91 IDs positivos e 130 negativos em **cada uma das seis épocas**. O checkpoint
selecionado da configuração conservadora já viu todos os 221 em sua primeira
época, não somente nas épocas descartadas posteriormente.

A comparadora anterior teve seis positivos reais, todos agora no treino,
e 52 negativos reais: 43 agora no treino, quatro na validação e **cinco dos
oito negativos atuais de teste**. Isso foi confirmado pelos draws efetivos
somente até sua época selecionada (2), sem contar épocas posteriores. A nova
candidata não recebeu os exemplos reservados por nenhuma fonte histórica:
o pool real antigo separado não foi reutilizado. A vantagem de exposição da
comparadora e a consulta anterior de todos os 260 tornam a avaliação
exploratória, mesmo com os novos splits preservados.

Não houve modificação de Android, backend, TTS, allowlist ou baseline,
publicação, instalação, merge ou deploy. WAVs, inventários sensíveis, features
e pesos permaneceram fora do Git. Homologação ARM64 física e coleta contínua
prospectiva continuam pendentes. Resultados e decisão seguem abaixo.

## Resultados por época

| Configuração/época | LR | Positivos val. LiteRT/nativo /11 | FP val. LiteRT/nativo /15 | Positivos originais val. | BCE original val. | Elegível |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| model-v1/1 | 0.000010 | 10/10 | 10/10 | 100.00% | 0.002269 | não |
| model-v1/2 | 0.000010 | 10/10 | 7/7 | 98.48% | 0.007352 | não |
| model-v1/3 | 0.000010 | 8/8 | 6/7 | 96.21% | 0.010569 | não |
| model-v1/4 | 0.000010 | 8/8 | 6/6 | 96.59% | 0.010362 | não |
| model-v2/1 **selecionado** | 0.000003 | 11/11 | 13/13 | 100.00% | 0.000663 | sim |
| model-v2/2 | 0.000003 | 10/10 | 12/12 | 100.00% | 0.001140 | não |

A seleção global foi selada em `selection-v1.json` antes do primeiro replay final: configuração conservadora, **época 1, LR 0,000003**. Ela preservou 11/11 chamadas da validação e todos os positivos da validação original; os checkpoints com menos FP sacrificavam chamadas. A segunda época conservadora também perdeu uma chamada e não foi escolhida. Não houve troca de pesos ou threshold após observar o teste.

O modelo selecionado já recebeu os 221 originais: 256 apresentações positivas e 256 negativas na primeira época, escolhendo IDs antes dos ganhos. Os fits completos tiveram quatro e duas épocas, sempre com 91/130 IDs por época. Augmentações e reapresentações não foram contadas como eventos adicionais.

## Comparação por split

Positivos são chamadas reconhecidas perto da decisão original (−500/+200 ms). Negativos são contextos **rejeitados sem evento em qualquer posição**. Valores principais LiteRT; divergências nativas estão indicadas. Treino é diagnóstico de ajuste, não holdout.

| Split e métrica | Baseline | Melhor anterior | Novo selecionado |
| --- | ---: | ---: | ---: |
| Treino — positivos reconhecidos | 91/91 | 90/91 | 90/91 |
| Treino — negativos rejeitados | 6/130 | 50/130 | 16/130 |
| Validação — positivos reconhecidos | 11/11 | 10/11 | 11/11 |
| Validação — negativos rejeitados | 2/15 | 4/15 | 2/15 |
| Teste — positivos reconhecidos | 5/5 | 5/5 | 5/5 |
| Teste — negativos rejeitados | 0/8 | 2/8 | 0/8 |

No nativo, o modelo anterior rejeita 51/130 negativos de treino e 1/8 de teste; o novo rejeita 15/130 no treino. As demais contagens da tabela coincidem nos dois runtimes. A margem temporal e o critério em qualquer posição produzem as mesmas contagens de contextos nesta comparação; o JSON também conserva eventos adicionais e pares por critério.

No teste, o novo manteve 8/8 contextos falsos ativos, contra 8/8 do baseline: **redução de 0% em contextos**. Os eventos totais nesses oito contextos caíram de nove para oito (11,11%), eliminando um evento adicional, sem rejeitar nenhum contexto. Na validação, ambos mantiveram 13/15: redução de 0%. No treino, 124→114 contextos perto da decisão no LiteRT (8,06% líquidos), e 124→115 no nativo (7,26%). Essa melhora no conjunto ajustado não demonstra generalização. O novo ainda perdeu uma chamada do próprio treino: 90/91 contra 91/91 do baseline.

A candidata anterior tinha mais rejeição de negativos, mas reconheceu somente 10/11 positivos de validação. Ela também já havia treinado cinco dos oito negativos atuais de teste, portanto não se interpreta sua vantagem ali como avaliação independente. Nenhuma observação foi removida para melhorar essas métricas.

## Regressões conhecidas, runtimes e controles

Os três modelos reconheceram 23/23 positivos antigos da PoC, tanto individualmente quanto no contínuo com prefixo público de 30 s e intervalos de 3 s sem reset. Nos oito negativos originais houve 1/8 contextos ativados em cada modelo. Nos 35 min públicos houve quatro eventos em cada modelo, nos dois runtimes. Esses conjuntos já eram conhecidos e não escolheram checkpoint.

Entre os 15 positivos da primeira coleta, baseline 15/15, anterior 14/15 e novo **15/15**, incluindo recuperação do antigo alias `old-P14`. Desses 15, 12 agora pertencem ao treino, dois à validação e um ao teste; não constituem um teste independente adicional. A perda de um positivo de treino do novo corresponde a outra gravação da coleta de 05/11.

| Modelo, 260 contextos | Maior diferença LiteRT/nativo em LSB | Contextos acima de 1 LSB | Contextos com ativação divergente | Contextos com evento fora de um stride/contagem distinta |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 21 | 215 | 0 | 0 |
| Melhor anterior | 20 | 222 | 2 | 2 |
| Novo selecionado | 22 | 220 | 5 | 5 |

O carregamento nativo passou nos seis exports. Cada TFLite mantém 39.552 bytes, 13.057 parâmetros, entrada INT8 `[1,3,40]`, saída UINT8 `[1,1]`, estado streaming e quantização original. A tolerância de 1 LSB falha inclusive no baseline e permaneceu inalterada. Carregar no host não comprova igualdade numérica ou homologação ARM64 física.

Os três modelos passaram no reset exato após estado sujo e em resets repetidos, comparados a instâncias limpas dentro de cada runtime. Silêncio de 30 s e ruído branco de 30 s em RMS 100/1000 não geraram eventos. O replay contínuo dos 260 contextos com intervalos artificiais de 3 s foi executado sem reset; nenhum positivo reservado de validação/teste reconhecido pelo baseline foi perdido nesse protocolo. É uma regressão de estado, não reconstrução da sessão original nem exposição para FP/hora. No agregado contínuo, o baseline reconheceu 105/107 e o novo 104/107; as perdas pareadas adicionais ficaram no split de treino.

## Decisão

**NO-GO.** O checkpoint conservador recuperou a chamada antiga difícil e preservou os positivos reservados, mas não trouxe redução dos falsos contextos na validação ou no teste. Uma chamada do próprio treino também deixou de ser reproduzida. A configuração mais forte reduzia negativos sacrificando positivos; não foi promovida por ter menos FP.

O experimento solicitado foi realizado com todos os dados humanos de treino. Os resultados não justificam alterar o modelo atual. O teste de cinco positivos e oito negativos é exploratório; não comprova desempenho em produção. Não houve instalação, publicação de pesos, alteração do Android, backend, TTS, allowlist ou baseline.

A amostragem 50/50 resolve a predominância numérica de uma classe, mas não garante o compromisso desejado. Nesta rodada, o passo conservador preserva sensibilidade com pouco efeito nos negativos; aumentar a adaptação produz perdas já na validação. Futuras investigações devem ser pequenas e separadas, por exemplo peso relativo das fontes ou adaptação das estatísticas BN, sempre com nova avaliação reservada. Não se acrescentou filtro acústico nem se alteraram rótulos para explicar ou contornar o resultado.

## Entregáveis privados e testes finais

Raiz: `/home/phc/.cache/aegis-wakeword-human-label-training/`.

| Artefato | Local / SHA-256 |
| --- | --- |
| Modelo selecionado, pesquisa NO-GO | `model-v2/aegis.int8.tflite` |
| SHA-256 selecionado | `c05d071f33465397d50216c6f1ab9af7a70b6f303ca689934239cae4be410567` |
| Melhor checkpoint | `model-v2/epoch-01/best.weights.h5; LR0,000003; baseline; Adam novo; BN congelada` |
| Outro fit preservado | `model-v1/aegis.int8.tflite; melhor época2; rejeitado na validação` |
| Preparação completa | `preparation-v1/audit/, protocol.json e features/` |
| Seleção pré-teste | `selection-v1.json e final-replay-launch-v1.json` |
| Resultados individuais privados | `eval-{baseline,previous,new}-final/evaluation.json e scores por frame` |
| Exposição da comparadora | `comparator-exposure-history-confirmation.json` |
| Controles | `controls-v1/controls.json` |
| Resumo seguro e pareados privados | `summary-v1/public_summary.json e private_summary.json` |

[Resultados estruturados versionados](wakeword-human-label-training-results.json) incluem as métricas por split/runtime, históricos dos dois fits e hashes de proveniência. Os dados de voz, IDs reais, WAVs, features e pesos permanecem privados. Os snapshots de fonte executada estão em `source-snapshots/`, com SHA verificado contra cada relatório de treino.

Verificação final: **33 testes passaram**, sendo 14 novos e 19 regressões dos helpers anteriores; compilação Python e `git diff --check`; auditoria real completa; seis exports/replays de validação nos dois runtimes; três replays finais completos e controles de estado. Nenhuma execução Linux foi apresentada como teste físico no Android.

A medição inicial de custo nativo teve fator tempo de processamento/tempo de
áudio de 0,00520 no baseline e 0,01210 no novo (2,33×). Ela foi preservada.
Uma checagem diagnóstica com os mesmos pesos e ruído, três repetições em ordem
alternada, produziu intervalos de 0,00678–0,01213 no baseline e
0,00685–0,00850 no novo. A variação no host não permite atribuir uma razão
estável ao modelo ou extrapolar para ARM64. Nenhum peso, threshold ou seleção
foi modificado por essa checagem. O relatório privado
`native-resource-diagnostic-v1.json` conserva todas as medições. Memória/RSS do
harness compartilhado também não isola a arena por modelo.

Pacote privado de pesquisa, explicitamente **NO-GO**:
`~/.cache/aegis-wakeword-human-label-training/aegis-human-label-training-research-2026-11-05.zip`,
SHA-256 `a118018f0070ee6278f97f5d2e80eb689d5821c7b86f0dad850db8597de1a7dd`.
Contém os dois TFLites, manifesto, seleção e resultados, sem WAVs, features ou
transcrições. Arquivos privados em 0600 e diretórios em 0700.
