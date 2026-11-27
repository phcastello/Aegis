# Anexos no chat

O botão **+** à esquerda do campo de mensagem aceita até quatro arquivos de **512 MiB cada** no Android e no Windows. Uma mensagem pode levar até 2 GiB de anexos. Você pode digitar ou ditar a pergunta. No Android também pode compartilhar um arquivo de outro app para a Aegis e acrescentar a pergunta antes de enviar. O histórico permite baixar o original. Para voltar a ele em uma conversa longa, mencione o nome do arquivo.
[Compartilhamento no Android](https://developer.android.com/develop/ui/compose/sharing/receive)

Antes de enviar, confira os anexos na faixa acima do texto. As imagens aparecem em miniatura. Documentos, ZIPs e formatos de imagem que o dispositivo não consegue exibir recebem um cartão com o nome do arquivo. Use o × para remover um anexo. Se faltar espaço na tela, deslize a faixa para ver os demais.

A prévia é local. O seletor, a colagem e o arraste usam uma URL temporária do arquivo, liberada quando o cartão sai da tela. Para fotos compartilhadas por outro aplicativo Android, a ponte nativa fornece uma miniatura reduzida, e o envio continua usando o original.
[Miniaturas de arquivos locais na Web](https://developer.mozilla.org/en-US/docs/Web/API/File_API/Using_files_from_web_applications#example_using_object_urls_to_display_images) · [Decodificação de imagens no Android](https://developer.android.com/topic/performance/graphics/load-bitmap)

## Envio e armazenamento

O cliente registra o tamanho do arquivo e envia os dados binários em blocos numerados de até 4 MiB. Um bloco idêntico pode ser retransmitido. A API verifica se todos chegaram antes de vincular os originais à mensagem. Os blocos ficam no PostgreSQL e o download aceita intervalos de bytes. Os envios que ficaram sem mensagem expiram depois de 24 horas. O limite de 8 MiB no JSON continua atendendo às versões antigas que enviam arquivos em base64.
[Leitura em partes no navegador](https://developer.mozilla.org/en-US/docs/Web/API/Blob/slice)

Um proxy externo precisa aceitar corpos de pelo menos 4 MiB para os blocos. A rota de mensagens mantém o limite de 12 MiB para o envelope usado pelas versões antigas.
[Configuração de tamanho no Nginx](https://nginx.org/en/docs/http/ngx_http_core_module.html#client_max_body_size)

## Leitura pela Aegis

A Aegis recebe uma prévia dos anexos. Em texto, CSV, JSON e código, ela pode buscar uma expressão ou ler outro trecho quando a resposta estiver fora da prévia. Em DOCX, XLSX e PPTX grandes, o servidor extrai até 128 MiB de texto, lendo até 512 MiB de XML. Se a extração for parcial, a Aegis recebe um aviso sobre o alcance da busca. PDFs pequenos e imagens compatíveis podem seguir diretamente para o modelo. Em PDFs grandes, o servidor usa o Poppler para obter uma prévia e consultar o texto por página. Imagens grandes recebem uma prévia visual reduzida com libvips.
[Poppler](https://manpages.debian.org/bookworm/poppler-utils/pdftoppm.1.en.html) · [Miniaturas com libvips](https://www.libvips.org/API/current/using-vipsthumbnail.html)

Num ZIP, cada entrada conserva seu caminho. A Aegis pode listar arquivos por nome, consultar uma entrada e cruzar dados de pastas diferentes. O índice comporta até 4.096 entradas no conjunto de anexos, com até 2 GiB de conteúdo descompactado para leitura e dois níveis de ZIPs internos. A prévia textual inicial soma até 96 mil caracteres. O leitor sinaliza cortes e limites, e o tamanho da prévia não reduz o arquivo disponível para download. ZIPs ilegíveis ou com senha não podem ser interpretados.
[Arquivos ZIP no .NET](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive?view=net-8.0)
