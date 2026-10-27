# Instruções de engenharia

## Identidade e critérios de decisão

A Aegis é uma assistente pessoal privada, destinada ao uso de seu proprietário.
Combina serviços próprios e modelos acessados por API. Privacidade, utilidade,
previsibilidade e baixo custo operacional orientam as decisões.

**Maximizar a utilidade com o menor trabalho necessário, tornando a Aegis cada vez
mais facilitadora.** Prefira soluções simples, eficientes e incrementais.

## Implementação

- Prefira a menor solução correta. Leia o código, os contratos e a documentação
  existentes antes de propor abstrações; reutilize serviços e contratos adequados.
- Limite o escopo à tarefa. Evite refatorações desnecessárias e não transforme
  necessidades futuras em funcionalidades presentes.
- Preserve compatibilidade com os clientes existentes quando exigida. Ao alterar
  contratos compartilhados, identifique e valide seus consumidores.
- Justifique novas dependências. Preserve limites de recursos, segurança,
  persistência, observabilidade e tratamento de falhas.
- Nunca exponha credenciais, dados pessoais ou segredos em código, logs ou artefatos.

## Branches e distribuição

- Trabalhe na branch designada; preserve a worktree original, alterações locais e
  outras branches. Use uma worktree isolada quando necessário.
- Não modifique diretamente a `main`, não use force push e não faça merge sem
  autorização. Preserve as regras de proteção e os checks obrigatórios.
- Não abra um PR automaticamente a cada push. Preserve o estado draft quando pedido.
- Faça commits por responsabilidade e push na branch de desenvolvimento quando
  isso fizer parte da tarefa solicitada, após as verificações pertinentes.
- Implementação comum: implemente, teste, corrija, faça commit e push na branch
  designada e relate o resultado. Push não publica automaticamente.
- Quando o proprietário pedir implementação **e disponibilização para teste**,
  essa solicitação autoriza a publicação privada de desenvolvimento. Após testes,
  commit e push, execute `python3 scripts/publish-dev.py --ref <branch-ou-SHA>`
  pelo acesso privado ao servidor e verifique a execução real. Veja
  [publish-dev](docs/publish-dev.md). Não peça confirmação para cada etapa ordinária.
- Preserve a aprovação única do environment de assinatura. Não aprove reviewers
  automaticamente. Publicação de desenvolvimento não autoriza release estável,
  merge na main, exclusão de dados nem mudança de credenciais.
- Se a publicação falhar, investigue e corrija o problema técnico possível.
  Push ou build bem-sucedido não conclui uma tarefa destinada a teste. Bloqueie
  operações sensíveis quando dependerem de ação administrativa ou risco aos dados;
  conclua o trabalho independente e informe precisamente a evidência e o bloqueio.
- Consulte [distribuição e CI](docs/distribuicao-e-ci.md) antes de alterar workflows,
  assinatura, identidade, versões ou atualização dos aplicativos. Use o mecanismo
  existente de sincronização de versão e preserve códigos Android monotônicos.

## Verificações antes de concluir e enviar commits

**Execute as verificações pertinentes às alterações antes de declarar a
implementação pronta e enviar os commits.** A matriz completa não é obrigatória
em toda iteração; selecione verificações que provem os contratos afetados.

| Alteração | Verificação esperada |
| --- | --- |
| Interface compartilhada | Testes da interface, typecheck e build dos hosts afetados; contratos dos consumidores. |
| Comportamento visual | Expectativas e testes visuais relacionados, incluindo versão e identidade quando afetadas. |
| Backend | Testes das camadas afetadas e integrações pertinentes. |
| Modelo EF | Migration, compatibilidade e atualização em banco descartável. |
| Rust | Testes portáveis e validações nativas possíveis na plataforma disponível. |
| Kotlin/Android | Testes Android disponíveis no ambiente e contratos JNI/R8 quando afetados. |
| Capability | Catálogo com nomes/versões e consumidores de todas as plataformas. |
| Contrato compartilhado | Atualização e testes dos consumidores correspondentes. |
| CI/distribuição | Sintaxe, seletor, gates, versionamento e controles de assinatura afetados. |

Comandos de entrada existentes:

```bash
npm test --prefix apps/aegis-node
npm run build --prefix apps/aegis-node
npm test --prefix frontend/aegis-pwa
npm run build --prefix frontend/aegis-pwa
npm run test:rust --prefix apps/aegis-node
npm run test:release --prefix apps/aegis-node
dotnet test backend/Aegis.sln
```

Consulte a documentação do componente para os testes específicos e seus requisitos.
Se uma verificação não puder ser executada no ambiente disponível, informe a
limitação; nunca declare que passou. Mocks, navegador e emuladores não comprovam
funcionamento físico em Windows ou Android.

## Infraestrutura e dados

Não introduza uma instalação permanente completa da Aegis no computador do
proprietário apenas para desenvolver. Compilações, testes unitários e bancos
descartáveis da infraestrutura de desenvolvimento continuam permitidos.

Aplicativos em desenvolvimento podem utilizar a infraestrutura privada existente
mediante autorização e respeitando as configurações atuais. Não exija outra
implantação permanente do backend, PostgreSQL, Qdrant, Neo4j ou Firebase.
Nunca execute testes destrutivos contra dados de produção.

Migrations compatíveis estão incluídas na autorização de `publish-dev`, após
identificação exata, revisão vinculada ao hash, testes descartáveis e backup
restaurado e verificado. Migration desconhecida, destrutiva, incompatível ou sem
recuperação comprovada exige autorização específica; nunca contorne o bloqueio.
Consulte [operação privada](docs/operacao-privada.md). Preserve configurações,
PostgreSQL, Qdrant, Neo4j, FCM, Data Protection e identidades. Nunca remova volumes,
resete banco nem execute Down como rollback automático. Não rode migrations no
startup de Production nem testes destrutivos nos dados pessoais.

## Relatório de conclusão

Informe o que implementou, verificações executadas e seus resultados, o que não
conseguiu validar, riscos e limitações conhecidos, commits criados, se houve push
e se executou alguma ação de distribuição ou deploy. Distinga evidência local,
CI, emulação e homologação física; não declare aprovação de checks pendentes ou
falhando.
