# Distribuição, previews e estágios de CI

Para disponibilizar desenvolvimento para teste, use a operação única
[publish-dev](publish-dev.md). Ela coordena API/PWA privadas e, quando o diff afeta
aplicativos, reserva, build, assinatura e recomendação com uma aprovação protegida.
Os comandos separados descritos abaixo continuam como ferramentas manuais.

Este documento descreve os caminhos implementados e distingue suas pendências operacionais. Os instaladores v0.7.1 publicados permanecem inalterados; editar workflows não demonstra uma distribuição nova.

## Distribuição recomendada v2 e compatibilidade

O controle v2 é independente dos aplicativos. Este bootstrap conserva a versão
**0.7.1**, código Android **7001999**, e não integra as funcionalidades do PR #19.
O updater v2 está na branch de desenvolvimento; a main continua usando o feed
legado até uma distribuição migratória autorizada.

Nodes v2 consultam um único recomendado:
`https://raw.githubusercontent.com/phcastello/Aegis/aegis-distribution/latest.json`.
Previews, estáveis e recuperações são classificações de distribuição, sem canais
selecionáveis. Publicar oferece a instalação; cada Node com interface pede ação
do usuário. Os instaladores permanentes ficam em releases `node-v<versão>`.

O manifesto schema2 registra versão pública, código de instalação independente,
commit/versão efetivos do código, hashes, assinaturas e compatibilidade API/dados.
Reservas ficam no ledger da branch `aegis-distribution`, antes do build, com
códigos estritamente crescentes. Falhas consomem a reserva. Promoções repetidas
só aceitam os mesmos bytes. Após publicar e conferir todos os assets, commits Git
com avanço fast-forward atualizam o recomendado sem apagar o último manifesto.

A convenção de recuperação é preservada: `0.7.2-unstable.5.rollback.1` recupera
uma preview e `0.7.3-rollback.1` recupera a estável `0.7.2`, sem consumir a próxima
preview normal. Eventos de tentativas falhas deixam lacunas, sem reutilização.

`node-preview/latest.json` permanece **somente estável**, para v0.7.1 e clientes
anteriores. Previews e recuperações v2 nunca modificam esse asset. Uma futura
estável pode migrar esses clientes; para entrar antes nas previews v2, exige-se
uma última instalação manual assinada por cima. A preview antiga `.1`, run
`40038742314`, também exige essa migração e não pode ser promovida como v2.

O publicador v2 confere hashes, metadados, assinatura Windows e certificado
Android, publica assets versionados imutáveis e verifica os bytes recuperados
antes de avançar o ponteiro. Falha parcial preserva o recomendado anterior e
permite retomar o draft. O feed legado das estáveis conserva sua limitação
histórica de upload `--clobber`, sem atomicidade do GitHub; o recomendado v2 usa
Git e não sofre essa janela. Nunca substituir bytes de assets versionados.

O teste de upgrade assinado Windows usa o último baseline estável sobrevivente
anterior ao candidato, atualmente v0.7.1 para versões posteriores. Seu SHA-256
é `f4cda9fc477258ff6c5f3bf420b0430c5ac3fbd2e76c01d145a3466657d494c3`.
Seleciona por SemVer, sem derivar código Android. O baseline de integração é
separado e pode ser v0.7.0; testes em runners não comprovam hardware do proprietário.

## Fluxo de trabalho

**Desenvolver → Validar → Push → Gerar preview → Testar → PR pronto → Integrar → Homologar release.**

