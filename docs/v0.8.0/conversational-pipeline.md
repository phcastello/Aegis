# Pipeline conversacional — implementação e avaliação de 01/11/2026 UTC

O backend e a PWA foram implantados efetivamente na fonte
`350f892442acba5e1826ee60d376935c75a4fd49`. O caminho hands-free agora transmite
STT durante a captura, consulta memória semântica por decisão do modelo e pode
sintetizar unidades finais antes de terminar a geração. Android manual e
hands-free compartilham o player nativo. **A meta de quatro segundos ainda não
foi atingida nem comprovada no telefone.** Há falhas de qualidade explicitadas
abaixo; publicação não representa aprovação dessa meta.

A [distribuição publicada e recomendada](https://github.com/phcastello/Aegis/releases/tag/node-v0.8.0-unstable.6)
é `0.8.0-unstable.6`, código Android `8000006`, fonte `350f892`, pelo workflow
[41257700322](https://github.com/phcastello/Aegis/actions/runs/41257700322).
A conferência final da publicação e dos artefatos está em
[conversational-pipeline-publication.json](conversational-pipeline-publication.json).
Os bytes Android/Windows, checksums, assinaturas, certificado, manifesto, feed,
ledger, JNI e marcadores DEX foram conferidos independentemente após sucesso
do workflow. A atualização está no fluxo normal de Configurações → Atualizações;
não foi instalada ou detectada fisicamente no aparelho do proprietário.

## Configuração efetiva e escopo

O modelo permanece **`gpt-6-luna`**, `reasoning.effort=low`,
`service_tier=auto`, `max_output_tokens=7000`, `store=false`. Não foi imposto
verbosity ou limite menor de resposta. Defaults de opções, JSON de produção/
desenvolvimento, `.env.example` e default Compose foram atualizados. A variável
**ativa** `AEGIS_CHAT_REASONING_EFFORT=medium` foi identificada e reconciliada
para `low`, incluindo a configuração operacional e seu arquivo de ambiente.
Somente essa variável mudou em relação ao ambiente anterior. As configurações
independentes de título e extração permaneceram iguais.

O smoke HTTPS de produção conferiu também o payload realmente auditado pelo
provedor: modelo, low, auto, 7.000 tokens e store=false. Testes cobrem caminhos
síncronos, streaming e loops de ferramentas compartilhados com texto e voz.
Medium aparece nos dados apenas como baseline experimental em infraestrutura
descartável, nunca como fallback de produto.

Não houve Fast/Priority, modelo alternativo, preparação especulativa, conversa
em parcial, reescrita do pedido por outro LLM, resposta de intenção ou raciocínio
em background. Identidade, personalidade e ordem natural da resposta foram
preservadas. Não houve alteração de pesos da wake word, parâmetros/algoritmo
do VAD, canais de notificação ou mastering do TTS.

## Contratos implementados

O [relay STT](hands-free-realtime-stt.md) usa a identidade nativa e mantém a
chave permanente ElevenLabs no servidor. A conexão só abre após ativação ou
fala aceita na continuação. Recebe o pre-roll e o PCM da mesma captura, sem
reabrir o microfone. O VAD local aprovado pede commit. O provedor confirma
`scribe_v2_realtime`, PCM mono 16 kHz, `no_verbatim=false` e commit manual;
não acrescenta um segundo endpointing remoto.

Resultados confirmados são agregados na ordem, sem repetir o evento
complementar com timestamps nem eliminar repetições legítimas. Só a conclusão
depois do commit local pode iniciar conversa. Há uma sessão por Node, quatro
globais, até 23 s de PCM, 40 s totais, 8 s de conexão/configuração e até 10 s
para commit, sem estender o prazo total. Falha técnica encerra realtime e
permite um único batch com o WAV capturado. Não há corrida permanente entre
batch e realtime. Cancelamento, identidade alterada e callbacks antigos não
autorizam outro turno. O cancelamento libera capacidade antes do close ACK.

Um probe real de 589 ms confirmou que commit curto funciona sem adicionar
áudio ou espera. A transcrição desse probe não foi correta, portanto ele prova
somente o contrato de duração. Não foi introduzido um piso artificial de dois
segundos. Keyterms adicionais do realtime não foram habilitados: a documentação
prevê cobrança adicional e não havia evidência de corrigirem as falhas observadas.

No hands-free, a primeira chamada conserva histórico, horário, identidade,
instruções, contexto observado existente e estados pendentes. Apenas a busca
semântica automática obrigatória foi retirada. O próprio modelo usa
`memory_search` na rodada existente quando necessário. Texto mantém sua busca
automática anterior. Indexação, grafo, validade temporal, persistência e escritas
posteriores continuam funcionando. Não foi criado perfil pessoal inventado ou
ampliado o cache exato de embeddings.

O [contrato de saída](hands-free-backend.md) aceita `voiceSegments=true` na
rota streaming. A função terminal `hands_free_reply_segments` contém strings
finais ordenadas e a decisão continue/close na mesma geração. Somente strings
JSON inteiramente fechadas e validadas são liberadas. Ferramentas normais
continuam antes da chamada terminal; `parallel_tool_calls=false`, schema estrito
e validação final proíbem combinar resposta terminal com ações. Comentários,
raciocínio, tokens soltos e argumentos incompletos não são falados.

Cada unidade é persistida antes de `final_segment`, com IDs de turno, conversa,
mensagem, segmento e sequência. O endpoint de voz aceita esses IDs, não texto
arbitrário do cliente. Conteúdo emitido é imutável e deve coincidir com a
conclusão do provedor. Falha mantém o prefixo persistido com estado interrompido
e cancela o restante. Rejeitar replay não cancela uma requisição legítima que
já possua aquele ID. Não houve migration.

O backend consome **o endpoint existente `/v1/aegis/speech`**, com ULID próprio
por unidade, prioridade/política existentes e PCM s16le mono 24 kHz. O serviço
`aegis-tts` não foi editado, implantado ou reiniciado por esta implementação.
Cancelamento upstream tem prazo limitado mesmo com HttpClient infinito.

Android mantém dois slots de síntese, atual mais uma unidade antecipada,
pipes de 24 kB e um único writer/AudioTrack contínuo. O terceiro request espera
capacidade; não se sintetiza toda a resposta de uma vez. A síntese seguinte pode
começar enquanto o player ainda reproduz a anterior. Cancelamento fecha calls,
pipes e áudio pendente. A continuação começa somente depois do fim textual,
das unidades válidas e do playback head final, mantendo dois segundos para
início de fala. O ACK de conclusão não bloqueia essa janela.

## Volume e ciclo de vida Android

Manual e hands-free agora usam o mesmo `HandsFreePlayback`, atributos
`USAGE_MEDIA/CONTENT_TYPE_SPEECH`, foco, PCM e ganho unitário. No Android, o chat
manual não passa pelo ganho/categoria diferentes do Web Audio. PWA e Windows
mantêm o fallback existente. Não se altera o volume global do telefone nem se
cria outro controle de volume de resposta. O cue mantém seu PCM e ganho aprovado.

O teste com **o mesmo PCM salvo** comprova igualdade de bytes pelos dois
caminhos de software; não compara sínteses independentes. A comparação no
mesmo telefone, rota e volume ainda falta. Os testes de media policy e o código
compartilhado não substituem essa medição acústica ou os botões físicos.

O player nativo libera o token exato do lease após drenar/cancelar, inclusive
quando o WebView está suspenso. Uma conclusão nativa ou callback JS antigo não
libera um lease posterior. Não foram recriados canais para contornar a
notificação ocultada pelo proprietário.

## Método e limites das medições

O baseline histórico está em [hands-free-latency.md](hands-free-latency.md).
Esta rodada começou na branch atualizada `86e2409`, na worktree já existente
`/home/phc/Aegis-hands-free`; a worktree original e o trabalho posterior foram
preservados. Foram usados serviços/provedores reais, bancos e identidades
sintéticos descartáveis, sem copiar conversas, memórias ou contas do proprietário.
As operações reais de ferramentas foram de leitura. O corpus, os prompts e os
áudios permanecem privados; as tabelas usam IDs opacos.

Os tempos abaixo são **simulação de cliente no servidor**, não emulador ou
telefone. Usam `perf_counter` local e correlação por turno. Os tempos internos
da API usam seu próprio relógio monotônico e são apresentados separadamente;
não se subtraíram relógios de máquinas distintas. Primeiro byte, primeiro PCM
e primeiro sinal digital (`abs(PCM16)>=64`) são medidas diferentes. Não se mede
o primeiro fonema no alto-falante.

Para representar o endpoint, o harness espera os 800 ms aprovados após o fim
do WAV, sem enviar padding ao STT. A coluna principal soma a cauda digitalmente
quieta que já estava no WAV: última amostra de entrada acima do limiar → primeiro
sinal de saída. Assim, inclui **cauda real mais 800 ms simulados**; é uma
estimativa conservadora, não a execução do VAD Android. Não foi reduzido
endpointing para favorecer o resultado. A instrumentação para medir o percurso
real no aparelho está em [marcos Android](hands-free-client-latency.md).

Cada etapa comparativa reiniciou somente a API descartável, incluindo sua
primeira amostra/JIT nos resultados e zerando o cache exato em RAM. Não se
reiniciou ou aqueceu periodicamente o TTS. Cache de prefixo do provedor continua
sendo comportamento normal e está contabilizado no uso. A etapa final usa
**cinco perguntas reservadas inéditas**, sem cache exato prévio. O harness de
servidor baixa unidades sequencialmente; ele não homologa o prefetch/player
Android. A partir de `45988f2`, o helper distingue fila de início efetivo HTTP;
nos registros antigos de unidades posteriores, `requestAtMs` era tempo de fila,
não prova de início da síntese.

No replay realtime do harness, a conexão fica pronta antes de começar o WAV
ritmado. Isso não reproduz o caso em que o handshake nativo ultrapassa o fim
de uma fala curta. `connectReadyMs` é informado separadamente; estes números
não certificam latência de comandos curtos após a wake word. O aplicativo
real guarda áudio enquanto conecta, preserva o pre-roll e não aguarda readiness
para continuar capturando.

O TTS mudou independentemente durante o trabalho:

| Período | SHA exposto | Variante exposta |
| --- | --- | --- |
| Primeiros baseline/low | `ba1beb9eff78eedf1df1b71ceb94b8539bccad43` | DSP `stable` |
| Etapas intermediárias | `04f884045a35e354e0c86e9d68e36ae01d69f84f` | `stable-codec22x22-onset-conservative-v1` |
| Matriz renovada e smoke implantado | `1749f5fb29320dced0a16add618989e36954ee7b` | `stable-codec22x22-onset-conservative-v1-startup320ms` |

Houve readiness 503 durante a atualização externa. Onze tentativas completas
falharam no HTTP de fala; foram preservadas. A matriz foi interrompida, e o
harness passou a verificar readiness antes de gastar novas chamadas. Um piloto
de recuperação também está preservado. As comparações centrais abaixo usam
somente a matriz renovada, cujo SHA/variante TTS permaneceu igual antes/depois
de cada etapa. Não se atribuem à aplicação ganhos entre versões TTS diferentes.
Imagem/digest e hardware do TTS não foram inspecionados administrativamente.

## Resultados, incluindo falhas

Milissegundos da estimativa principal; nenhuma amostra lenta foi removida.
Os cinco primeiros conjuntos usam quatro itens comparáveis; o último é outro
conjunto de cinco perguntas inéditas, incluindo uma resposta maior. N pequeno
e ordem fixa não sustentam percentis, garantia máxima ou atribuição percentual
precisa entre essas duas populações. Todos os tempos, inclusive respostas
incorretas, estão mantidos; qualidade é informada separadamente.

| Configuração | N | Média | Mediana | Máximo | Acima de 4.000 ms | Qualidade da intenção/resposta |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline medium, prefetch, batch, texto inteiro | 4 | 7.291 | 7.092 | 9.282 | 4/4 | 3/4 |
| Low, restante igual | 4 | 6.516 | 6.235 | 8.539 | 4/4 | 3/4 |
| Low + memória sob demanda, batch | 4 | 5.768 | 5.662 | 7.032 | 4/4 | 3/4 |
| Low + memória sob demanda + realtime, texto inteiro | 4 | 5.378 | 5.322 | 6.160 | 4/4 | 3/4 |
| Low + memória sob demanda + fala incremental, batch | 4 | 5.696 | 5.494 | 7.668 | 4/4 | 3/4 |
| Conjunto final, perguntas inéditas | 5 | 4.807 | 4.884 | 5.557 | **5/5** | **4/5** |

| Item final reservado | Último sinal de entrada → primeiro sinal de saída (ms) | Resultado |
| --- | ---: | --- |
| c01 | 5.411,6 | Correto; primeira API descartável |
| c02 | 4.004,5 | Correto |
| c03 | 4.180,2 | Erro aritmético do modelo |
| c04 | 5.556,6 | Consulta de leitura correta |
| c05 | 4.883,7 | Três unidades finais corretas |

O último caso recebeu o primeiro segmento em 2.742 ms e primeiro sinal em
4.423 ms após o fim do WAV. No relógio interno da API, o primeiro segmento foi
persistido em 1.712 ms e o provedor concluiu em 2.220 ms: há unidade final e
solicitação TTS antes do fim da geração, sem chamada por frase. Não se afirma
que o primeiro PCM chegou antes desse fim. O `done` recebido pelo harness em
6.087 ms sofreu backpressure do próprio leitor e não mede duração do modelo;
esse problema do helper foi corrigido e testado sem novas chamadas pagas.
A métrica conservadora soma os 461 ms de cauda de entrada desse caso.

A preparação não exige mais embedding remoto em pedidos simples hands-free.
Na matriz renovada, preparação média passou de 780 ms no baseline e 653 ms
com low/prefetch para 50 ms com memória sob demanda. Houve quatro embeddings
no caminho de cada conjunto com prefetch e zero nos conjuntos sob demanda;
os cinco pedidos inéditos também tiveram zero, com preparação média de 44 ms.
Esse ganho não depende de acertar o cache exato de embeddings.
As durações de preparação, rodadas/ferramentas, primeiro segmento, TTS/PCM e
total por turno estão nos artefatos. O commit residual realtime final ficou em
aproximadamente 192–251 ms nesta amostra; handshake aconteceu durante a fase
ativa, sem serviço pago passivo. A meta máxima não é substituída por uma média:
**os cinco casos finais ultrapassam quatro segundos nesta definição, antes de
medir transporte/reprodução no telefone**.

Dados completos e sanitizados:
[resultados JSON](conversational-pipeline-results.json),
[todas as amostras CSV](conversational-pipeline-samples.csv) e
[etapas internas CSV](conversational-pipeline-stages.csv).

## Correção, transcrição e memória

A revisão de todas as 60 tentativas encontrou 40 respostas corretas, seis
falhas de intenção com transcrição ambígua, duas falhas de transcrição, um erro
aritmético e onze casos sem qualidade observável por falha HTTP. Não se contam
esclarecimentos provocados por transcrição ruim como acerto de latência útil.

No eSpeak, realtime corrompeu duas frases que batch reconhecia; o modelo pediu
esclarecimento, sem executar ações. Com Qwen, os números foram preservados,
mas seis versões de um caso de autocorreção tiveram pontuação/escopo de negação
ambíguos e produziram a operação errada. Isso ocorreu também no baseline medium;
a causa não pode ser atribuída somente ao low. A presença dos mesmos números
não comprova preservação da intenção. Os WAVs são sintéticos e não substituem
avaliação de fala humana no telefone.

No conjunto final reservado, o modelo recebeu os operandos corrigidos e
afirmou resultado **56 onde o correto era 46**. Esse conteúdo veio da resposta
terminal final do provedor; parser, persistência e TTS não o reescreveram.
O caso permanece como limitação concreta. Não se voltou para medium, não houve
correção silenciosa depois da fala nem se ajustou o corpus para apagar a falha.

Oito turnos adicionais de avaliação de memória/conversa passaram em sete
verificações iniciais: preferência implícita, referência indireta/ausência,
fato corrigido, fato esquecido, data relativa, conversa natural e continuação
na mesma conversa. O caso histórico falhou: a busca atual não trouxe o fato
expirado, e o modelo não refez a consulta com `asOf`. O contrato foi esclarecido
para comparar atual/passado em consultas separadas, sem confundir ausência
atual com ausência histórica ou substituir evidência por fato de outra pessoa.

Uma pergunta histórica reservada nova, com a imagem final implantada, passou:
duas buscas selecionaram os registros canônicos atual e histórico corretos,
com mapeamento temporal revisado. Usou três chamadas low em **8.454 ms de texto**,
sem STT/TTS. A rodada extra necessária é contabilizada separadamente, e a
falha original foi mantida. Um caso de esquecimento também expôs relevância
fraca: o fato esquecido foi omitido corretamente, mas a resposta ofereceu
um fato de outra entidade. Não há afirmação de qualidade irrestrita da memória.

## Verificação e custo

| Verificação | Resultado |
| --- | --- |
| Backend final completo | 790 aprovados, 100 opt-in externos ignorados, zero falhas |
| PostgreSQL descartável real | 38 aprovados, zero skips/falhas |
| Android JVM com Kotlin de produção | 78 aprovados, zero falhas |
| Android ARM64/R8 | Código minificado verificado com chave descartável de teste, métodos refletidos/JNI preservados |
| Node / PWA | 163 / 94 aprovados; builds e typechecks aprovados |
| Rust portátil / ACL | 90 aprovados, dois opt-in existentes ignorados |
| Rust Android ARM64 | `cargo check` com Rust 1.94 e NDK 29 aprovado |
| Contratos de distribuição/IPC | 53 aprovados |
| Publicador / Compose | 32 aprovados; composição validada |
| Workflow publicado | Android/Windows e assinatura aprovados; instalação/WebView/perfil Windows verificados no runner |

São verificações de software, HTTP/WS real em loopback e recursos descartáveis;
não certificam áudio físico, bateria, tela bloqueada ou Windows físico. Os
testes incluem fallback único, dados fragmentados, EOF, backpressure, duas
sínteses limitadas, cancelamento, identidade, replay, persistência interrompida,
confirmações, ferramentas, datas e validade temporal.

Os experimentos descartáveis registraram **69 auditorias / 89 HTTPs do modelo
principal**, incluindo baselines, falhas, pilotos e nove turnos de qualidade.
Há uso deduplicado conhecido de 71 chamadas e uso ausente de 18: **940.329 tokens
de entrada, 829.477 cached, 44.390 de escrita de cache e 2.869 de saída**. Não
inclui integralmente títulos, extração, embeddings, STT e TTS; não se inventou
um total monetário nem uma redução percentual de fatura. O teste histórico
necessário consumiu 43.540 tokens de entrada e 153 de saída, incluídos nesses
totais. O smoke de produção é uma chamada adicional separada.

Nove sínteses adicionais geraram os WAVs de entrada Qwen. Duas requisições de
fixture foram rejeitadas antes da síntese por ID inválido; as requisições reais
do aplicativo usam ULIDs válidos. Segmentação não acrescenta chamadas LLM por
frase; pode acrescentar requests TTS, limitados a atual mais um antecipado.
Realtime acrescenta uma conexão paga por fase ativa de fala, não durante a
espera pela wake word. Não foram adicionados polling/profiling detalhado ou
chamadas de aquecimento ao uso cotidiano. Consumo ativo/bateria ainda não foi
medido no telefone.

## Produção, recuperação e pendências físicas

Operação concluída de backend/PWA:
`20261101T003658Z-81d6cfce`. Imagens ativas verificadas:

- API: `sha256:d583b6278841d785bc7b3486f96ddc6ba456397571ad7c16afd3f0013c5243bf`.
- PWA: `sha256:3d0ee01ffc2745462294ef76b8bf39fca88a0de1dff7a8a17447787ec5103e08`.
- Revisão dos dois componentes: `350f892442acba5e1826ee60d376935c75a4fd49`.

O publicador foi executado com SHA fixo e recriou os containers com imagens
novas. Não se apresentou reinício da imagem anterior como implantação. HTTPS,
service worker, API v1 e observação dos workers passaram. Não havia migrations
pendentes; não houve migrador, backup/restauração desnecessários ou reinício dos
bancos/TTS. Configuração, mounts, volumes, certificados e pareamento foram
preservados. O profiling detalhado de produção continua desligado.

Dois preflights anteriores pararam antes de alterar containers: classificação
do template de ambiente e reconciliação do Compose. O publicador agora reconhece
apenas a troca exata do default medium→low quando o ambiente ativo já é low;
qualquer outro diff continua bloqueado. A reconciliação registra hashes e
valor efetivo, sem contornar a proteção de mudanças de infraestrutura.

O smoke atravessou **o HTTPS e os proxies reais** com WS autenticado, transcrição
final, conversa, segmento autorizado e PCM válido. Acesso sem identidade foi
401. O primeiro segmento chegou antes do fim textual; primeiro sinal ficou em
5.267 ms após o fim do WAV, com commit residual de 266 ms. É evidência funcional
do deploy, não ganho por pergunta inédita ou medição de alto-falante. O payload
real foi low/auto/7000/store=false. A conversa e o Node sintéticos foram
removidos, sem memória pessoal criada. Os proxies existentes já preservavam
upgrade, Authorization, buffering desativado e timeouts; não se enfraqueceu sua
configuração global.

A stack experimental foi encerrada por IDs/projeto próprios: quatro containers,
três volumes anônimos sintéticos e sua rede removidos, arquivo Compose de
credenciais apagado. Volumes/containers de produção foram preservados. Nenhum
`down -v`, force push ou merge na main foi executado.

Rollback de API/PWA mantém o ambiente low e as imagens anteriores congeladas
na operação privada acima. Procedimento documentado, não executado:

```sh
docker compose --project-directory /home/phc/Aegis -p aegis \
  -f /home/phc/.local/state/aegis/publish-dev/20261101T003658Z-81d6cfce/previous.compose.private.json \
  up -d --no-deps --no-build aegis-api aegis-pwa
docker exec aegis-pwa nginx -s reload
```

A API anterior é `24349fcc66ae…`, fonte `a7759e9`; PWA anterior
`caa387d38b99…`, fonte `fec7c18`. Não há downgrade de esquema. Clientes anteriores
mantêm batch, chat/voz legados e API/dados v1. O novo fluxo segmentado requer
backend com esse suporte; uma recuperação nativa deve reconstruir a fonte
funcional de `unstable.5` (`a7759e9`) com versão/código monotônico pelo
[mecanismo existente](../distribuicao-e-ci.md), preservando assinatura e dados.
Não se instala um APK histórico de código inferior. Nenhuma recuperação foi
publicada preventivamente.

Ainda precisam de homologação física: atualização sobre a instalação existente,
mesmo PCM/rota/volume pelos dois modos, botões físicos, alto-falante e prosódia
entre unidades, fala humana curta/autocorreções/nomes, bloqueio/retorno/rede e
rotas Bluetooth, e impacto de conexões nos períodos ativos. Exportar métricas
pelas configurações hands-free; os marcos monotônicos e a confirmação de head
permitem medir o percurso real sem profiling permanente. A meta de quatro
segundos e as falhas de qualidade acima permanecem pendentes.

## Commits

Todos na branch `feat/v0.8.0-hands-free`, sem merge na main:

| Responsabilidade | Commit |
| --- | --- |
| Reasoning low | `71311e3` |
| Relay STT | `2088a97` |
| Segmentos finais e memória sob demanda | `5325329` |
| Propriedade de requests / cancelamento limitado | `2ea7ea9`, `d0094d0` |
| Android streaming, player único e prefetch | `7db93b1` |
| Recurso realtime / lease nativo | `c416fb5`, `573f5da` |
| Contrato histórico de memória | `463b589` |
| Medição, readiness e clocks independentes | `3b64571`, `4c0b164`, `45988f2`, `3f6c5bf` |
| Classificação e reconciliação do publicador | `01ed04b`, `350f892` |

A revisão distribuída `350f892` contém as mudanças de execução. `45988f2` e
`3f6c5bf` corrigem somente a instrumentação do helper de medição; não alteram
os aplicativos implantados.
