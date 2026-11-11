# Licenciamento da Aegis — proposta e verificação

Data: 11 de novembro de 2026.
Base examinada: [`8607dc009b20fda001f69b43e506b18bbfa837be`](https://github.com/phcastello/Aegis/commit/8607dc009b20fda001f69b43e506b18bbfa837be),
branch `main` de `phcastello/Aegis`.

## Resultado

Proposta: GNU AGPL versão 3 exclusivamente, com preservação de atribuição
razoável de Pedro Hasson Castello como criador original, conforme a seção 7(b).
`LICENSE` contém o texto oficial integral; `ADDITIONAL_TERMS.md` contém a
condição separada; `AUTHORS.md` registra a autoria e `NOTICE.md` delimita o
material próprio e de terceiros. O README explica os direitos e as obrigações.

A proposta deve permanecer em revisão antes da integração e de uma nova
distribuição: **a compatibilidade do conjunto Android não está resolvida**,
a procedência jurídica dos recursos visuais não está comprovada no repositório
e o inventário das licenças de cada artefato final ainda precisa ser concluído.
Nenhuma exceção para vinculação com SDK proprietário foi concedida nesta
proposta. Nenhuma dependência foi substituída, atualizada ou relicenciada.

## Repositório, licença anterior e autoria

O repositório encontrado é [`phcastello/Aegis`](https://github.com/phcastello/Aegis),
distinto de `phcastello/aegis-tts`. A conta conectada é `phcastello`, proprietária
do repositório, com permissões de administração e escrita. O pedido autoriza
a preparação da mudança em branch própria e PR; não autoriza merge ou release.

Na base não há `LICENSE`/`COPYING` geral, `AUTHORS` ou termo geral de licença no
README. A busca dos nomes de arquivos de licença no histórico alcançável
localizou as licenças da biblioteca vendorizada, não uma licença geral anterior.
A publicidade do código no GitHub não substitui uma licença de software.

Os 412 commits alcançáveis da base têm dois nomes de autoria (`phcastello` e
`Pedro Castello`), com o mesmo e-mail. Não foram encontrados trailers
`Co-authored-by` ou `Signed-off-by` nesse histórico. O primeiro commit é
`76aa3f2`, de 4 de junho de 2026. Isso é evidência de autoria registrada,
não prova conclusiva da cadeia de titularidade. O nome completo do criador
foi fornecido pelo proprietário no pedido.

Material copiado, contribuições externas sem registro, contratos com
empregadores e direitos sobre imagens não podem ser afastados por uma consulta
ao Git. Se existirem outros titulares, obtenha a autorização necessária antes
de licenciar seu material. A proposta não revoga licenças eventualmente
concedidas fora deste histórico nem torna retroativamente obrigatórios os
termos de versões posteriores.

## Dependências e possíveis impedimentos

| Grupo examinado | Evidência e resultado |
| --- | --- |
| npm | Quatro lockfiles, 697 entradas de pacotes, contando repetições entre hosts e ferramentas. Predominam MIT, ISC, Apache-2.0 e BSD. Há também BlueOak-1.0.0, Python-2.0 e `caniuse-lite` sob CC-BY-4.0 (dados de ferramenta de build). O pacote compartilhado local não é uma dependência externa sem licença. |
| DOMPurify | Declara `(MPL-2.0 OR Apache-2.0)` no lockfile compartilhado. A alternativa Apache-2.0 permite a combinação, preservando seus avisos. |
| Rust | `cargo metadata --locked --format-version 1` resolveu 553 pacotes, incluindo o projeto local, para a árvore de metadados de todas as plataformas. Predominam licenças permissivas. Isso não equivale à lista efetivamente vinculada em um instalador. |
| MPL-2.0 em Rust | `cssparser`, `cssparser-macros`, `dtoa-short`, `option-ext` e `selectors` declaram MPL-2.0. Verificar, nas fontes efetivamente distribuídas, elegibilidade às licenças secundárias, avisos e acesso ao código conforme a MPL, sem presumir que o rótulo do manifesto resolve todas as condições. |
| Alternativas LGPL em Rust | As versões 5.3.0 e 6.0.0 de `r-efi` oferecem `MIT OR Apache-2.0 OR LGPL-2.1-or-later`. Não é necessário selecionar LGPL quando se cumpre uma alternativa permissiva válida. |
| NuGet | Foram identificadas 17 combinações diretas de nome/versão nos projetos, incluindo testes. Metadados oficiais dos 15 pins fixos declaram MIT, Apache-2.0 ou PostgreSQL. Duas referências `Microsoft.Extensions.*` usam `8.*`; não houve restore .NET para fixar essas versões nem examinar transitivas. |
| Biblioteca vendorizada | `android-native-keyring-store` 1.0.0 é MIT OU Apache-2.0. Os dois arquivos de licença, avisos e registro `AEGIS-PATCH.md` permanecem intactos; a atribuição à Aegis não lhes é aplicada. |
| Android | O Gradle inclui `com.google.android.gms:play-services-location:21.3.0` e Firebase Messaging, além de AndroidX e Material. O POM oficial do primeiro declara **Android Software Development Kit License**. É pendência material, não uma licença permissiva automaticamente compatível. |
| Infraestrutura e APIs | PostgreSQL, Qdrant, Neo4j Community e serviços externos são componentes separados no arranjo examinado. Comunicação por rede não os relicencia. Mapas, dados, modelos e APIs conservam seus termos. Este exame não decide a natureza de toda possível combinação futura. |
| Recursos visuais | `ruri-source.md` documenta a cadeia técnica dos arquivos do proprietário e o uso de PixelLab. Não contém demonstração completa dos direitos sobre personagens, referências ou permissão de redistribuição sob AGPL. Confirmar direitos e os termos do fornecedor antes de conceder direitos sobre esses recursos. |

### Android: decisão necessária antes de distribuir

O POM exato de `play-services-location` remete aos termos do Android SDK. Esses
termos contêm direitos limitados e restrições sobre cópia, modificação e
redistribuição, ressalvadas as licenças abertas de componentes específicos.
Não é correto tratar toda a dependência como Apache-2.0 apenas porque outros
SDKs Android ou partes dela usam software livre.

A combinação de código AGPL com essa biblioteca exige avaliar o componente
efetivamente empacotado, seus termos e eventual enquadramento nas disposições
de bibliotecas de sistema. Essa qualificação não foi presumida aqui. As opções
são substituir a dependência por alternativa compatível, obter permissões
suficientes, ou redigir uma permissão adicional de vinculação estreita sob a
seção 7, concedida pelos titulares competentes. Uma exceção da Aegis não concede
direitos sobre o SDK que o Google não conceda. Qualquer exceção deve ser uma
decisão expressa e revisada, não uma consequência oculta da atribuição.

### Inventário e avisos dos artefatos

As declarações de licença dos manifestos são evidência inicial, não certificação
de compatibilidade. Conferir as licenças e os arquivos de aviso das versões
efetivamente incluídas, recursos nativos, transitivas Android/NuGet e condições
das licenças de dados. Reunir os textos e créditos necessários para cada
instalador e imagem distribuída. O inventário CSV entregue com a análise
identifica o alcance de cada entrada; não é um SBOM completo de binários.

## Verificação da atribuição e das interfaces

| Disposição | Aplicação nesta proposta |
| --- | --- |
| Seção 0: Avisos Legais Apropriados | Configurações → Sobre apresenta criador e copyright, ausência de garantia, direito de redistribuir sob a licença e acesso ao texto integral. |
| Seção 5(d) | O mesmo componente é inserido nas telas Sobre existentes da PWA e do host nativo compartilhado por Android/Windows. A exceção para interfaces do Programa sem avisos é preservada; não se exige criar interface gráfica. |
| Seção 7(b) | Apenas identificação razoável da pessoa que criou o material original. Aceita tradução, adaptação, consolidação e delimitação da contribuição; não força atribuição falsa de alterações. |
| Seção 7: aviso nas fontes | Comentários em 397 arquivos de implementação originais remetem a `/ADDITIONAL_TERMS.md`. Nesses arquivos, a alteração mecânica acrescenta somente avisos; as duas entradas de interface também incorporam o componente legal. Vendor, wrappers e recursos gráficos conservam sua situação própria. |
| Seções 7 e 10: limites | Sem logotipo, link promocional, banner permanente, pagamento, restrição comercial ou necessidade de aprovação. Forks podem reorganizar os créditos e renomear o produto. Não se impede a remoção de restrições incompatíveis. |
| Seção 13 | A seção Sobre oferece acesso ao repositório do projeto. Na publicação de uma versão modificada, oferecer de forma proeminente o Código-Fonte Correspondente exato aos usuários remotos; um link genérico ao upstream não basta para um fork diferente. |

Os quatro textos legais são importados no componente Vue e ficam incluídos no
JavaScript do aplicativo, para leitura mesmo sem rede após o carregamento.
O build Docker da PWA passa a receber esses arquivos; sua lista de exclusão
foi ajustada. O link de código usa o navegador do sistema no host nativo e um
link comum na PWA. Não foram alterados contratos de API, identidade de versão,
atualizadores ou lógica funcional.

O restante da disponibilização de Código-Fonte Correspondente, instruções de
instalação quando exigíveis e avisos de terceiros deve acompanhar cada futura
distribuição, inclusive imagens de backend. Esta proposta não modifica o
processo de release nem declara os binários anteriores conformes.

## -only e -or-later

`AGPL-3.0-only` escolhe apenas a versão 3. `AGPL-3.0-or-later` daria a cada
destinatário a opção de usar também versões futuras publicadas pela FSF. A
escolha aqui é **-only**, conforme solicitado, explicitada nos avisos fora da
licença. O modelo “or any later version” no apêndice oficial permanece intacto;
não é a declaração de licença deste programa. Não há promessa de migrar
automaticamente para versões futuras.

## Verificações executadas

- Build e typecheck dos dois hosts: aprovados (`npm run build` em
  `apps/aegis-node` e `frontend/aegis-pwa`).
- Testes existentes: 145/145 no host nativo e 85/85 na PWA, sem falhas.
- Teste de navegador da identidade existente ampliado para os avisos legais:
  30 cenários aprovados em Chrome, com os dois hosts reais, IPC/API simulados,
  larguras 320/360/412/800/1100 e fontes 100%/200%. Verificou crédito acessível,
  ausência de garantia, redistribuição, texto integral dos quatro arquivos
  disponível sem rede, abertura do navegador nativo e recuperação de falha.
  A importação de Vite no teste foi ajustada para aceitar caminhos Windows.
- Conferência visual das capturas de web estreita e host Windows largo:
  crédito legível, sem sobreposição ou rolagem horizontal nos avisos.
- Comparação com a base: após retirar somente os comentários de aviso e
  normalizar os finais de linha do checkout, os corpos de 395 arquivos são
  idênticos. As outras duas fontes originais são as entradas `App.vue`, que
  passam a incluir o componente legal. O componente novo é compartilhado.
- `LICENSE` idêntico byte a byte ao download oficial; vendor inalterado;
  `git diff --check` aprovado.

O Docker não pôde ser construído porque o serviço Docker local está desligado;
o ajuste de contexto foi revisado nos arquivos. Não foram executados testes
.NET/Rust/Android para alterações que acrescentam somente comentários nessas
linguagens, nem validação física de Android ou Windows/WebView2. Nenhum
instalador foi construído, assinado ou publicado nesta tarefa.

## Fontes normativas consultadas

- [AGPLv3 oficial](https://www.gnu.org/licenses/agpl-3.0.html): seções 0, 2,
  4–7, 10, 13–17. Texto obtido de
  <https://www.gnu.org/licenses/agpl-3.0.txt>, com SHA-256
  `0d96a4ff68ad6d4b6f1f30f713b18d5184912ba8dd389f86aa7710db079abcb0`.
- [FSF: avisos legais e atribuição razoável, 27/01/2026](https://www.fsf.org/blogs/community/gpl-compliant-legal-notices-author-attributions):
  orientação sobre localização flexível, limites dos créditos e risco de
  atribuição inexata.
- [Mozilla: MPL 2.0 FAQ, Q14](https://www.mozilla.org/en-US/MPL/2.0/FAQ/#q14-may-i-combine-mpl-licensed-code-and-lgpl-licensed-code-in-the-same-executable-program):
  condições de combinação com licenças secundárias.
- [POM de Play Services Location 21.3.0](https://dl.google.com/dl/android/maven2/com/google/android/gms/play-services-location/21.3.0/play-services-location-21.3.0.pom)
  e [termos do Android SDK](https://developer.android.com/studio/terms.html).
- [Google: avisos de bibliotecas abertas no Android](https://developers.google.com/android/guides/opensource).
- Metadados oficiais NuGet (`api.nuget.org/v3-flatcontainer`), lockfiles npm e
  metadados dos crates fixados em `Cargo.lock`.

Esta é uma análise técnica de preparação de licenciamento, não uma decisão
judicial sobre titularidade ou compatibilidade. A cláusula adicional e a
solução para os SDKs Android podem merecer revisão jurídica antes da publicação.