1. Crie a branch designada a partir de `origin/main`, por exemplo `git switch -c fix/assunto origin/main`. Se ela já existir, reutilize-a e sincronize sem sobrescrever alterações locais; preserve outras worktrees.
2. Execute as verificações dos componentes afetados antes do commit/push, conforme [AGENTS.md](../AGENTS.md). Para UI: testes, typecheck/build e expectativas visuais; para backend: camadas/integrações pertinentes; para capability: contratos de todas as plataformas. Informe limitações do ambiente.
3. Faça commit e `git push -u origin <branch>`. Push de desenvolvimento não inicia a matriz remota. Abra ou mantenha o PR em draft enquanto estiver iterando.
4. Solicite uma preview manual do SHA completo pelo procedimento abaixo. Gerar instaladores não atualiza o backend.
5. Instale deliberadamente sobre uma instalação compatível e teste os dispositivos reais, preservando identidade e dados.
6. Quando a alteração estiver pronta, marque o PR como pronto para revisão. Confira **Aegis Node CI result** no candidato atual antes da integração autorizada.
7. Antes de lançar, solicite homologação de release proporcional ao risco ou a matriz completa. A publicação estável continua sendo outra operação protegida e autorizada.

## Estágios e seleção

A seleção permanece em [select-node-ci.mjs](../.github/scripts/select-node-ci.mjs). Ela registra separadamente `affected` (backend, UI, Windows, Android, distribuição, geolocalização, notificações, áudio, energia, identidade e dependências) e `stage`. Falha de histórico seleciona os subsistemas conservadoramente, mas respeita a profundidade do estágio.

| Estágio | Gatilho e verificações |
| --- | --- |
| Desenvolvimento | Push em `fix/*`, `feat/*` ou outra branch não dispara workflow. Eventos de PR draft executam apenas testes dos scripts de CI e checagem documental, sem builds nativos ou medições. O gate de integração fica **falhando deliberadamente**, com mensagem de que draft não é aceite; não apresenta um skip como aprovação. |
| Preview | Somente **Aegis Native Preview**, manual, com SHA e versão explícitos. Compilação, contratos essenciais, identidade, versão, distribuição e assinatura isolada; sem emuladores de consumo, baselines recompilados ou homologação integral. |
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

## Preview e promoção protegidas

Os workflows são despachados de `main`. O SHA de fonte é resolvido e registrado
por completo; pode pertencer à branch de desenvolvimento deste repositório.
A primeira candidata v2 natural é `.2`, mas deve-se conferir ledger, releases e
runs admitidos antes de escolher a versão. Nunca reutilizar uma reserva.

```bash
gh workflow run aegis-native-preview.yml --repo phcastello/Aegis --ref main \
  -f sha=<SHA-completo-revisado> -f branch=fix/v0.7.2-ux-proactivity \
  -f version=0.7.2-unstable.2 -f kind=normal -f platform=both
```

`platform=android` ou `windows` permite homologação focada; a promoção exige
`both`. O build não recebe chaves oficiais: Android usa chave descartável;
Windows gera NSIS sem assinatura oficial do updater. Runners separados executam
somente código confiável da main para verificar/reassinar os bytes. O environment
`aegis-native-signing` exige reviewer, mantém bypass administrativo desabilitado
e allowlist exata de `main` e tags `node-v*`. As chaves oficiais ficam somente
nesse environment. A variável pública `AEGIS_ANDROID_FIREBASE_CLIENT_B64` contém
apenas o cliente Firebase Android existente, nunca service account.

Aprove no GitHub a assinatura somente após revisar SHA, versão, plano e builds.
Os finais incluem instalador, `preview.json`, `SHA256SUMS` e assinatura Windows.
Os intermediários duram um dia; os finais,14 dias. Build e assinatura não publicam
atualização. Artefatos expirados exigem nova reserva/build, sem novos bytes sob
uma identidade já reservada.

Após sucesso e autorização explícita da distribuição:

```bash
gh workflow run aegis-native-promote.yml --repo phcastello/Aegis --ref main \
  -f run_id=<run-v2-aprovado> -f publish=true
```

A promoção pede nova aprovação do environment, verifica plano/reserva/origem,
hashes e assinaturas e só então publica a release e avança o recomendado.
`publish=false` não publica. Assinatura do updater Windows não é Authenticode
nem remove SmartScreen. Estáveis conservam a matriz completa de release;
previews e recuperações não exigem automaticamente medições longas de consumo.

