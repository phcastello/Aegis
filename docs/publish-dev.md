# Publicar desenvolvimento para teste

No host privado já autorizado, com Docker local, Python 3, Git, Node.js e `gh` autenticado
como proprietário, use o checkout dos controles revisados:

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

São executados testes pertinentes nos bytes selecionados, builds e verificação da
imagem efetivamente iniciada, HTTPS, API v1 e disponibilidade/cache do service
worker (incluindo o teste de upgrade em Chromium instalado no host). O comando usa a configuração efetiva, preservando os mounts, volumes,
credenciais e bancos; alterações de Compose exigem reconciliar a instalação antes
da publicação. Testes de consumo e homologação estável continuam separados.

Migration pendente precisa de revisão exata em `SAFE_MIGRATIONS` no script. Nome
ou palavras como AddColumn não provam segurança. A revisão considera Up, dados e
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
ledger e chama **uma vez** `aegis-native-preview.yml` da main com `publish_dev=true`.
O workflow reutiliza a reserva, builds sem segredos e verificadores existentes.
Um único job protegido assina Android e Windows e publica/promove os mesmos bytes,
com uma aprovação de `aegis-native-signing`. O candidato não executa nesse job.
Não há segundo workflow de promoção na rotina. O comando permanece aberto e
atesta a saúde privada a cada 20 segundos; o job confere atestado recente do
proprietário para o SHA selecionado, inclusive imediatamente antes de avançar o
feed. Sem atestado válido, o feed não avança. A instalação nos Nodes continua
dependendo da confirmação no aplicativo, em Configurações → Atualizações.

Não altere reviewers, permissões nem environments para evitar a aprovação. O
bootstrap dessa opção precisa entrar na main por PR de infraestrutura com o check
`Aegis Node CI result` aprovado. Depois, qualquer branch de desenvolvimento pode
ser a fonte. Preview manual e `aegis-native-promote.yml` continuam disponíveis;
rollbacks continuam eventos novos e versionados. Build ou assinatura falhos
consomem a reserva; uma nova tentativa usa o próximo número, nunca outros bytes
sob a mesma versão. A publicação imutável é retomável com os mesmos bytes pelos
controles existentes; não remova releases, ledger ou feed para repetir.
Falhas transitórias de upload/promoção têm até três tentativas com os mesmos bytes
no mesmo job aprovado, sem outra reserva, assinatura ou aprovação. Se persistirem,
a operação falha e conserva o recomendado anterior.

Cada execução grava plano, SHA, identidades Docker, migrations, checks existentes,
resultados de testes, backup e estado final em
`~/.local/state/aegis/publish-dev/<execução>/`. `current.json` registra a implantação
privada saudável e as versões recomendada/anterior. Arquivos `private` contêm
configuração e evidência sensível: não envie esses arquivos ao GitHub. Uma trava
local e comparações de containers/Compose/feed bloqueiam execução concorrente;
o ledger e o feed mantêm seu avanço por compare-and-swap no GitHub.

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
