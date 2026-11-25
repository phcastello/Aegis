# Cancelamento de mídia externa: bancada local

A opção experimental de referência de mídia usa o AEC3 da Aegis com PCM mono de 16 kHz, em quadros de 20 ms, fornecidos em uma linha de tempo monotônica comum. O microfone continua em PCM original quando não há referência autorizada, quando a referência não é válida ou durante a convergência. O cancelamento nativo já existente para a própria voz sintetizada funciona independentemente dessa opção.

## Resultados

As gravações públicas de eco usadas nesta bancada são pares `farend-singletalk` do [AEC Challenge da Microsoft](https://github.com/microsoft/AEC-Challenge), separados das fontes de treino do detector de double-talk. A música instrumental é uma síntese determinística de acordes, percussão e um percurso acústico com reflexões. Não há música comercial. O controle vocal sintético usa eSpeak NG.

Também foram reproduzidos localmente os 19 clipes reais positivos do split de teste já existente, selecionados antes desta bancada, com SHA-256 do PCM conferido. Eles já tinham sido consultados em avaliações do wakeword; esta é uma avaliação de robustez a uma mistura acústica controlada, sem constituir um novo teste independente do wakeword. Os modelos atuais Micro v2 e Open v2 foram verificados pelos pins de `HandsFreeModelBundle.kt`. No áudio limpo, o Micro reconheceu 19/19 e a cascata reconheceu 17/19.

### Redução acústica e preservação de fala

Quatro pares públicos e quatro cenários instrumentais, referência alinhada:

| Método | Redução mediana de eco de fala | Redução mediana de eco instrumental |
|---|---:|---:|
| Saída linear AEC3 sem gate | 3,52 dB | 6,47 dB |
| Supressão residual não linear AEC3 | 29,35 dB | 25,63 dB |
| Saída linear após gate de evidência | 3,15 dB | 7,12 dB |

Apesar da redução maior, a saída não linear atenuou a fala próxima em cerca de 8 dB no cenário instrumental sintético a 0 dB de relação fala/eco. A saída linear com gate manteve ganhos estimados da fala perto de −0,4 a −1,1 dB nos cenários alinhados. O gate exige pelo menos oito observações fortes de redução de potência de 3 dB nos últimos 50 quadros e rejeita saída significativamente mais energética que o microfone. A entrada sem referência útil ou com PCM de referência silencioso foi preservada byte a byte.

Atrasos incorretos importam: com a referência fornecida 80 ms tarde em relação ao par de gravações, a redução mediana do gate caiu para 0,14 dB em fala e 1,68 dB em instrumental. Portanto, a implementação exige timestamps de captura consistentes e não aplica um deslocamento arbitrário para tentar compensar um relógio desconhecido.

### Reconhecimento da cascata atual sobre mídia

Número de chamadas reconhecidas entre os 19 positivos por cenário, em referência idealmente disponível. A relação fala/eco é medida no horizonte anotado da palavra de ativação.

| Áudio de fundo | Relação fala/eco | Microfone original | AEC3 linear com gate |
|---|---:|---:|---:|
| Fala pública reproduzida | 0 dB | 0/19 | 1/19 |
| Fala pública reproduzida | +6 dB | 0/19 | 5/19 |
| Fala pública reproduzida | +12 dB | 0/19 | 11/19 |
| Música instrumental sintética | 0 dB | 9/19 | 8/19 |
| Música instrumental sintética | +6 dB | 15/19 | 11/19 |
| Música instrumental sintética | +12 dB | 17/19 | 15/19 |

A soma passou de 41/114 a 51/114. A melhoria está concentrada em fala reproduzida, que era a condição mais adversa. A redução de sensibilidade sobre instrumental é uma limitação do candidato. Não há garantia de reconhecimento invariável com mídia em volumes elevados.

Quando dois dos dez quadros de cada leitura de 200 ms estavam sem referência porque o outro gravador ainda não havia entregue o áudio, manter o AEC3 em execução com referência silenciosa nesses quadros preservou o estado adaptativo, mas degradou a cascata para 35/114 (fala pública 0/57 e instrumental 35/57). A disponibilidade temporal da referência é importante para o uso real. O gate final mantém uma janela de 50 quadros e desativa o tratamento enquanto mais de 10% deles estão indisponíveis, liberando o AEC3 até a cobertura se recuperar. A implementação também aguarda a chegada da referência por até 45 ms quando a leitura está ligeiramente adiantada, sem deslocar artificialmente os timestamps. Com o gate de cobertura, este teste de perda persistente retornou exatamente às mesmas métricas do microfone original (41/114), em todos os níveis de relação fala/eco. No replay ideal, as métricas permaneceram idênticas às da tabela. Em três replays instrumentais determinísticos, o PCM tratado ideal permaneceu byte a byte igual ao gate anterior, e, sob perda persistente, o PCM retornado foi byte a byte igual ao microfone original.

### Mídia que contém a própria palavra de ativação

Oito controles locais reproduziram um dos positivos como áudio de mídia. A cascata aceitou 6/8 no microfone original e 4/8 com o tratamento linear. Um segundo controle sobrepôs uma chamada real independente à chamada da mídia. A cascata tratada reconheceu 7/8; a cascata executada apenas na referência também reconheceu a palavra em seis desses sete casos.

Um veto baseado somente na palavra detectada na referência eliminaria os quatro falsos aceites desse controle, mas invalidaria seis das sete chamadas reais coincidentes. A bancada não sustenta esse veto. A política de captura continua a exigir o wakeword completo e o cancelamento da mídia após a ativação.

### Custo e escopo

O custo de CPU medido para o caminho linear com o gate final foi aproximadamente 17,03 ms por segundo de áudio no host x86-64, enquanto a referência estava continuamente disponível, e 0,57 ms por segundo com perda persistente. É uma estimativa de custo do processamento JNI/Kotlin em bancada; não é uma extrapolação de bateria do S20 FE. A opção de mídia requer a autorização do usuário para `AudioPlaybackCapture` do Android e depende das políticas dos aplicativos reprodutores. A pausa por foco de áudio é uma etapa independente, executada durante a sessão de conversa. A qualidade acústica, a disponibilidade de timestamps, o comportamento de pausa em aplicativos reais e o custo energético exigem validação física.

## Prova física das APIs no S20 FE

Dois testes controlados, em Android 13 (SDK 33, SM-G781B), reproduziram áudio sintético com `USAGE_MEDIA` de outro UID. O aplicativo de prova recebeu 48.000 amostras a 16 kHz de cada gravador, com timestamps válidos e sem silenciamento de captura declarado pelo Android.

| Captura | RMS microfone, teste 1 | RMS microfone, teste 2 |
|---|---:|---:|
| MIC, efeito desligado | 5.339,90 | 5.652,35 |
| MIC, efeito ligado | 5.860,19 | 6.045,35 |
| Comunicação, efeito desligado | 4.628,31 | 4.336,25 |
| Comunicação, efeito ligado | 0,00 | 0,00 |

O nível RMS da referência nos testes MIC ficou entre 2.341,98 e 2.419,52. O efeito nativo ligado em MIC não reduziu o áudio de mídia neste aparelho. O modo de comunicação mudou a rota e houve supressão completa no controle de eco; não foi medida fala independente neste modo, o que impede uma conclusão sobre preservação do usuário. O modo de comunicação permanente também não se adequa à escuta contínua de baixo impacto.

A solicitação transitória de foco produziu pausa da fonte e retomada após liberação, nas duas execuções. Os valores RMS da referência antes/durante/depois foram 1.054,59/0,00/2.703,84 e 849,42/0,00/2.659,41. A aplicação de mídia do teste registrou `LOSS_TRANSIENT` e `GAIN`. Na segunda execução, o aparelho estava com tela apagada e bloqueado; a captura de referência continuou. Isso confirma as APIs nesta configuração de teste, sem homologar o pipeline acústico completo da versão distribuída ou os aplicativos de mídia comerciais.

## Reprodução

- Compile `NativeHandsFreeEcho.kt`, `NodeHandsFreeExternalMediaProcessor.kt` e `scripts/hands-free/ExternalMediaBatch.kt` com o Kotlin/JVM e a biblioteca JNI Linux da Aegis. O runner usa o mesmo protocolo de entrada descrito em [scripts/barge-in-dtd/README.md](../../scripts/barge-in-dtd/README.md), acrescentando saída tratada pelo gate e tempo de CPU. O argumento opcional do runner simula de zero a dois quadros faltantes no fim de cada leitura de 200 ms.
- `scripts/hands-free/benchmark-external-media.py` mede o replay público/sintético e o detector primário. Requer `--reference-dir`, `--batch-argv`, `--detector-lib`, `--model`, `--output`; os argumentos `--sources`, `--offsets` e `--snr` selecionam a matriz de avaliação.
- `scripts/hands-free/benchmark-external-media-heldout.py` mede a cascata, com a referência ONNX já usada pelos testes do wakeword. Requer `--heldout` (ZIP privado contendo 19 WAVs e `index.json` com os nomes neutros, amostras de decisão e SHA-256 do PCM), `--bundle` (bundle privado atual), `--reference-dir`, `--batch-argv`, `--detector-lib`, `--output` e `--gaps`. Os dados reais e pesos privados não são versionados.
- Os relatórios registram somente métricas agregadas e hashes de modelo, sem arquivos de áudio, nomes de gravações privadas, transcrições ou escores por gravação.
