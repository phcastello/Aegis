# Anexos no chat

No Android e no Windows, o clipe no compositor permite selecionar até quatro
arquivos, com até 8 MiB somados por mensagem. A pergunta pode ser digitada ou
ditada pelo microfone do compositor. No Android, também é possível compartilhar
um arquivo de outro aplicativo para a Aegis e acrescentar a pergunta antes de
enviar. Os anexos ficam vinculados à mensagem. Ao reabrir a conversa, é
possível consultar o conteúdo ou baixar o arquivo original.
[Compartilhamento no Android](https://developer.android.com/develop/ui/compose/sharing/receive)
· [Seletor de arquivos](https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/input/file)

## Leitura pelo modelo

A API entrega imagens JPEG, PNG, WebP e GIF como imagens, e PDFs como documentos.
Para texto, incluindo CSV, JSON e código-fonte, o backend identifica UTF-8 e
UTF-16 com marca de ordem de bytes. Documentos Office Open XML usam a entrada
de arquivo do provedor. O nome de cada anexo acompanha o seu conteúdo.
[Imagens na Responses API](https://developers.openai.com/api/docs/guides/images-vision)
· [Entrada de arquivos](https://developers.openai.com/api/docs/guides/file-inputs)

O backend abre cada ZIP em memória. Os caminhos internos identificam a origem
de cada trecho. Assim, uma pergunta pode cruzar um CSV em uma pasta com um
documento em outra. Um binário sem leitura disponível aparece identificado
pelo caminho. Os limites por mensagem são 256 entradas de arquivo ou diretório
e 24 MiB de dados descompactados, incluindo até dois níveis de ZIPs internos.
A entrada de texto comporta até 96 mil caracteres com indicação de corte.
ZIPs ilegíveis, protegidos por senha ou que ultrapassam os limites de
descompactação produzem um erro no envio.
[Leitura de entradas ZIP no .NET](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive?view=net-8.0)

Os bytes originais ficam no PostgreSQL. O histórico carrega os metadados, e o
download usa os identificadores da mensagem e do anexo. Em uma conversa longa,
mencionar o nome do arquivo ajuda a indicar qual anexo deve ser consultado
novamente. O gateway reserva 12 MiB para o JSON das duas rotas de envio, o que
acomoda a expansão do base64. Uma instalação com proxy externo precisa aceitar
esse tamanho até o gateway da Aegis.
[Limite de corpo de requisição no Nginx](https://nginx.org/en/docs/http/ngx_http_core_module.html#client_max_body_size)
