# Proveniência e conferência do baseline

O pacote `aegis-battery-baseline-2026-10-25_30.zip` foi recebido e validado em
31/10/2026. A cópia de transporte está na raiz da worktree da implementação,
ignorada pelo Git. Antes de extrair, foram rejeitados caminhos absolutos,
traversal, barras invertidas, duplicatas, links simbólicos e entradas acima dos
limites de tamanho; o CRC do ZIP passou.

Os seis JPGs foram conferidos por tamanho e SHA-256 contra `manifest.json`.
Todos corresponderam. O ZIP e cada entrada original foram preservados byte a byte
em armazenamento privado fora do Git, sob
`~/.local/share/aegis/private/battery/aegis-battery-baseline-2026-10-25_30/`
(diretórios 0700, arquivos 0600). Não há imagens nesta pasta versionada.
O transporte preservado permite repetir a verificação dos originais.

`baseline.md`, `baseline.csv`, `baseline.json` e `manifest.json` são os bytes
recebidos no pacote, sem reescrita editorial. O CSV foi confrontado com a tabela
do pedido; totais, média, mediana e desvio-padrão foram recalculados.
`validation.json` registra a conferência e hashes dos quatro documentos.

- `baseline.csv`: transcrição numérica dos seis dias.
- `baseline.json`: transcrição, metadados, resumo derivado e estimativas visuais
  explicitamente separadas.
- `baseline.md`: análise e limites de interpretação fornecidos.
- `manifest.json`: nomes originais, nomes por data, dimensões, bytes e hashes.

O baseline naturalístico é anterior à feature segundo o proprietário. Não
contém atribuição de consumo à Aegis e não transforma tela desligada em CPU
suspensa. A soma dos tempos exibidos é 130h09min; as horas ausentes não foram
completadas nem tiveram causa atribuída. O uso de 113% foi preservado.

O dia 31/10/2026 e qualquer dia parcial da ativação efetiva ficam excluídos.
O protocolo posterior está em [protocolo de uso](../three-day-protocol.md).
