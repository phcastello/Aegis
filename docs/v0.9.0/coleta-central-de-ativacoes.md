# Coleta privada de exemplos da palavra de ativação

Em **Configurações → Áudio e voz → Hands-free**, ative a sincronização no aparelho pareado que participará da coleta. A escolha é individual para Android e Windows e começa desligada. No Android, a opção anterior **Guardar ativações localmente** continua com seu próprio controle e exportação. O envio central começa com novas detecções, incluindo disparos acidentais que podem conter conversas próximas.

Cada exemplo guarda o som ao redor do disparo, o trecho posterior disponível, a data, o grupo da gravação, a versão do detector e os scores disponíveis. O contexto tem até dois segundos antes da decisão, com margem de até 200 ms. Um primeiro comando pode durar até 20 s. No Android, um candidato descartado pode conservar até três segundos posteriores. A amostra pode ser menor quando o microfone para. A classificação humana tem duas perguntas independentes:

1. A pessoa disse a palavra de ativação?
2. A fala era dirigida à Aegis?

Uma detecção suprimida porque outro aparelho ganhou a eleição pode receber **correta** na primeira pergunta. O resultado observado da eleição e o tratamento do pedido ficam em campos separados. Nenhuma transcrição é necessária para o dataset. O revisor permite deixar cada pergunta sem resposta ou marcá-la como incerta.

## Transporte e armazenamento

O cliente guarda os envios pendentes em armazenamento privado, vinculados ao pareamento. O Android usa WorkManager com requisito de rede e retentativa exponencial. Uma confirmação HTTP 204 libera a cópia da fila. Perder a resposta depois de o servidor gravar a amostra é recuperável: o cliente pode repetir o mesmo PUT. Desligar a opção interrompe novas coletas e descarta envios pendentes daquela autorização. Amostras que o servidor já confirmou continuam no acervo privado.

`PUT /api/nodes/me/wake-samples/{eventId}` usa a credencial do Node habilitado e a identidade autenticada para delimitar os IDs. A API valida o WAV PCM16 mono de 16 kHz, o SHA-256, a duração e os campos de metadados. Um ID existente com conteúdo diferente recebe 409. O corpo máximo é de 1 MiB. O diretório `/var/lib/aegis/wake-dataset`, no volume Docker `wake_dataset`, guarda um registro imutável por nó e evento. O limite de armazenamento central padrão é 1 GiB, configurável em `WakeDataset__MaximumBytes`. Ao encher, a API devolve 507, preserva as amostras existentes e os clientes mantêm a fila para outra tentativa. Os arquivos são dados privados do proprietário e devem ser incluídos em seu backup de volumes.

No Windows, uma fila SQLite no diretório privado do aplicativo conserva as
amostras entre reinicializações. Ela armazena até 100 MiB de envios pendentes
e repete a entrega ao servidor enquanto o aplicativo estiver em execução. Com o
mesmo identificador, a confirmação pode ser repetida mesmo que a conexão caia.
[Transações atômicas no SQLite](https://sqlite.org/atomiccommit.html)

## Revisar as amostras

Em uma máquina com acesso administrativo privado ao Compose e ao checkout da Aegis, copie o volume para um diretório privado novo e gere um ZIP de revisão com Python 3:

```sh
mkdir -m 700 amostras-aegis
docker compose cp aegis-api:/var/lib/aegis/wake-dataset/. amostras-aegis/
python3 scripts/hands-free/export-central-dataset.py amostras-aegis revisao-aegis.zip
```

Se o checkout estiver em outra máquina, execute a cópia no servidor e transfira a pasta por seu acesso privado antes do comando Python. Extraia o ZIP e abra `index.html`. Ouça as faixas, classifique a palavra e a intenção, escolha a pasta extraída e use **Exportar dataset datado**. O novo ZIP inclui os WAVs, hashes e rótulos em JSONL e CSV. O áudio só passa pelo revisor local. A extração preserva todas as amostras de origem, e eventos recebidos durante a cópia podem aparecer na próxima exportação.

## Referências

A fila Android usa as restrições de conectividade e o backoff do [WorkManager](https://developer.android.com/develop/background-work/background-tasks/persistent/getting-started/define-work). Os IDs estáveis permitem repetir escritas com a semântica idempotente do [HTTP PUT, RFC 9110 §9.3.4](https://www.rfc-editor.org/rfc/rfc9110#section-9.3.4). O formato de revisão estende o [revisor local de ativações](../v0.8.0/activation-dataset.md).
