# Contagem de ações de pesquisa web

O servidor registra cada ID de ação nativa observada no provedor Responses. O custo
de uma pesquisa depende do faturamento do provedor. O relatório fornece contagens
de ações, sem presumir uma equivalência monetária entre os tipos.

## Consultar

Na instalação atualizada do servidor, use o NodeAdmin já incluído no container da API,
com as mesmas permissões administrativas locais de leitura do banco.

```bash
docker compose exec -T aegis-api dotnet /app/node-admin/Aegis.NodeAdmin.dll \
  web-search-usage --from 2026-11-26 --to 2026-11-27
```

O exemplo consulta a data inteira de 26/11 em UTC. O início é inclusivo e o fim
exclusivo. Também são aceitos horários RFC3339 com sufixo Z e precisão de segundos
ou frações de segundo. O resultado é um JSON com `search`, `open_page`,
`find_in_page`, `unknown`, `other`, `total`, o intervalo efetivamente usado e
um detalhamento das quantidades por último estado observado.

## Como interpretar

- `search` é uma ação de busca, mesmo se a API devolver várias consultas nela.
- `open_page` é uma abertura de página. `find_in_page` é uma busca no conteúdo
  de página. `other` significa ação de tipo anunciado pela API ainda não reconhecido.
- `unknown` significa que a ação foi observada, mas o tipo não estava disponível.
  Em particular, uma interrupção do stream pode deixar apenas o evento inicial.
- O mesmo ID de ação é atualizado, nunca somado de novo pelas notificações
  SSE ou pela resposta final. A data de atribuição é a primeira observação local em UTC.
  Os estados incluem chamadas ainda em execução, concluídas, incompletas e com falha,
  conforme as informações efetivamente recebidas. Uma chamada sem estado final
  não é automaticamente declarada cancelada pelo provedor.

O registro é independente das mensagens e só guarda os IDs técnicos de ação/resposta,
tipo, estado e horários de observação. Nenhuma consulta, URL, texto, credencial ou
cópia da resposta entra nessa tabela. As informações de evidência por conversa
continuam no contrato de pesquisa existente.

A documentação oficial separa os três tipos de ação e identifica a ação
de busca como tarifada. A contagem da Aegis começa quando o novo coletor e sua
migração estiverem instalados. Ela pode diferir do painel da API por uso de outros
clientes, ações do provedor não recebidas localmente, interrupção antes do primeiro
evento, atraso no faturamento ou falha de persistência. A API de streaming não
promete fornecer o corpo final depois de um cancelamento; por isso não inferimos
o tipo ausente e não estimamos valores do painel. Falhas de escrita emitem alerta
técnico sem incluir dados privados, e eventos repetidos podem recuperar uma
escrita que falhou. Não há reconstrução automática das pesquisas anteriores.

Fontes [Ações da ferramenta web](https://developers.openai.com/api/docs/guides/tools-web-search#output-and-citations)
e [eventos de streaming da Responses API](https://developers.openai.com/api/reference/resources/responses/streaming-events#responseweb_search_callin_progress).

A migração `20261126180000_AddWebSearchUsage` é aditiva. Deve ser validada em banco
descartável e aprovada pelo procedimento existente de migração antes da publicação
privada. A aplicação anterior pode usar o esquema resultante; não execute Down para
reverter imagem. Nenhuma migração de produção é acionada pelo relatório.
