# Aegis — avisos de licença e autoria

Aegis — criada originalmente por Pedro Hasson Castello.

Copyright (C) 2026 Pedro Hasson Castello, quanto ao material original de sua
autoria. Cada outro titular conserva seus direitos sobre seu próprio material.

O código original da Aegis é disponibilizado sob a GNU Affero General Public
License, **versão 3 exclusivamente** (`AGPL-3.0-only`), em [`LICENSE`](LICENSE).
Os arquivos que remetem a [`ADDITIONAL_TERMS.md`](ADDITIONAL_TERMS.md) estão
também sujeitos aos termos de atribuição razoável ali previstos, conforme a
seção 7(b). O identificador da licença base, isoladamente, não descreve esses
termos adicionais. Este aviso e os termos adicionais não modificam o texto da
AGPLv3.

Você pode usar, modificar e transmitir o material licenciado, inclusive
comercialmente, nos termos da AGPLv3 e das condições adicionais aplicáveis.
O programa é fornecido **SEM GARANTIA**, na medida permitida pela lei, salvo
garantias expressamente oferecidas; consulte as seções 15 a 17 de `LICENSE`.

## Material de terceiros

As licenças e os avisos próprios prevalecem para o material de terceiros.
Não se reivindica autoria desse material nem se substituem suas licenças.
Em particular:

- `apps/aegis-node/src-tauri/vendor/android-native-keyring-store/`:
  MIT OU Apache-2.0, conforme `LICENSE-MIT`, `LICENSE-APACHE`, os avisos no
  código e `AEGIS-PATCH.md` do diretório. Esses arquivos permanecem inalterados.
- Gradle Wrapper (`gradlew`, `gradlew.bat` e `gradle/wrapper/`): material
  upstream com licença Apache-2.0; os avisos existentes são preservados.
- Pacotes npm, crates Rust, pacotes NuGet e bibliotecas Android: sujeitos às
  respectivas licenças e avisos; constar em um manifesto não os relicencia.
- APIs hospedadas, dados e mapas, serviços de voz, modelos e infraestrutura:
  não recebem uma licença AGPL por serem acessados pela Aegis. Seus termos
  continuam aplicáveis. Preserve a atribuição dos mapas onde apresentada.
- Recursos visuais e fontes de animação, inclusive Ruri/PixelLab: a procedência
  técnica consta em `packages/aegis-client/src/assets/ruri-source.md`.
  A concessão AGPL alcança somente direitos efetivamente detidos ou licenciáveis
  pelo titular; não concede direitos de terceiros sobre personagens ou imagens.

Este aviso não é um inventário completo das licenças de cada binário. Antes de
uma distribuição, reúna os avisos exigidos pelas versões efetivamente incluídas.
Há pendências sobre SDKs Android, recursos visuais e inventário transitivo em
[`docs/licenciamento.md`](docs/licenciamento.md); esta proposta não atesta a
conformidade dos instaladores já publicados nem autoriza novos binários por si só.

## Código-fonte e versões

O repositório original é <https://github.com/phcastello/Aegis>.
Quem distribuir ou operar uma versão modificada deve cumprir as obrigações de
Código-Fonte Correspondente aplicáveis (seções 4 a 6 e 13). Um link genérico ao
repositório original não substitui o código da versão modificada em uso.
Não inclua credenciais, dados pessoais ou segredos no código disponibilizado.
