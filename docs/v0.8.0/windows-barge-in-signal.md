# Barge-in Windows: sinal, referência e bancada

## Escolha da arquitetura

O caminho de reprodução entrega **o PCM próprio efetivamente apresentado** ao processamento de captura. Não se toma o volume do visualizador, a chegada de bytes HTTP, nem a prévia de áudio ainda na fila como evidência acústica. O processamento bidirecional é local, somente durante a interação; o monitor de wakeword em repouso não ganha uma nova thread de AEC, VAD ou classificação.

Referências da pesquisa:

- [WebRTC Audio Processing / AEC3](https://webrtc.googlesource.com/src/+/refs/heads/main/modules/audio_processing/aec3/): cancelamento adaptativo com referência de reprodução, estimador de atraso, saída linear e supressão de eco residual. O processamento de captura da Aegis no Android segue essa arquitetura.
- [Sonora, commit 1632d47](https://github.com/dignifiedquire/sonora/tree/1632d47c1e77d0857f9145bd175134b348541e11): port Rust do WebRTC M145, com validação upstream e CI Windows; a versão **0.3.0** está fixada no Cargo. A implementação utiliza somente AEC3, RNN VAD e primitivas necessárias, sem driver, dispositivo virtual, serviço, C++ auxiliar ou acesso remoto. O processamento permanece possível em rotas sem APO de cancelamento nativo. A licença BSD-3-Clause com os avisos de WebRTC e Sonora é distribuída no instalador Windows.
- [Sidevoice, implementação nativa](https://github.com/sidevoice/sidevoice-voice): entrega referência do callback de reprodução ao AEC3 antes do detector, mantendo o processamento fora do callback; exemplo de aplicação de voz em macOS, Linux e Windows.
- [WinMM waveOutGetPosition](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutgetposition): permite obter a posição do dispositivo nas unidades que o driver efetivamente suporta. O código aceita amostras, bytes ou milissegundos e rejeita posição além do PCM submetido com sucesso. Não assume que a posição de geração seja a posição ouvida.
- [Microsoft AEC Challenge, ICASSP 2022](https://github.com/microsoft/AEC-Challenge/tree/8f861f0f8ffca26a3d1d16f17f2594ea89abe7ac): pares públicos de microfone e playback de eco real, estacionário e com movimento, utilizados para bancada separada.

## Implementação

1. `RenderReference` acompanha a mesma reprodução WinMM que informa a posição ouvida. Retém até quatro segundos de PCM somente em RAM, converte 24→16 kHz preservando duração, desconta a idade dos blocos de microfone ainda na fila e respeita o cursor de reprodução. Não repete quadros em um cursor parado e não lê amostras futuras. Pequenas diferenças de contador são estabilizadas sem antecipar o cursor observado; referência ausente não dá votos positivos. Três blocos indisponíveis consecutivos reinicializam o estado acústico. A remoção do leitor do cursor ocorre sob o mesmo mutex antes de o handle ser fechado, e a geração impede que a saída anterior invalide uma nova. O PCM deixa de ser retido após a cauda acústica (até 800 ms, mais a idade de captura pendente). Nenhum PCM é registrado em diagnóstico.
2. O AEC3 processa cinco blocos consecutivos de 64 amostras por quadro de captura de 20 ms, com saída linear e não linear. O filtro de captura passa-altas a 16 kHz corresponde ao APM WebRTC, pois a interface de blocos não o instancia automaticamente. A busca de atraso é estendida para aproximadamente um segundo e o filtro de sala para 96 ms (24 blocos); filtros de ganho automático não são adicionados. A saída linear é atrasada em 64 amostras para acompanhar a latência da supressão residual. O TTS e o volume de reprodução não são alterados.
3. A decisão combina o RNN VAD do WebRTC (subquadros de 10 ms), piso acústico adaptativo, correlação normalizada de 40 ms com o histórico de referência (até 1,2 s), evidência de supressão medida e o classificador **AGD4** já presente no Android. O mesmo binário de 403.072 bytes é incluído no Windows, sem duplicar o arquivo nem treinar outro modelo. O teste de paridade compara as pontuações causais Rust com as do classificador Kotlin, compilado separadamente, em 16 quadros determinísticos. As saídas do modelo não são usadas como simples limiar global: o histórico de pontuações anteriores, com exclusão dos últimos 200 ms, evita considerar um padrão de eco já observado como nova fala. O caminho acusticamente desacoplado (mute / fone) requer evidência recente de playback ativo e microfone quieto, e perde a qualificação em caso de eco correlacionado.
4. São exigidos cinco votos em oito quadros (quatro quando não há eco/rota desacoplada, ou durante pensamento sem reprodução), preservando até 300 ms anteriores ao primeiro voto, mais o início da fala confirmada, em PCM filtrado e screened. O quadro que confirma o início entra no prefixo uma única vez. Durante raciocínio sem playback, a captura usa somente o RNN VAD e a qualificação acústica, sem rodar AEC3 ou AGD4. Depois de interromper o TTS, o cancelador continua consumindo somente a cauda já ouvida, nunca a reprodução que estava enfileirada mas não foi emitida. Não há botão obrigatório e a decisão não depende do estado de apresentação da janela.

## Resultados da bancada (25/11/2026)

As gravações de microfone usadas para os controles reais são exclusivamente os pares públicos do AEC Challenge. A fala independente foi inserida a partir de clipes sintetizados offline do banco Supertonic 3 já gerado para a Aegis; não foi usada a fala do proprietário. Os IDs públicos foram ordenados deterministicamente por SHA-256 e excluídos dos conjuntos de treino/validação do modelo Android. Separaram-se seis fontes de desenvolvimento (três estacionárias e três com movimento); a primeira avaliação independente usou outras doze. Após testar a extensão da busca/filtro e acrescentar o passa-altas padrão WebRTC com base **somente no desenvolvimento**, o conjunto final usou doze fontes novas, seis estacionárias e seis com movimento. As fontes finais não foram consultadas antes de congelar a configuração.

| Configuração / conjunto | Falas detectadas | Controles de eco com corte falso | Cortes antes da fala | Latência após início da fala (acertos) |
| --- | ---: | ---: | ---: | ---: |
| Misturas sintéticas, sinal final (dois pares de vozes far × near) | 18/20 | 0/12 | 0 | 80–140 ms |
| Desenvolvimento, sinal final (+3 / +9 dB, fala de 460 ms) | 9/12 (4/6; 5/6) | 0/6 | 0 | 120–240 ms |
| Primeira avaliação independente, sinal anterior ao passa-altas | 13/24 (6/12; 7/12) | 0/12 | 0 | 100–140 ms |
| **Avaliação final independente, sinal final** (+3 / +9 dB, fala de 460 ms) | **15/24 (7/12; 8/12)** | **0/12** (96 s) | **0** | **120–160 ms** |

No teste misturado final: pensamento sem reprodução 4/4, referência digital ativa mas microfone sem eco 4/4, eco simulado baixo 4/4, eco alto 4/4, eco de múltiplos caminhos com atraso de 188 ms 2/4. P95 do processamento em bancada Release **2,5 ms por quadro de 20 ms** neste PC; não é a latência fim-a-fim de captura, corte, servidor ou STT. Não se inferem taxa diária de falsos disparos ou consumo de energia a partir desses dados.

Diagnóstico de desenvolvimento: o AEC3 acompanhou atrasos reais de aproximadamente 4–512 ms. A atenuação linear permaneceu baixa em caminhos de eco forte/variável (0,6–8,0 dB), enquanto a saída não linear atenuou aproximadamente 1,2–27,8 dB. A camada passa-altas elevou uma rota móvel de 1,5 para 8,0 dB lineares; reduzir o gate de adaptação produziu mais atraso sem aumentar acertos, e relaxar a pontuação condicionado à emergência do residual não trouxe ganho. Ambos os candidatos foram descartados; não se reduziu o limiar global a partir dos resultados de holdout.

O detector baseado apenas na correlação + RNN VAD + AEC3 chegou a 20/20 nas misturas de laboratório, mas fez 6/6 cortes falsos nos controles públicos reais. Isso motivou a adoção do classificador condicionado à referência e da qualificação conservadora. A ausência de cortes falsos na bancada final é promissora, mas **62,5% das interrupções curtas com eco real não é suficiente para declarar suporte genérico consolidado**. Em caminhos com reverberação, distorção, movimento ou atraso/posição WinMM pouco confiável, ainda pode ser necessário falar mais ou esperar o TTS, e a política prefere não se interromper sozinha. A homologação física deve usar speakers e fones, volume zero/baixo/médio/máximo, início/meio/fim do TTS, comandos curtos, mudança de rota, fala durante pensamento e continuidade na mesma conversa. Nem captura física nem genericidade foram comprovadas em bancada. O cancelamento de outras mídias externas não faz parte deste caminho: a referência é somente do TTS próprio.

## Reproduzir

`scripts/windows-barge-in-bench/prepare.py` prepara os pares do commit público fixado em `scripts/barge-in-dtd/sources.json` e quatro WAVs sintetizados, em um diretório temporário indicado explicitamente. Registra SHA-256 dos WAVs de origem e PCM gerado. O mesmo preparo foi executado de novo e os 24 arquivos PCM do conjunto final coincidiram byte a byte com o replay original. Use FFmpeg para obter PCM16 mono a 16 kHz.

Os quatro clipes sintetizados usados na bancada (nomes da coleção local Supertonic 3 e SHA-256 do PCM convertido) são:

| Arquivo sintético | PCM | SHA-256 |
| --- | --- | --- |
| `supertonic3-m5-command.wav` | `far-1.pcm` | `344d9f24a057b3cb84e4c524ac0b6a787eaf54c9c9eb794e174ea9e17090e2d7` |
| `supertonic3-m4-command.wav` | `far-2.pcm` | `f1b102802210a2971f7265d2415d98e096eb4684e59fb8b476ba7710c1b0ad3a` |
| `supertonic3-m2-oi-diz.wav` | `near-1.pcm` | `298fd5f8e2fc13cdb7176887d438f9eed9fc73e35ac9210771ad453698edeb6b` |
| `supertonic3-m3-oi-diz.wav` | `near-2.pcm` | `5fc018830f86e83fc76d7592cf88049f2899955bc606a317d4b4e4efab6f6086` |

```powershell
python scripts/windows-barge-in-bench/prepare.py --output $scratch --split final `
  --near-wav $synthNear1 $synthNear2 --far-wav $synthFar1 $synthFar2
$env:AEGIS_BARGE_IN_PCM_DIR = Join-Path $scratch 'pcm'
$env:AEGIS_BARGE_ROOM_DIR = Join-Path $scratch 'public'
$env:AEGIS_BARGE_ROOM_SPLIT = 'final'
cargo test --manifest-path apps/aegis-node/src-tauri/Cargo.toml --locked --no-default-features --release --lib replay_mixed_speech -- --ignored --nocapture
cargo test --manifest-path apps/aegis-node/src-tauri/Cargo.toml --locked --no-default-features --release --lib replay_public_room_echo -- --ignored --nocapture
cargo test --manifest-path apps/aegis-node/src-tauri/Cargo.toml --locked --no-default-features --lib windows_barge_in_signal
```

Os replays são marcados como ignorados na suíte comum por dependerem de fixtures externas. Os testes determinísticos de referência, cursor, falhas, classificação causal e prefixo não requerem microfone, rede nem fixtures externas. Os áudios e dados intermediários não entram no repositório nem no instalador.
