# Publicar desenvolvimento para teste

No host privado já autorizado, com Docker local, Python 3, Git, Node.js e `gh` autenticado
como identidade autorizada do repositório, use o checkout dos controles revisados.
Após implementar, testar, fazer commit e push:

```bash
python3 scripts/publish-dev.py --ref fix/v0.7.2-ux-proactivity
python3 scripts/publish-dev.py --ref <SHA-completo> --plan
```

O comando fixa o SHA enviado ao GitHub. Descobre o projeto, arquivos Compose,
ambiente e imagens efetivamente em execução pelos labels dos containers existentes.
Usa o socket Docker local; não requer SSH público, Docker TCP, runner privilegiado,
serviço permanente ou merge do candidato na main. Para desenvolver fora do
servidor, acesse esse host pelo caminho privado autenticado já existente e execute
o comando nele. Não exponha portas administrativas para esse fim.

O diff usa o SHA implantado **de cada componente**, e o feed recomendado para os
Nodes. Backend altera só a API; PWA altera só a PWA; cliente compartilhado altera
PWA e nativos. Documentação não publica componentes. Componente sem identidade
verificável é reconstruído uma vez. `--rebuild backend` ou `--rebuild pwa` força
somente esse componente privado, sem forçar uma versão nativa; `--full` força todos.
API nova provoca apenas reload do nginx da PWA para resolver seu novo IP.

O agente executa os testes pertinentes antes do commit/push. `publish-dev` constrói
somente os componentes privados selecionados e verifica a integridade da implantação:
imagem/SHA efetivamente iniciados, HTTPS, API v1 e disponibilidade/cache do service
worker. Não repete as suítes de desenvolvimento nem a homologação extensa de
consumo. O comando usa a configuração efetiva, preservando mounts, volumes,
credenciais e bancos; alterações de Compose exigem reconciliar a instalação antes
da publicação. Os health checks reais são concluídos nessa preparação, sem
repetição durante os builds nativos.

Sem migrations pendentes, não há backup, restauração de ensaio nem execução do
migrador. Migration pendente precisa de revisão exata em `SAFE_MIGRATIONS` no
script. Nome ou palavras como AddColumn não provam segurança. A revisão considera Up, dados e
contratos antigos; Down nunca é executado. A API escritora para por uma janela
curta; o dump PostgreSQL e Data Protection são salvos com a configuração/FCM em
diretório privado. O dump é restaurado em PostgreSQL descartável isolado; os hashes
de todas as linhas históricas são comparados. O candidato migra duas vezes e o
migrador e a API anteriores também precisam aceitar o esquema expandido numa
rede descartável sem segredos nem portas publicadas. Só então o NodeAdmin
migra a instalação real e a nova imagem inicia. Backup inválido ou migration
incerta bloqueia a operação. Recuperação automática troca imagens compatíveis;
restauração do banco exige procedimento administrativo próprio, em destino separado.
Qdrant e Neo4j não são alterados nem reiniciados por publicação de imagens.

Quando o diff afeta aplicativos, a operação escolhe a próxima `unstable.N` no
ledger e despacha **uma vez** `aegis-native-preview.yml` da `main` com
`publish_dev=true`. A preparação fixa SHA, identidades implantadas e contratos de
compatibilidade API/dados. O input `deployment` contém `sourceCommit`, os SHAs
implantados de `backend`/`pwa` e `compatibility: { api: 1, nodeData: 1 }`; o plano
e a reserva conservam esses metadados, sem prazo de validade. O workflow
confere a solicitação autorizada e os contratos, reserva versão/código, compila
sem segredos, verifica os artefatos, assina Android e Windows com código confiável
e publica os mesmos bytes no feed recomendado. O código candidato nunca executa
no job que recebe as chaves oficiais.

O comando termina depois de solicitar a execução; o terminal pode ser fechado.
Não envia heartbeat nem status com validade curta, não acompanha o workflow por
horas e não precisa de uma sessão no servidor privado durante a assinatura.
O GitHub conclui a publicação sem aprovação interativa e sem um segundo workflow
de promoção. O resultado do comando distingue solicitação iniciada de publicação
concluída. `phase=native-requested` e `success=true` significam solicitação aceita;
confira a execução no GitHub e o feed recomendado para afirmar publicação concluída.
Nos Nodes compatíveis, a atualização aparece em **Configurações → Atualizações**;
a instalação continua exigindo a ação normal do usuário no aplicativo.

Os controles entram na `main` pelo PR de infraestrutura com `Aegis Node CI result`
aprovado; a proteção da branch permanece obrigatória. O environment
`aegis-native-signing` conserva as chaves oficiais e a allowlist de `main`/`node-v*`,
sem reviewer obrigatório para a rotina autorizada. Mudanças administrativas devem
usar a configuração do GitHub, sem bypass nem aprovação automática de reviewers.
Preview focada, promoção manual de artefatos existentes e release estável continuam
operações explícitas separadas; não participam da rotina `publish-dev`.

Build ou assinatura falhos consomem a reserva e preservam o recomendado anterior.
Uma nova tentativa de build usa o próximo número; nunca outros bytes sob a mesma
versão. Falha parcial de upload mantém o draft e o feed anterior; a publicação
imutável pode retomar somente os mesmos bytes. Falhas transitórias de upload têm
até três tentativas no mesmo job, sem reconstrução, nova assinatura ou intervenção
humana. Não remova releases, ledger ou feed para repetir.

Cada execução grava plano, SHA, identidades Docker, migrations, checks existentes,
evidência de integridade, backup quando necessário e solicitação nativa em
`~/.local/state/aegis/publish-dev/<execução>/`. `current.json` registra a implantação
privada saudável e o feed observado; a conclusão nativa é registrada no GitHub.
`native-run.json` guarda versão, SHA, metadados e o link da execução quando já
disponível. Arquivos `private` contêm
configuração e evidência sensível: não envie esses arquivos ao GitHub. Uma trava
local e comparações de containers/Compose/feed protegem a preparação privada;
a concorrência do workflow e o avanço fast-forward protegem ledger e feed no GitHub.

Falha de build não altera a instalação. Falha de migration ou saúde tenta recuperar
as imagens anteriores somente com compatibilidade comprovada. Falha nativa depois
do backend saudável mantém esse backend compatível e o feed anterior. Uma reexecução
recalcula apenas o que falta pelos SHAs reais; não recompila aplicativos se o feed
já corresponde à fonte. Não existe atomicidade entre GitHub, servidor e dispositivos.

Testes seguros da operação:

```bash
python3 -m unittest discover -s scripts -p publish_dev_test.py
node --test .github/scripts/*.test.mjs
```

O ensaio Docker adicional usa somente imagens locais imutáveis indicadas em
`AEGIS_PUBLISH_TEST_OLD_IMAGE` e `AEGIS_PUBLISH_TEST_NEW_IMAGE`:
`python3 -m unittest discover -s scripts -p publish_dev_docker_test.py`.
Cria PostgreSQL em tmpfs e dados sintéticos numa rede interna temporária; nunca
descobre a instalação pessoal. Falhas da operação usam fixtures sem dados reais.
