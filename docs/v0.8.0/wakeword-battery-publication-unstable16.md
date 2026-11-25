# Publicação da correção hands-free — unstable.16

A versão **0.8.0-unstable.16** foi publicada em 16/11/2026 às 18:58 BRT.
Ela é a versão recomendada do canal de desenvolvimento.

- [Release oficial](https://github.com/phcastello/Aegis/releases/tag/node-v0.8.0-unstable.16).
- [APK Android ARM64 assinado](https://github.com/phcastello/Aegis/releases/download/node-v0.8.0-unstable.16/Aegis-Android-arm64.apk).
- [Workflow concluído com sucesso](https://github.com/phcastello/Aegis/actions/runs/43963321974).
- Fonte do aplicativo: `ec8906958eea8aca9f5d5259f84078a76b1b57a3`.
- Controles confiáveis: `8607dc009b20fda001f69b43e506b18bbfa837be`.
- Código Android: `8000016`; pacote: `com.aegis.node`.

O escopo selecionado pelo publicador foi somente `native`, sem migrations
pendentes. Os builds Android e Windows, a checagem de instalação/perfil Windows,
a assinatura isolada e a publicação do feed terminaram com sucesso.

## Verificação dos bytes oficiais

O APK tem 62.304.201 bytes e SHA-256:
`13bef8b5a22f55b7f3a6bc50d3873a4affde1907b1fd4510be1ab7d503055ee1`.

O digest do asset GitHub, `SHA256SUMS`, o manifesto e o feed recomendado
coincidem. A reserva do ledger, o source, o run e a tag também foram conferidos.
O certificado oficial foi validado, com SHA-256:
`df5286c556657ddcee6ce79c510b967678bac8ced18d082db0e318bc47debb31`.

As verificações estáticas do APK oficial passaram:

- Identidade/versão embutidas corretas; aplicativo sem flag de debug.
- Guard existente de distribuição: ARM64 e ausência de fixtures.
- Provider `ai.onnxruntime.TelemetryInitializer` ausente do manifesto.
- Classes e membros necessários do ONNX preservados pelo R8: 84 classes,
  383 campos, 814 métodos, incluindo 188 métodos nativos.
- Nenhum peso privado, PCM ou pacote de treinamento embutido nas 749 entradas.
- Alinhamento ZIP de 16 KiB e todos os segmentos LOAD dos seis ELF ARM64 com
  alinhamento de 16 KiB.
- Os oito payloads executáveis DEX/ELF têm os mesmos hashes do candidato
  verificado antes da assinatura.

Antes do envio da fonte, 189 testes JVM em 17 suítes passaram sem erros, falhas
ou testes ignorados. Incluem a paridade de 164 janelas privadas com ONNX Runtime
1.31.1 e os seis testes da confirmação assíncrona. Os 19 contratos hands-free em
Node e a verificação de sintaxe dos três scripts de reprodução também passaram.

## Ativação e limites

Atualizar o aplicativo em Configurações → Atualizações. Depois desativar a
escuta, importar o ZIP privado `aegis-strong-verifier-20261116.zip`, conferir
`Strong + verificador` e reativar a escuta. O ZIP privado é entregue separadamente
no workspace do proprietário; não acompanha o APK nem os assets públicos.
O TFLite privado anterior permite voltar ao Strong pelo mesmo fluxo.

As verificações de software e distribuição estão concluídas. Não havia telefone
conectado para homologação física. A taxa de alarmes em conversa/música/TV,
a resposta em ARM e a autonomia de bateria ainda precisam de avaliação no
aparelho. Os resultados offline dos conjuntos conhecidos não comprovam esses
resultados físicos. Esta entrega não encerra a etapa 1 nem inicia as
funcionalidades da etapa 2.

- [Descrição da correção e instruções completas](wakeword-battery-20261116.md).
- [Protocolo e resultados do wake word](wakeword-verifier-evaluation.md).
- [Investigação de bateria](hands-free-battery-20261116.md).

Evidências sem áudio/pesos ficam no servidor 22 em
`/var/tmp/aegis-wakeword-battery-20261116-data/release-unstable16/`:
`official-release-verification.json` e `wake-apk-check.json`.
