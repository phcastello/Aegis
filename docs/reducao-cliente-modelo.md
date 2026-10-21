# Redução seletiva do cliente de modelo — A12

Foram retirados somente `IAegisModelClient.GenerateAsync` e `IAegisModelClient.StreamAsync`, suas implementações genéricas em `OpenAIResponsesClient` e os dois métodos que apenas lançavam `NotImplementedException` no double de `ToolLoopTests`. As implementações com ferramentas, parsers compartilhados, métricas, auditoria/redação de dados e tratamento de fases permanecem iguais.

A revisão anterior à remoção pesquisou referências dos tipos, chamadas qualificadas e não qualificadas, registros DI e reflexão em `backend/src`, `backend/tests`, scripts e demais pacotes do repositório, excluindo artefatos gerados. O resultado foi:

| Símbolo/caminho | Consumidores encontrados | Decisão |
| --- | --- | --- |
| `IAegisModelClient` | Implementação `OpenAIResponsesClient`, registro HTTP em `DependencyInjection`, injeção em `AegisToolLoop`, double de `ToolLoopTests` | Preservar interface com os dois métodos operacionais. |
| `GenerateAsync(ModelRequest)` / `StreamAsync(ModelRequest)` do cliente | Declaração, implementação e os dois stubs do double; nenhuma chamada direta, interna ou por interface | Remover os métodos. |
| `RespondWithToolsAsync` | `AegisToolLoop.RunAsync` e testes de payload/uso/redação | Preservar. |
| `RespondWithToolsStreamAsync` | `AegisToolLoop.StreamAsync` e teste integrado com transporte HTTP sintético | Preservar. |
| `ModelCompletionResponse` | `ChatService.RunToolCompletionAsync` monta a resposta do loop | Preservar o tipo. |
| `ModelStreamChunk` | Contrato/implementação de `IAegisToolLoop`, `ChatService`, doubles e testes | Preservar o tipo e o streaming do loop. |
| `IConversationTitleGenerator.GenerateAsync` | `ConversationTitleService`, `OpenAiTitleGenerator`, testes de títulos | Preservar; é outro contrato. |
| Reflexão | Sanitização privada de títulos em teste, `DispatchProxy` de contratos de banco/voz/memória/Google e leitura de metadados da fixture nativa; nenhum despacho dinâmico para os métodos removidos | Nenhum consumidor adicional localizado. |

Comandos de reprodução da inspeção, executados na raiz:

```sh
rg -n 'IAegisModelClient|OpenAIResponsesClient|ModelCompletionResponse|ModelStreamChunk' backend/src backend/tests
rg -n 'GenerateAsync\(|StreamAsync\(|GetMethod\(|GetMethods\(|InvokeMember\(|DispatchProxy' backend/src backend/tests
```

Os resultados de `StreamAsync` devem ser classificados pelo tipo: `IAegisToolLoop.StreamAsync` e `ReadAsStreamAsync` têm consumidores e continuam. A ausência de uso pelo proprietário não foi usada como evidência.

Validação local: compilação .NET 8 e testes de loop com ferramentas, payload/ordem de contexto, métricas/redação, streaming HTTP sintético e títulos. A suíte completa do backend é executada novamente após a remoção; serviços externos e fixtures físicas continuam condicionados às variáveis explícitas existentes. Nenhuma chamada paga ou conexão com produção é necessária para esses testes.
