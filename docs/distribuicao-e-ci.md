# Distribuição e seleção de CI após a v0.7.1

Estas mudanças estão implementadas na manutenção; não alteram os instaladores v0.7.1 já publicados nem demonstram uma distribuição nova.

## Atualização estável e previews

`node-preview/latest.json` conserva sua URL histórica porque Android e Windows já distribuídos dependem dela. Seu contrato passa a ser **somente estável**. Previews possuem instaladores e `latest.json` imutáveis em `node-vX.Y.Z-unstable.N`, disponíveis para instalação manual deliberada. Não existe adesão automática a previews nem seletor de canal nesta manutenção.

`publish-release.mjs` verifica versão, assinatura declarada no manifesto, código Android, URLs e bytes dos assets antes de publicar a distribuição. A verificação criptográfica da assinatura Windows e do certificado Android continua no workflow de release. Apenas uma distribuição estável pública e novamente verificada avança o feed compatível. A preview não consulta nem modifica esse feed, mesmo se ele estiver ausente. Isso protege também clientes antigos que não filtram prereleases.

Uma preview futura no inventário de releases não bloqueia um patch estável de uma linha mantida. Um manifesto histórico de preview no feed também não define o piso estável: pode ser substituído pelo patch estável verificado. Instalações de preview mais novas ignoram versões menores pela comparação da versão instalada e do `versionCode`. Um feed ou uma distribuição estável mais nova continua impedindo retrocesso do canal.

Clientes novos usam o mesmo filtro conservador nos caminhos Android e Windows: candidato estável válido, estritamente posterior à versão instalada e com alocação Android crescente. Assim, `0.7.1 → 0.7.2-unstable.1` é recusado e `0.7.2-unstable.1 → 0.7.2` é permitido. O Android mantém a validação da URL, do código do pacote e do certificado na instalação; o Windows mantém URL versionada e assinatura do updater com a chave existente. Fixtures `stage.N` continuam cobrindo parsing e migração históricos.

Falhas durante upload deixam o draft recuperável e não avançam o feed. Reexecuções com os mesmos bytes completam assets ausentes; bytes diferentes são recusados, inclusive para releases já públicas. Uma falha após publicar a distribuição permite retomar somente o avanço do feed. O GitHub não oferece substituição atômica de um asset por outro de mesmo nome: `--clobber` pode deixar uma janela sem `latest.json` se a transferência falhar depois da exclusão. A aplicação informa feed indisponível; não trata isso como versão atual. Recupera-se repetindo o publicador com a mesma distribuição verificada, sem trocar assets versionados. Os testes simulam interrupções antes da mutação e retry; não prometem transação atômica do GitHub.

O teste do instalador Windows assinado roda para todo lançamento, inclusive futuras versões e previews. Seleciona o último baseline estável sobrevivente estritamente anterior ao candidato: v0.7.1 para versões posteriores a ela, mantendo v0.7.0 no caso histórico de upgrade para v0.7.1. O SHA-256 da v0.7.1 (`f4cda9fc477258ff6c5f3bf420b0430c5ac3fbd2e76c01d145a3466657d494c3`) foi conferido nos metadados do asset GitHub `660986488` e nos bytes baixados. O upgrade original de v0.7.0 a partir da preview removida permanece evidência histórica; o publicador não tenta recriar essa distribuição.

Nenhum desses testes publica feeds artificiais ou altera releases/tags reais.

## CI proporcional

Os workflows sempre iniciam em PRs; a seleção ocorre dentro deles. Isso permite que alterações documentais concluam os checks sem deixá-los pendentes por um filtro `paths`. Os agregadores `Aegis Node CI result` e `Aegis Release Identity result` falham se qualquer job selecionado falhar, for cancelado ou for omitido inesperadamente. Um skip planejado não é evidência de aceite nativo.

A seleção mora em [select-node-ci.mjs](../.github/scripts/select-node-ci.mjs), com testes executados até em mudanças documentais. O mesmo job verifica comandos e links com `node scripts/check-docs.mjs`.

| Alteração | Verificações selecionadas |
| --- | --- |
| README/documentação | Seletor e checagem leve da documentação; sem builds dos aplicativos. |
| UI compartilhada, web ou nativa | Typecheck, builds dos dois hosts, testes JS e paridade/browser; upgrade do Service Worker com fila legada. |
| Backend | Build, testes .NET com PostgreSQL isolado, migrations em banco descartável `aegis_operation_test`, Compose somente por `config` e contratos reais de transporte; mudanças nos contratos nativos também acionam nativos. |
| Kotlin/manifest/permissões Android | ARM64, exclusão de fixtures e testes framework API 26/35 em debug/release minificado; janelas de consumo. |
| Rust/runtime/armazenamento | Windows e Android, incluindo upgrade, lifecycle, JNI/R8, assinatura e consumo aplicáveis. |
| Captura de áudio compartilhada | UI e smokes nativos Windows/Android, além dos gates de repouso. |
| Atualização no host Windows | UI e validações nativas dos dois clientes. |
| Dependências, lockfiles, build ou workflows | Cobertura conservadora completa; mudanças do próprio seletor incluídas. |
| Identidade/versão/App/sidebar | Provas específicas de identidade em browser, WebView2 instalado e WebView Android. |

O workflow de identidade mantém suas provas exclusivas; os testes unitários e contratos genéricos ficam no workflow principal. Os baselines recompilados por SHA preservado continuam fixos; a seleção não altera hashes, chaves, exclusão de fixtures ou cenários.

Para aceite completo, execute manualmente **Aegis Node CI** com `full_matrix=true` (ou sem filtros focados). Isso inclui as seis janelas Android de dez minutos e a comparação equivalente de repouso. As opções antigas para UI, Windows, distribuição ARM64 e framework/API continuam disponíveis; `full_matrix` prevalece sobre elas. O workflow **Aegis Release Identity** também permite execução manual completa ou Windows isolado.

A promoção de uma release reutiliza os dois workflows completos **na referência imutável solicitada**, antes de compilar/publicar os instaladores assinados. Seus grupos de concorrência são separados dos checks comuns. Não se deve promover uma release com evidência de um modo focado ou de outro commit.

Para verificar localmente sem hardware:

```bash
node --test .github/scripts/select-node-ci.test.mjs
npm test --prefix apps/aegis-node
npm run test:release --prefix apps/aegis-node
npm run test:rust --prefix apps/aegis-node
```

Os testes do publicador usam GitHub em memória; os testes portáveis Rust usam manifests locais. Os testes Windows do plugin oficial, os APKs assinados e os emuladores framework permanecem gates do CI e não equivalem a teste do microfone físico do proprietário.
