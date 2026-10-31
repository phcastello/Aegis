# Ganho do aviso de ativação

Baseline inspecionado em `3c04ea255ec9cfbb05124c5e830f9305fd0e3340`, antes
da alteração. O sinal aprovado continua exatamente o mesmo: PCM16 mono 16 kHz,
640 samples/40 ms, duas notas de 16 ms a 1046,5 e 1568 Hz, intervalo de 8 ms e
envelope senoidal. SHA-256 dos 1280 bytes PCM little-endian:
`8249525e9eaf920fbcc1397dba075252b08b26dab5349188fa81f833c94ddbc6`.

O player aplicava ganho `0.18` sobre um sinal cujo pico já era apenas 4997.
O ganho do **AudioTrack exclusivo do aviso** passa a `1.0`, acréscimo de
14,895 dB. Nenhum sample da forma de onda foi alterado.

| Medida digital | Antes | Depois |
| --- | ---: | ---: |
| Ganho do player | 0,18 | 1,0 |
| Pico PCM16 efetivo, antes do mixer do sistema | 899,46 | 4997 |
| RMS PCM16 efetivo, incluindo intervalo | 401,638 | 2231,322 |
| Pico, referência 32768 | −31,229 dBFS | −16,335 dBFS |
| RMS, referência 32768 | −38,232 dBFS | −23,338 dBFS |

Há 16,3 dB de margem digital de pico; nenhuma amostra atinge clipping.
Essas medidas não comprovam volume acústico nem conforto no Samsung.
O proprietário ainda precisa confirmar audibilidade em volume habitual e
conforto no máximo. Não houve alteração de volume global, voz TTS,
notificação, atributos/roteamento do áudio, modo silencioso ou Não Perturbe.

O worker continua fora da UI e da captura, com fila limitada e sem rede.
Agora verifica o resultado de `await(500 ms)`: marcador ou playback head no
fim confirmam conclusão. Ausência do callback com head completo não é falha;
head incompleto ao expirar é falha. Cancelamento invalida a geração, acorda
a espera e sincroniza `stop()` com `play()`, evitando reprodução tardia após
cancelar. Interrupção restaura o flag da thread; limpeza sempre libera o track.
Não foram adicionados timers periódicos, profiling ou mute do microfone.

## Regressão da captura

Suíte host de Kotlin com o JNI/libfvad real: **19 testes passaram**, sendo
13 puros e seis de integração. A onda aprovada é protegida pelo hash anterior,
pico/RMS, intervalo e duração. O classificador recebe o sinal em quatro
alinhamentos (0, 1, 160 e 319 samples), com ganhos de vazamento simulados
0,05, 0,18, 0,5 e 1,0; sinal isolado não confirma fala/áudio útil.

O WAV sintético privado contínuo “Aegis, que horas são?”, 16 kHz PCM16,
foi misturado ao aviso em três pontos (1/4, 1/2 e 3/4). O mesmo helper de
captura de produção preservou cada sample recebido, incluindo começo,
pre-roll e mistura, sem endpoint prematuro. Não se removeu janela alguma.
O VAD, seus parâmetros e a wake word permanecem inalterados. Os testes
anteriores de silêncio, áudio curto, fragmentação, limite, continuação e
nove entradas ambientais/negativas também passaram, sem limite duro.

Reprodução privada:

```sh
python3 scripts/hands-free/run-vad-host-tests.py \
  --library-dir "$HOME/.cache/aegis-hands-free/native-host" \
  --continuous-wav "$HOME/.local/state/aegis/latency-v080/continuous.wav" \
  --output "$HOME/.cache/aegis-hands-free/cue-gain-continuous-vad-20261031"
```

O relatório e o log ficam no diretório privado de saída. O WAV não entra no
Git ou APK. O argumento `--continuous-wav` é opcional; sem ele esse teste
adicional é ignorado, explicitamente, e as demais regressões continuam.

Esses testes executam amostras sintéticas e gravações existentes no host.
Não comprovam reverberação, resposta do alto-falante/microfone, classificação
acústica ou inteligibilidade no aparelho. A mistura pode conservar todos os
samples e ainda mascarar fonemas no ar. Não há nesta rodada medição física de
`AudioTrack`, callback, timeout ou cancelamento no Samsung.
