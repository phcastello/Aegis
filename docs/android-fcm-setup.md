# Android FCM — configuração operacional da Aegis

Nenhum projeto Firebase existia no início do Stage 06. Background push real depende desta configuração; mocks/compilação não constituem aceite físico.

1. Abra https://console.firebase.google.com/ e crie/selecione um projeto. Analytics é opcional e não é necessário. Registre o **project ID**.
2. Adicione um app **Android** com package **com.aegis.node**. Não altere esse package nem o certificado já usado para assinar a Aegis. Baixe `google-services.json`.
3. Coloque o arquivo client-side em `apps/aegis-node/src-tauri/gen/android/app/google-services.json` (ignorado pelo Git). Ele identifica projeto/app e não concede autoridade para enviar FCM; ainda assim não o publique nos logs. A integração Gradle processa esse arquivo quando presente; sem ele, FCM fica explicitamente não configurado.
4. Em Project Settings → Cloud Messaging, habilite Firebase Cloud Messaging API (HTTP v1), se ainda necessário. Em Google Cloud IAM, crie uma service account com autoridade de envio FCM limitada ao projeto (`roles/firebasecloudmessaging.admin`). Gere uma chave JSON e armazene-a fora do repositório, por exemplo `~/.config/aegis/firebase/service-account.json`, diretório700/arquivo600. Alternativamente use a credencial de service account gerenciada apropriada; nunca envie chaves pela conversa.
5. Backend Compose: monte o diretório privado via `AEGIS_FCM_CREDENTIALS_DIR`; configure `AEGIS_FCM_PROJECT_ID` e `AEGIS_FCM_SERVICE_ACCOUNT_FILE=/run/aegis-fcm/service-account.json`. O mount é read-only. Esses valores vão no ambiente operacional privado, não em arquivos versionados com secrets. O backend precisa do JSON privado; Android e WebView nunca recebem esse arquivo.
6. GitHub Actions: configure secret **FIREBASE_ANDROID_CONFIG_B64** com base64 do `google-services.json`. Somente configuração Android client-side vai nesse secret. A chave privada de service account fica exclusivamente no backend, não em GitHub/release. Os builds Android local/CI compilam sem configuração, mas nenhum token/BackgroundReachable real existe nesse caso.
7. Rebuild/deploy backend e publique o próximo preview pelo pipeline existente com o secret Android configurado. Instale/update normalmente sem limpar dados ou fazer pairing novamente. Autorize notifications pela UI nativa; abra o app para inicializar Firebase e sincronizar o token com a identidade Node existente.
8. Teste foreground pelo botão de diagnóstico: live_websocket e resultado conhecido. Depois background/tela apagada, espere o socket deixar de estar Online e confirme BackgroundReachable. Envie teste: fcm/accepted significa Firebase aceitou, não comprova display. Observe fisicamente a notificação. Toque para abrir e confirme Online novamente.
9. Teste swipe dos recentes separadamente, considerando comportamento do fabricante. **Force Stop não é suportado como garantia de delivery**; abra o app novamente após Force Stop.
10. Rotação backend: crie nova chave da service account, substitua arquivo privado/mount, reinicie API, teste envio e revogue a chave anterior. Rotação de token Android é automática; não exige pairing. Token inválido reportado pelo FCM invalida apenas a registration correspondente.

Não commitar/logar: service account, private_key, access token OAuth, Node credential, FCM token, pairing code. Nunca enviar esses dados à WebView. Não usar credentials dos Nodes físicos para testes automatizados.

Referências oficiais: [Android receive](https://firebase.google.com/docs/cloud-messaging/android/receive-messages), [HTTP v1](https://firebase.google.com/docs/cloud-messaging/send/v1-api), [Android priority](https://firebase.google.com/docs/cloud-messaging/android-message-priority).


## Diagnóstico IAM HTTP v1

Se OAuth funcionar, mas `messages:send` responder HTTP 403 com
`IAM_PERMISSION_DENIED` / `cloudmessaging.messages.create`, abra Google Cloud
Console → IAM no projeto de destino. Localize o principal `client_email` do JSON
privado e conceda **Firebase Cloud Messaging API Admin**
(`roles/firebasecloudmessaging.admin`). Essa permissão deve existir no projeto
de destino; não basta haver uma chave de service account válida. Repita o probe
após propagação do IAM.

`validate_only: true` permite provar autenticação, autorização e schema pela API
real sem entregar mensagem. PASS desse dry-run não prova token Android, receiver,
permission ou display: esses casos exigem o app instalado e registration real.

Referências: [papel IAM FCM](https://docs.cloud.google.com/iam/docs/roles-permissions/firebasecloudmessaging),
[validate_only HTTP v1](https://firebase.google.com/docs/reference/fcm/rest/v1/projects.messages/send).