Para a instalação inicial, baixar da release versionada o APK ou NSIS assinado.
Android: mesmo `com.aegis.node`, certificado oficial e código superior; instalar
por cima, sem desinstalar ou limpar dados. Windows: encerrar processo/tray e
manter usuário, escopo, diretório, WebView2 e Credential Manager. Conferir NodeId,
pareamento e preferências antes/depois sem registrar tokens. Nas próximas
publicações compatíveis, usar **Configurações → Atualizações**, sem ZIPs manuais.
Se o aplicativo não abrir, usar externamente o instalador oficial de recuperação
com código superior; nunca instalar um histórico inferior nem apagar o perfil.

## Backend e lançamento estável

Preview do aplicativo não implica deploy da API. Para teste integrado da v0.7.2 contra a infraestrutura privada existente, a API correspondente e a migration **`20261022133709_ReminderInterventions`** são pré-requisitos. Não é necessário manter outro ambiente permanente.

Obtenha autorização explícita para atualizar a infraestrutura real. Siga [operação privada](operacao-privada.md): registre imagem/commit anterior; preserve projeto Compose, volumes, Data Protection, FCM e configurações; pare escritores numa janela autorizada; produza backup e valide restauração em destino descartável; compile a imagem aprovada; execute o migrador administrativo **antes de iniciar a API nova**; valide health, schema, pareamento, lembretes e intervenções; só então teste os Nodes preview. Em falha, pare escritores e recupere imagem/esquema compatíveis ou restaure backup em destino controlado. Não execute testes destrutivos nem migrations de CI nos bancos reais. Nenhum desses comandos é executado pela preview.

Depois de integração e aceite físico, prepare a release estável com versão sincronizada, tags e publicação somente mediante autorização. **Aegis Native Preview** também aceita estável, exigindo a matriz completa antes do build; a publicação continua separada em **Aegis Native Promote**. A ativação atual autoriza somente preview. **Aegis Node Release** permanece histórico/manual, restrito ao controle da main e a fontes ancestrais revisadas; distribuições v2 são recusadas nesse caminho. As verificações de instalador Windows assinado e APK oficial foram preservadas. O caminho legado só pode compilar código histórico integrado à main após aprovação; código arbitrário de branches utiliza exclusivamente o isolamento v2. Por isso a revisão do código/tag autorizado continua necessária.

## Verificação da infraestrutura e desempenho

```bash
node --test .github/scripts/*.test.mjs
python3 -m unittest discover -s .github/scripts -p '*_test.py'
npm run test:release --prefix apps/aegis-node
npm run test:rust --prefix apps/aegis-node
# Com actionlint disponível:
actionlint
```

Os testes do seletor cobrem eventos draft/pronto, novos commits/base, subsistemas, dependências, preview/release, falha de histórico e gates omitidos/falhos. Os testes dos workflows verificam concorrência, isolamento, ausência de publicação/feeds e preservação dos gates de release. Os testes de preview cobrem SHA/branch, versão monotônica, sincronização temporária, procedência e falha de API/configuração. A validação sintática usa actionlint. Não se executa a matriz de duas horas para validar YAML.

Evidência anterior: run [39803911696](https://github.com/phcastello/Aegis/actions/runs/39803911696), Android API 35 **2h02min31s** (15:22:12–17:24:43 UTC); API 26 **23min01s**; ARM64 **11min47s**. A remoção das seis janelas de dez minutos e duas de repouso do estágio de integração elimina **80 minutos programados**, além da recompilação do baseline. Isso não é uma medição de duração final: compilação, testes framework, filas e caches continuam variáveis. Preview tampouco tem prazo garantido antes da primeira execução real. O run [39852679629](https://github.com/phcastello/Aegis/actions/runs/39852679629), SHA `e1ebd7f`, comprovou o caminho draft em **25 segundos** (20:53:14–20:53:39 UTC): testes JS/Python e documentação aprovados, nenhum job nativo executado e gate de integração recusado deliberadamente por estar em desenvolvimento. A duração anterior total desse pipeline no draft foi **2h04min20s** (15:20:31–17:24:51 UTC), incluindo o job API 35 acima. Essa redução mede a iteração em draft, não uma integração ou preview aprovada. Homologação e assinatura continuam pendentes até sua execução autorizada.
