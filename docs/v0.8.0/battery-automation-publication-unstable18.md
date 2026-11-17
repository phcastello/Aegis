# Automação de bateria — publicação e validação unstable.18

A **0.8.0-unstable.18** foi publicada pelo fluxo oficial em 16/11/2026,
instalada sem apagar dados e validada no S20 FE/SM-G781B com Android 13.

- [Release oficial](https://github.com/phcastello/Aegis/releases/tag/node-v0.8.0-unstable.18).
- [Workflow concluído](https://github.com/phcastello/Aegis/actions/runs/43989387955).
- Fonte: 0ca5b99bb2fe4e7b168c5af638ccc5698f279a6b.
- Android: com.aegis.node, código 8000018.
- APK: 62.336.969 bytes; SHA256
  7cd61c56aa7b220928a23d949e5cfa553b269747c158760fcd353056d4e1238c.

O plano selecionou apenas nativos, sem migrations pendentes. Builds Android e
Windows, assinatura e publicação/feed concluíram com sucesso. O job opcional
acceptance foi skipped; não representa homologação extensa.
Tag, fonte, reserva/ledger, manifesto recomendado, SHA256SUMS, digest do asset
e certificado oficial foram conferidos. O certificado continua
df5286c556657ddcee6ce79c510b967678bac8ced18d082db0e318bc47debb31.

O APK final passou nos guards de quatro métodos JNI String→String, receiver
único não exportado no manifest e DEX, ausência de novas permissões de alarmes
exatos/isenção de bateria, profileable sem debuggable, ORT/R8, alinhamento
ELF/ZIP de 16 KiB e ausência de modelos/áudios privados.

## Software

- Compilação Android real: **240 testes em 21 suítes**, zero falhas/erros/skips.
  Inclui supervisor, coletor, alarmes e 164 janelas privadas ONNX existentes.
- Interface/cliente Node: **194/194**; contratos: **85/85**.
- Typecheck/build Vue e browser mobile/desktop aprovados.
- Helpers Rust portáveis: **4/4**, parser e compilação nativa no CI aprovados.
- Sete estados reais JVM aceitos pelo parser TS, incluindo recuperação inválida.

## Verificação no aparelho com tela apagada

O botão normal foi armado com previsão de 90 min e cancelado antes de apagar
a tela, devolvendo os controles normais. Depois, **Verificar automação (sem
medir consumo)** foi iniciado uma vez; a tela não foi desbloqueada entre A/B/C.

| Fase | Duração real | Captura | Detecção | Tela ligada |
| --- | --- | --- | --- | --- |
| A · Escuta desligada | 60,079 s | 0 amostras | 0 | 0 ms |
| B · Somente microfone | 60,136 s | 956.800 amostras | 0 | 0 ms |
| C · Detecção local | 60,134 s | 956.800 amostras | 956.800 amostras | 0 ms |

As três corridas terminaram completed, com a mesma comparisonKey e os três
runIds desta sequência. Atrasos das fronteiras: 32, 17 e 27 ms. Alarmes exatos
já estavam permitidos pelo Android (allow-listed); nenhuma otimização/permissão
foi alterada para isso.

C registrou 299 chamadas do Strong, CPU do detector de 788,4 ms e CPU do thread
de captura de 1.324,0 ms. O thread inclui o detector; não somar. O modelo ficou
**Strong — época 2 bruto**: openWakeWord ausente, portanto contagem zero esperada.

Ao final: supervisor completed/token=null; nenhuma corrida ativa; gates
requiresResume/suspensionPersisted removidos; zero alarmes residuais;
enabled=true/diagnostic=false preservados e interface **Aguardando Aegis**.
Exportação pela tela oficial de arquivos concluída. O tempo temporário de
tela foi restaurado para 30.000 ms antes da sequência e após exportar, com
leitura de confirmação.

Todas as janelas foram inelegíveis: validation_only e short_window. Cabo e
ferramentas estavam conectados. **A prova confirma as transições e restauração;
não é o comparativo de 90 min, não prova suspensão profunda nem mede economia.**

## Evidência e ensaio longo

Relatório privado: 157.720 bytes, SHA256
adce4c4eec30fab5517df6f8f1b38b863b44c4b1100f0d3b742fe9bc3c96cfff.
Dados brutos, screenshots e checkpoints ficam fora do Git, na pasta privada
local e no 22: /var/tmp/aegis-wakeword-battery-20261116-data/,
subpastas battery-release-unstable18/ e battery-auto-physical/.

Para o ensaio real, carregar até 50–80%, deixar esfriar, retirar carregador/cabo,
abrir **Configurações → Áudio e voz → Investigar consumo de bateria**, tocar
**Iniciar comparação automática** e apagar a tela. Após cerca de 1h30, exportar
o relatório sem áudio. O Android pode alongar etapas; os tempos reais ficam
registrados. Nenhum ensaio longo foi iniciado nesta validação.

[Guia](battery-diagnostic-user-guide.md) ·
[Implementação e limites](battery-automation.md) ·
[Investigação de energia](battery-investigation-20261116.md).
