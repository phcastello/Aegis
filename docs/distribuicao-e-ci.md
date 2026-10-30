# Distribuição, atualizações e estágios de CI

Para disponibilizar desenvolvimento para teste, use a operação única
[publish-dev](publish-dev.md). Ela coordena API/PWA privadas e, quando o diff afeta
aplicativos, reserva, build, assinatura e recomendação em uma execução autônoma,
sem aprovação interativa. Os comandos separados descritos abaixo permanecem para
homologação focada, estáveis e manutenção explícita.

A Aegis tem um único fluxo recomendado para Nodes compatíveis. Previews,
estáveis e rollbacks descrevem eventos de distribuição; não são canais
selecionáveis. Publicar torna a distribuição disponível. Cada usuário autoriza
a instalação no seu Node com interface.

## Estado verificado e transição

O PR #20 foi integrado à `main` em `1170881`. A primeira preview,
`0.7.2-unstable.1`, código Android `7002001`, foi produzida pelo
[run 40038742314](https://github.com/phcastello/Aegis/actions/runs/40038742314),
fonte `ee95f43`. Ela usa o contrato anterior e só existe como artefato do Actions.
As releases nativas públicas inspecionadas em 23/10/2026 são `node-v0.7.0` e
`node-v0.7.1`. O feed legado recomenda `0.7.1`, código `7001999`.

Em 27/10/2026, antes da nova publicação, ledger e feed recomendam
`0.7.2-unstable.2`, código `7002002`, fonte `64dc334`. A reserva
`0.7.2-unstable.3`, código `7002003`, fonte `da567b9`, permanece consumida:
o [run 40469210800](https://github.com/phcastello/Aegis/actions/runs/40469210800)
compilou ambas as plataformas e falhou antes da assinatura. Não reutilizar essa
reserva nem reexecutar o run antigo. O resultado de cada distribuição nova deve
ser conferido no feed; editar controles não demonstra publicação concluída.

| Instalação | Migração |
| --- | --- |
| v0.7.1 Android e Windows | Continua no feed histórico. Pode receber uma futura **estável** com o updater v2 pelo aplicativo. Para entrar antes nas previews v2, exige uma única instalação manual assinada, por cima da existente. |
| `0.7.2-unstable.1` do primeiro run | Ainda usa o updater anterior e exige a mesma instalação única para receber previews v2. |
| Primeira distribuição contendo updater v2 | Consulta o recomendado; próximas previews, estáveis e recuperações compatíveis voltam ao fluxo interno. |
| Previews históricas `stage.N`/`unstable.N` anteriores | O parsing de origem é preservado. A migração depende de uma estável com versão e código superiores, ou da instalação manual única assinada compatível. |

Nunca colocar previews ou rollbacks no feed histórico
`https://github.com/phcastello/Aegis/releases/download/node-preview/latest.json`.
Clientes recentes o filtram como estável e clientes mais antigos podem não filtrar.
Ele permanece como ponte de compatibilidade, sem seleção na interface.

## Contrato recomendado

Android e Windows consultam o mesmo endereço fixo:

`https://raw.githubusercontent.com/phcastello/Aegis/aegis-distribution/latest.json`

Os instaladores permanentes ficam em releases `node-v<version>` e têm URLs
exatas e versionadas. Artefatos do Actions são somente intermediários para
homologação e promoção; sua retenção não define a disponibilidade do updater.
Não usar links de ZIPs do Actions no manifesto.

O manifesto mantém os campos do updater Tauri (`version`, `notes`, `pub_date`,
`platforms.windows-x86_64`) e a extensão `android.aarch64`. O contrato v2 exige:

- `schema: 2`, `version`, `versionCode`, `kind: normal|rollback`;
- `sourceCommit`, `sourceVersion`, `publishedAt` igual a `pub_date`;
- `compatibility: { api: 1, nodeData: 1 }`, contratos de protocolo e persistência;
- `rollbackOf` e `restoresVersion` somente para recuperação;
- URL fixa de cada plataforma, SHA-256 dos instaladores e assinatura Windows;
- `android.aarch64.versionCode` igual ao código interno da distribuição.

`sourceVersion` é a versão original do checkout; `restoresVersion` identifica
a distribuição funcional selecionada. Podem divergir: uma preview pode ter sido
produzida de um checkout cujo package.json ainda dizia `0.7.2`. O SHA efetivo é
registrado no plano, manifesto e runtime compilado, sem depender das notas livres.
`publishedAt` é a data fixa atribuída à distribuição no plano de build/assinatura,
não o instante de avanço do feed; permanece idêntica em promoções e retries.

Os clientes exigem SemVer superior, código interno superior ao instalado,
metadados consistentes, compatibilidade reconhecida e URLs exatas do repositório.
Android verifica também hash dos bytes, pacote, versão, código e certificado
oficial antes de abrir o instalador. Windows preserva a verificação criptográfica
do plugin Tauri com a chave existente e verifica o SHA-256 dos mesmos bytes antes
de liberá-los para instalação. Nenhum caminho apaga dados, identidade,
NodeId, pareamento, cofre ou preferências.

## Versionamento e recuperação

A versão SemVer identifica a distribuição. O código interno é uma reserva
separada e monotônica. As versões novas aceitas são `X.Y.Z`,
`X.Y.Z-unstable.N`, `X.Y.Z-unstable.N.rollback.M` e `X.Y.Z-rollback.M`.
Eventos positivos são limitados a 65535; não se aceita build metadata para
precedência nem rollback aninhado.

Uma preview defeituosa `0.7.2-unstable.5` é recuperada por
`0.7.2-unstable.5.rollback.1`. Recuperar essa recuperação gera
`0.7.2-unstable.5.rollback.2`, com `rollbackOf` apontando para o evento anterior.
A evolução normal continua em `0.7.2-unstable.6`.

Uma estável defeituosa `0.7.2` é recuperada por `0.7.3-rollback.1`, seguida,
se necessário, de `0.7.3-rollback.2`. É uma recuperação explícita do evento
anterior, não uma correção funcional `0.7.3`. A próxima preview normal continua
em `0.7.3-unstable.1`. Todas essas ordens são verificadas em JS e Rust/SemVer.
No limite nativo de patch 999, uma recuperação de estável exige procedimento
revisto para outra linha; o publicador bloqueia a geração automática.

O NSIS do Tauri conserva a versão SemVer de exibição/comparação e usa
`major.minor.patch.0` no campo numérico `VIProductVersion`; esse campo não
substitui a comparação SemVer do updater. O comportamento consta do
[código da versão de CLI utilizada](https://github.com/tauri-apps/tauri/blob/tauri-cli-v2.12.1/crates/tauri-bundler/src/bundle/windows/nsis/mod.rs).
A instalação real de recuperação em Windows ainda exige homologação.

Reservas verificadas em 27/10/2026, antes da nova publicação:

| Evento | Distribuição | Código interno |
| --- | --- | --- |
| Estável publicada | `0.7.1` | `7001999` |
| Preview histórica | `0.7.2-unstable.1` | `7002001` |
| Preview v2 recomendada | `0.7.2-unstable.2` | `7002002` |
| Reserva consumida pelo build cuja assinatura falhou | `0.7.2-unstable.3` | `7002003` |

Sem novas reservas intermediárias, a próxima preview é `0.7.2-unstable.4`,
código `7002004`. Esse número só se torna efetivo quando o workflow grava a
reserva; sempre consultar o ledger. Recuperações publicadas também consomem
códigos novos. Não editar códigos de APKs históricos.

## Reserva e publicação resistentes a falhas

A branch de estado `aegis-distribution` contém `ledger.json` e, após primeira
publicação, `latest.json`. Cada reserva é adicionada ao ledger com versão,
código, SHA, run, controle confiável e procedência. Não há serviço ou banco novo.
O piso inclui releases e runs admitidos anteriores, inclusive o primeiro run.

A reserva é gravada **antes** da compilação. Uma falha ou cancelamento não
devolve a versão nem o código. Nova tentativa de build usa nova distribuição;
não reexecutar assinatura com o mesmo nome para produzir bytes diferentes.
A promoção pode ser repetida com os mesmos artefatos assinados.

O contador avança a partir do maior código conhecido, preservando o slot
histórico `999` das estáveis. Prereleases não o consomem; se esgotarem os códigos
inferiores, o plano bloqueia e exige outra linha de versão. O limite Android é
`2100000000`. A alocação de uma estável exige seu slot histórico superior a
todas as reservas, preservando a migração dos clientes antigos.

Os workflows compartilham concorrência e a gravação usa commits com pai fixado
e avanço de ref com `force:false`. Duas reservas concorrentes não sobrescrevem
uma à outra; o perdedor relê o ledger e valida novamente. O mecanismo segue o
[contrato GitHub de atualização fast-forward](https://docs.github.com/en/rest/git/refs#update-a-reference).
Preservar a branch e seu histórico; não apagar ou retroceder o ledger para
reaproveitar códigos. Restringir alterações administrativas dessa branch ao
publicador e ao proprietário, sem reescrever seu histórico.

A promoção cria ou completa um draft versionado, recusa bytes diferentes em
assets existentes e baixa os bytes publicados para compará-los com os arquivos
verificados. Só depois torna a distribuição pública e avança o recomendado.
Falha parcial deixa o draft retomável e o último recomendado intacto. Se a
release já estiver pública, a repetição verifica os mesmos bytes e retoma
somente o que falta. Código novo jamais é uma simples renomeação de APK antigo.

Estáveis também atualizam o feed legado, após verificar a distribuição completa.
Esse asset histórico ainda usa a operação antiga `--clobber`, que não é atômica
no GitHub: falha após exclusão pode deixá-lo temporariamente ausente. Repetir a
promoção dos mesmos bytes recupera esse feed. O ponteiro recomendado v2 não
sofre essa janela e permanece válido. Previews e recuperações não tocam o legado.

## Publicação de desenvolvimento e manutenção manual

A rotina é [publish-dev](publish-dev.md): uma identidade autorizada solicita a
publicação, o comando valida a implantação privada e despacha o workflow da
`main`. SHA, implantações e contratos API/dados são registrados na preparação e
conferidos pelo plano confiável. Não há heartbeat, status com TTL nem consulta
contínua ao servidor privado. Uma execução reserva, compila, verifica, assina,
publica os arquivos imutáveis e atualiza o feed; o processo local já pode ter
terminado. Push e PR não publicam automaticamente.

O environment `aegis-native-signing` mantém as chaves oficiais e sua allowlist
exata de `main` e tags `node-v*`, sem reviewer obrigatório. A solicitação explícita
é verificada pelo controle confiável; não há aprovação de assinatura ou promoção
na rotina de desenvolvimento. Mudanças administrativas são feitas na configuração
do GitHub, preservando restrições de branch e segredos. A variável pública
`AEGIS_ANDROID_FIREBASE_CLIENT_B64` contém apenas o cliente Firebase Android
existente, nunca service account.

O SHA de fonte é registrado por completo e deve pertencer à branch indicada do
mesmo repositório. O build tem token somente de leitura e não recebe as chaves:
Android usa chave descartável e Windows gera NSIS sem assinatura oficial do
updater. Runners separados executam somente código confiável da `main` para
verificar/reassinar os bytes; instaladores candidatos não são executados no job
de assinatura. Os finais incluem instalador, `preview.json`, `SHA256SUMS` e
assinatura Windows. Assinatura do updater Windows não é Authenticode nem remove
SmartScreen.

Confira ledger, releases e runs admitidos antes de escolher uma distribuição.
Uma falha consome a reserva; nova compilação exige versão/código novos. Nunca
reutilize uma reserva antiga para bytes diferentes ou reexecute um run antigo
para produzir outra assinatura. Artefatos expirados exigem uma nova distribuição.

Para homologação focada ou preparação explícita de uma estável, o caminho manual
continua disponível; ele não substitui a rotina de desenvolvimento:

```bash
gh workflow run aegis-native-preview.yml --repo phcastello/Aegis --ref main \
  -f sha=<SHA-completo-revisado> -f branch=<branch-do-repositorio> \
  -f version=<nova-versao-nao-reservada> -f kind=normal -f platform=both
```

Sem `publish_dev=true`, esse caminho produz artefatos assinados e não recomenda
uma atualização. `platform=android` ou `windows` permite homologação focada; uma
recomendação exige `both`. Para publicar os mesmos bytes de uma execução manual
bem-sucedida, com autorização explícita:

```bash
gh workflow run aegis-native-promote.yml --repo phcastello/Aegis --ref main \
  -f run_id=<run-v2-bem-sucedido> -f publish=true
```

Essa ferramenta de manutenção verifica plano/reserva/origem, hashes e assinaturas
antes de publicar a release e avançar o recomendado. Não é uma segunda etapa de
`publish-dev`. `publish=false` não publica. Estáveis conservam a matriz completa
de release e exigem autorização própria; previews e recuperações não repetem
automaticamente medições longas de consumo.
## Gerar um rollback

Selecione duas distribuições v2 públicas: a defeituosa e a funcional. O plano
consulta seus manifests versionados, exige compatibilidade e SHA fonte exato,
valida a sequência de recuperação e reconstrói o código escolhido com nova
versão, novo código e novas assinaturas. Não se instala diretamente um histórico.

```bash
gh workflow run aegis-native-preview.yml --repo phcastello/Aegis --ref main \
  -f sha=<sourceCommit-da-distribuicao-funcional> \
  -f branch=fix/v0.7.2-ux-proactivity \
  -f version=0.7.2-unstable.5.rollback.1 -f kind=rollback \
  -f rollback_of=0.7.2-unstable.5 -f restores_version=0.7.2-unstable.4 \
  -f compatibility_confirmed=true -f platform=both
```

Esses nomes são exemplo, não distribuições existentes. Essa recuperação manual
exige autorização explícita; depois do build e da assinatura, promover os mesmos
bytes pelo `aegis-native-promote.yml`, sem aprovação interativa do environment.
O SHA restaurado deve ser alcançável pela branch indicada e coincidir com a
procedência da distribuição funcional. Rollback de outro rollback incrementa
o evento e aponta `rollback_of` para a distribuição defeituosa publicada.
Se um evento for reservado e o build falhar, pule seu número: `.rollback.2` pode
recuperar a mesma distribuição original quando `.rollback.1` nunca foi publicada.
Nunca reutilize a reserva nem invente uma versão normal para contornar a falha. Fontes anteriores ao contrato v2 exigem preparação/revisão específica;
não contornamos as verificações para assiná-las automaticamente.

`compatibility_confirmed=true` é uma declaração explícita do operador, não
uma prova automática de compatibilidade de banco. Conferir API atual, protocolo,
configuração e persistência antes de selecioná-lo. Divergência dos contratos
bloqueia. Quando não houver garantia, preparar procedimento específico e manter
o rollback bloqueado. Nunca executar downgrade destrutivo de esquema.

## Instalar e recuperar o próprio Node

Em **Configurações → Atualizações**, a consulta automática discreta e a consulta
manual usam o mesmo fluxo. Uma preview publicada aparece normalmente. Uma
recuperação aparece como **Atualização de recuperação disponível**, identifica
o evento defeituoso e a distribuição restaurada e usa o mesmo botão de instalar.
Download, progresso e erros são preservados; não há instalação silenciosa.

**Recuperar este Node** consulta o recomendado e oferece a recuperação já
publicada. Para uma versão histórica, explica a reconstrução autorizada no
GitHub e oferece o acesso ao workflow. Não despacha assinatura, não promete
recuperação imediata e não tenta instalar código Android inferior.

Se o aplicativo não abrir, usar outro dispositivo/navegador para acessar a
release de recuperação publicada, conferir `latest.json`/`SHA256SUMS` e
identidade, baixar somente o instalador da plataforma e instalar por cima.
Android: mesmo pacote/certificado, código superior, sem desinstalar ou `pm clear`.
Windows: encerrar processo/tray, manter usuário/escopo/diretório/perfil e cofre.
Se não existir recuperação instalável, o proprietário publica um novo evento
pelo procedimento acima. Não reutilizar instalador antigo nem apagar o perfil.
Não há watchdog ou supervisor novo.

## Controles confiáveis na main

Os controles de publicação entram na `main` por PR de infraestrutura com o
check obrigatório `Aegis Node CI result`, preservando a proteção existente.
Não é necessário integrar o PR #19 para publicar seu candidato de desenvolvimento.
O workflow é sempre despachado de `main`; código candidato ou de pull requests
não recebe as chaves oficiais.

Preservar `aegis-native-signing`, sua allowlist de `main`/`node-v*` e as chaves
somente no environment. A rotina autorizada não exige reviewer nem aprovação
separada de assinatura/publicação. Ajustes administrativos usam a configuração
do GitHub e não um bypass. A variável pública `AEGIS_ANDROID_FIREBASE_CLIENT_B64`
conserva o cliente `com.aegis.node`; nunca usar service account no build.

O token de `plan` grava somente a reserva pelo controle confiável. O candidato
recebe permissões de leitura e nenhum segredo oficial; o job de código confiável
assina e publica os mesmos bytes. O ledger existente e seu histórico são
preservados, inclusive reservas de tentativas que falharam.
## Estágios e seleção

A seleção permanece em [select-node-ci.mjs](../.github/scripts/select-node-ci.mjs). Ela registra separadamente `affected` (backend, UI, Windows, Android, distribuição, geolocalização, notificações, áudio, energia, identidade e dependências) e `stage`. Falha de histórico seleciona os subsistemas conservadoramente, mas respeita a profundidade do estágio.

| Estágio | Gatilho e verificações |
| --- | --- |
| Desenvolvimento | Push em `fix/*`, `feat/*` ou outra branch não dispara workflow. Eventos de PR draft executam apenas testes dos scripts de CI e checagem documental, sem builds nativos ou medições. O gate de integração fica **falhando deliberadamente**, com mensagem de que draft não é aceite; não apresenta um skip como aprovação. |
| Preview | **Aegis Native Preview**, por solicitação explícita de `publish-dev` ou homologação manual. Compilação, contratos essenciais, identidade, versão, distribuição e assinatura isolada; `publish-dev` também publica e recomenda na mesma execução. Sem emuladores de consumo, baselines recompilados ou homologação integral. |
| Integração | PR aberto/pronto, `ready_for_review`, `synchronize`, `reopened`, `edited` e `merge_group/checks_requested`; também push em `main` e execução manual. Seleciona backend/migrations/transportes, interfaces, Windows/Android e identidade pertinentes. Android conserva framework API 26/35 debug e minificado; Windows conserva instalação/upgrade/inicialização e armazenamento. **Sem janelas longas de consumo nem recompilação de baseline para idle.** |
| Release | Execução manual `stage=release`, com `base_ref` completo para selecionar riscos, ou `full_matrix=true`. Mudanças de geofencing/background/energia/áudio, dependências e escopo desconhecido conservam medições relacionadas. UI/documentação conhecidas não justificam bateria. O publicador continua exigindo a **matriz completa**, incluindo identidade e validações de release assinada. |

UI comum seleciona builds/testes dos hosts e navegador; backend seleciona .NET, PostgreSQL descartável, migrations, Compose apenas por `config` e contratos reais de transporte. Kotlin seleciona Android; Rust compartilhado também seleciona Android e Windows. Alterações de identidade conservam browser/WebView2/WebView Android; contratos de notificações/transportes conservam nativos. Lockfiles podem selecionar compilação ampla de integração sem habilitar consumo prolongado. Mudanças desconhecidas e na infraestrutura de CI continuam conservadoras.

O workflow de identidade é reutilizado pelo CI principal, sem outro disparo automático duplicado. O agregador verifica todos os jobs selecionados, inclusive o resultado desse workflow. Falha, cancelamento, ausência ou skip inesperado de job selecionado impedem aprovação; não há `continue-on-error` nos gates. O SHA de origem e o SHA de evidência (merge de teste do PR ou merge queue) são registrados; todos os jobs usam o SHA resolvido. O gate consulta novamente o PR e recusa head/base diferentes, PR fechado ou retorno a draft. Concorrência por branch/PR cancela execuções substituídas.

`Aegis Node CI result` é o contexto estável de integração. Release usa **Aegis Release Acceptance result**; preview e modos focados não substituem integração. Filtros manuais antigos continuam disponíveis para diagnóstico, com gate final recusando aceite parcial. `base_ref` só limita a homologação de release; integração manual permanece conservadora, impedindo escolher um diff vazio para obter aceite.

Uma atualização da base pode exigir nova execução mesmo sem mudar o head. Configure o requisito de branch atualizada descrito abaixo e atualize a branch/reexecute o candidato quando necessário; não reutilize um resultado anterior ao novo merge de teste.

Para homologação extensa deliberada:

```bash
gh workflow run aegis-node-ci.yml --repo phcastello/Aegis \
  --ref <branch-ou-tag-revisada> -f stage=release -f full_matrix=true
```

Para homologação por risco, use `stage=release`, `full_matrix=false` e `base_ref=<SHA-base-completo>`. Sem base verificável, o seletor conserva a matriz completa do estágio, incluindo consumo. Para v0.7.2, geofencing e execução contextual exigem as medições antes do lançamento estável; elas não se repetem a cada preview.
## Proteção real da main

O ruleset [Protect main,19912815](https://github.com/phcastello/Aegis/rules/19912815)
permanece ativo, sem bypass, exigindo PR e impedindo exclusão/force push.
Na ativação v2, foi acrescentado **Aegis Node CI result**, origem **GitHub Actions**
(app15368), com **branch atualizada obrigatória**, confirmado pela API GitHub.
Nenhuma regra existente foi removida. Preview e validação focada não substituem
esse gate, que deve aprovar o candidato real antes do merge. PR #19 permanece
draft; somente o PR separado de infraestrutura pode ser integrado nesta ativação.
## Backend, dados e lançamento estável

`publish-dev` atualiza a API/PWA somente quando seus componentes mudam. Uma
alteração apenas de backend não compila Android/Windows. A preparação valida a
saúde real e os contratos necessários aos clientes existentes; os metadados
verificáveis acompanham a distribuição nativa, sem novos health checks durante
compilação, assinatura ou publicação.

O script consulta o histórico EF real. Sem migrations pendentes, não executa
backup/restauração de ensaio nem o migrador. Com migration pendente, preserva a
revisão exata, backup restaurado e verificado, compatibilidade com a API anterior
e aplicação administrativa antes da nova API. Migrations destrutivas ou incertas
continuam bloqueadas. Consulte [operação privada](operacao-privada.md). Volumes,
Data Protection, FCM, credenciais e identidades são preservados; PostgreSQL,
Qdrant e Neo4j não são reiniciados para publicar código. Não execute testes de CI
ou testes destrutivos nos bancos reais.

Depois de integração e aceite físico, prepare a release estável com versão
sincronizada, tags e publicação somente mediante autorização específica.
**Aegis Native Preview** também aceita estável, exigindo a matriz completa antes
do build; a manutenção manual publica por **Aegis Native Promote**. `publish-dev`
aceita apenas uma nova `unstable.N`. **Aegis Node Release** permanece histórico e
manual, restrito ao controle da `main` e a fontes ancestrais revisadas;
distribuições v2 são recusadas nesse caminho. As verificações de instalador
Windows assinado e APK oficial permanecem obrigatórias. Código arbitrário de
branches utiliza exclusivamente o isolamento v2.
## Nodes headless futuros

O contrato descreve distribuição, instalação, plataformas e procedência sem
exigir GUI. Um futuro runtime headless ou supervisor local deverá executar
atualização/recuperação, manter o estado após desconexão da GUI e reportar saúde.
Uma GUI poderá solicitar operações e acompanhar o resultado. Não implementamos
runtime Linux/headless, novos tipos de Node, protocolo remoto, armazenamento ou
serviço privilegiado nesta etapa.

## Verificação e limites

```bash
node --test .github/scripts/*.test.mjs
python3 -m unittest discover -s .github/scripts -p '*_test.py'
python3 -m unittest discover -s scripts -p publish_dev_test.py
npm run test:release --prefix apps/aegis-node
npm test --prefix apps/aegis-node
npm run build --prefix apps/aegis-node
npm run test:rust --prefix apps/aegis-node
actionlint
```

Os testes locais usam GitHub em memória para concorrência, imutabilidade,
interrupção de upload e avanço do ponteiro; manifests simulados não substituem
uma publicação real. Não se executa homologação de duas horas para validar YAML.
UI deve ser validada nos dois hosts e no layout móvel, inclusive recuperação.

Antes de declarar uma publicação concluída: execução nativa bem-sucedida,
conferência de todos os bytes/manifest, avanço real do feed e detecção nos clientes.
A solicitação aceita por `publish-dev` não demonstra esses resultados. Depois, homologação física Android/Windows sobre instalação existente:
NodeId, credenciais, pareamento e preferências preservados; preview detectada;
recuperação instalada; próxima atualização normal instalada; funcionalidades
existentes operacionais. Emulação/browser/mocks não comprovam esses resultados.

O registro de testes desta implementação fica em
[v0.7.2 — implementação](v0.7.2-implementation.md). Nenhuma migration ou implantação
real é autorizada implicitamente pela assinatura isolada do aplicativo. A rotina
`publish-dev` aplica somente os componentes e migrations compatíveis previstos
na solicitação autorizada.
