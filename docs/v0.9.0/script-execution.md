# Execução de scripts em Python

A Aegis pode rodar Python a partir do chat digitado, da voz manual ou do
hands-free. A ferramenta atende cálculos, sorteios, simulações e transformação
ou análise de dados. O código pode receber texto UTF-8 pela entrada padrão
e abrir arquivos anexados à conversa.

Além da biblioteca padrão, o ambiente inclui `openpyxl` para ler e gravar XLSX,
`pypdf` para ler e editar PDF, e `png` (PyPNG) para ler e gravar PNG.
O módulo `aegis_docx` oferece `read_paragraphs(fonte)` para ler o texto do corpo
e das tabelas e `write_document(destino, parágrafos, tables=...)` para criar DOCX
com parágrafos e tabelas simples. `tables` recebe uma sequência de tabelas,
cada uma formada por linhas e células. Os pacotes são fixados por versão e
SHA-256. As verificações da imagem abrem os arquivos produzidos com o runtime
empacotado. O modelo escolhe o formato que servir ao pedido.
[openpyxl](https://openpyxl.readthedocs.io/en/stable/) ·
[pypdf](https://pypdf.readthedocs.io/) ·
[PyPNG](https://pypng.readthedocs.io/) ·
[Office Open XML](https://ecma-international.org/publications-and-standards/standards/ecma-376/)

## Arquivos por conversa

Se você enviar um ZIP com planilhas e pedir um CSV consolidado, a Aegis pode
abrir os originais e entregar o CSV na própria resposta. Em seguida, você pode
pedir “agora refaz sem março”. Os arquivos de trabalho ficam disponíveis por
72 horas desde a última mensagem dessa conversa. Uma nova mensagem renova o
prazo. Abrir o histórico ou baixar um arquivo mantém o prazo anterior.
Quando o prazo termina, o chat conserva os originais e cada versão entregue.

O processo Python termina após cada chamada. A próxima execução recebe os arquivos
intermediários salvos em `/work`. Isso permite retomar o trabalho sem manter um
interpretador ocioso. A limpeza também ocorre quando a conversa é excluída.
[Ciclo de vida de um Codespace](https://docs.github.com/en/codespaces/about-codespaces/understanding-the-codespace-lifecycle)

Para rever uma análise, a Aegis pode selecionar a entrega mais recente ou a
entrega indicada pelo nome ou identificador do arquivo. Se os intermediários
expiraram, ela reexecuta as etapas registradas com os mesmos anexos e verifica
as somas SHA-256 dos intermediários reconstruídos. A recuperação trabalha em
espaço temporário e conserva os downloads anteriores. Ela aceita até 20 etapas
e tem até 75 segundos para a reexecução. Se a entrega preceder o recurso, usar
outra versão do ambiente ou depender de uma etapa que produz dados diferentes
a cada execução, a Aegis informa a limitação e pode recomeçar pelos originais.

`/inputs/index.json` lista nomes, identificadores, tamanhos e caminhos dos
arquivos anexados a essa conversa. O executor monta os originais selecionados para leitura.
Arquivos regulares escritos em `/outputs` ficam disponíveis para download na
resposta concluída. Um trabalho delegado usa as mesmas três pastas. Os arquivos
de conversas diferentes ficam isolados. [Entrega de arquivos no chat](anexos.md)

O runtime inclui zlib para que `zipfile` possa ler e gravar arquivos ZIP com
compressão DEFLATE, inclusive planilhas e outros documentos que a utilizam.
[ZIP no Python](https://docs.python.org/3/library/zipfile.html)

## Decisão e entrega

A Aegis executa tarefas curtas no mesmo turno com `run_python`. Para um
trabalho que deve causar uma espera perceptível, ela usa `delegate_python`
antes da primeira execução. O aviso de intenção é salvo no chat e falado no
hands-free. A fila persistente guarda o vínculo com o pedido e o trabalhador
recebe o contexto necessário. Ele pode usar apenas a ferramenta Python.
O usuário pode continuar a conversa, cancelar pelo estado do trabalho ou
reabrir o aplicativo para ver o resultado salvo.

Enquanto o hands-free estiver ativo, o Node que fez o pedido lê a resposta
completa a partir da conclusão quando o áudio estiver livre. As decisões de
retomar a locução, parar de ler ou aceitar um novo pedido seguem o mecanismo
que já existe para pesquisa em segundo plano.

## Sorteio

Para sortear um inteiro de `a` a `b`, com os dois extremos incluídos, o
script usa `a + secrets.randbelow(b - a + 1)`. Um intervalo com extremos
iguais devolve aquele inteiro. A Aegis devolve o valor produzido na execução.
O módulo usa a fonte de aleatoriedade do sistema. [Documentação de secrets](https://docs.python.org/3/library/secrets.html)

## Isolamento e implantação

O backend empacota CPython para WASI e executa cada script no Wasmtime. A
instância recebe a biblioteca padrão como somente leitura e a entrada do
pedido. O executor restringe rede, acesso ao sistema de arquivos do servidor,
variáveis de ambiente e recursos por execução. Se o executor empacotado
estiver indisponível, a ferramenta fica fora do catálogo. As versões e
somas de verificação das dependências estão em
[`backend/script-runtime/build.sh`](../../backend/script-runtime/build.sh).
A distribuição ocorre na imagem do backend, sem instalar Python nos Nodes.
[Isolamento do Wasmtime](https://docs.wasmtime.dev/security.html)
· [Limitações do Python em WASI](https://docs.python.org/3/library/intro.html#webassembly-platforms)

O contrato aceita até 16 KiB de código e 256 KiB na entrada textual por execução.
Anexos usam leitura de arquivo e seguem os limites do chat. Cada script tem até
30 segundos, 96 MiB de memória WebAssembly e 16 KiB por fluxo de saída textual.
O servidor executa até dois scripts simultaneamente e serializa os do mesmo chat.
O espaço intermediário admite até 768 MiB e 4.096 itens por conversa. Um volume
guarda os intermediários entre chamadas, e as duas execuções compartilham um
`tmpfs` de 1 GiB para intermediários abertos e novos resultados. Os originais
selecionados são preparados em disco e liberados no fim da execução.
[Volumes Docker](https://docs.docker.com/engine/storage/volumes/) · [Armazenamento temporário no Docker](https://docs.docker.com/engine/storage/tmpfs/)
O PostgreSQL guarda o identificador da execução, a duração, o desfecho, a versão
do runtime, os identificadores de entrada e saída, a estrutura do código com
nomes, comentários e literais removidos e as somas dos intermediários. Receitas
vinculadas ao trabalho conservam o código e os parâmetros em um registro
criptografado pela proteção de dados já usada no servidor. A preparação da
resposta e a interface recebem somente o resultado da ferramenta e os anexos.
Os diagnósticos sem receita expiram em 30 dias. Os registros de receita acompanham
a conversa e saem da base quando ela é excluída.
