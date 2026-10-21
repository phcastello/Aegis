# Aegis — It's Not Stalking. It's a Feature.

*You're terrible at hiding.*

Aegis é uma assistente pessoal desenvolvida para uso próprio, com foco em privacidade, controle e uma personalidade própria. Reúne conversa por texto e voz, memória, integrações com Google, lembretes e ações nos dispositivos pareados.

O backend e os dados são administrados pelo proprietário. Os modelos de linguagem são acessados por API; a Aegis não é uma IA inteiramente local nem um serviço público de cadastro.

**Versão atual: v0.7.1.** [Notas do lançamento](https://github.com/phcastello/Aegis/releases/tag/v0.7.1)

## O que ela faz

- **Conversa:** respostas em streaming, histórico, transcrição de voz e reprodução por TTS.
- **Memória e integrações:** recuperação de informações pessoais e operações conversacionais com Gmail e Google Calendar.
- **Lembretes:** avisos únicos ou recorrentes, reconhecimento e adiamento, com entrega aos dispositivos elegíveis.
- **Lugares:** lembretes de chegada e saída no Android, considerando os locais cadastrados e o celular escolhido como fonte de localização.

Cada instalação nativa pareada é um **Node**: um dispositivo com identidade, permissões, capacidades e disponibilidade próprias.

## Plataformas

| Cliente | Disponível hoje |
| --- | --- |
| Android ARM64 | Chat, notificações nativas, lembretes e monitoramento geográfico em segundo plano. |
| Windows x86_64 | Chat, notificações nativas, gerenciamento dos dispositivos e execução na bandeja. |
| Web/PWA | Chat, histórico e integrações. O recebimento atual de lembretes usa os aplicativos nativos. |

Não há distribuição nativa para Linux, macOS ou iOS nesta versão.

## Baixar e atualizar

| Plataforma | Instalador oficial |
| --- | --- |
| Android | [Aegis-Android-arm64.apk](https://github.com/phcastello/Aegis/releases/download/node-v0.7.1/Aegis-Android-arm64.apk) |
| Windows | [Aegis-Windows-x86_64-Setup.exe](https://github.com/phcastello/Aegis/releases/download/node-v0.7.1/Aegis-Windows-x86_64-Setup.exe) |

As assinaturas e os hashes estão na [distribuição nativa](https://github.com/phcastello/Aegis/releases/tag/node-v0.7.1). Os instaladores são configurados para o backend pessoal do projeto; baixá-los não cria uma instância independente.

**Atualize por Configurações → Atualizações ou instale sobre a versão existente. Não desinstale para atualizar.** O Android solicita confirmação para instalar o APK. No Windows, o SmartScreen pode indicar editor desconhecido: a assinatura do updater é independente de um certificado comercial de editor.

## Primeiro uso

O chat pode ser usado sem pareamento. Para receber lembretes nativos, abra **Configurações → Dispositivos** e pareie a instalação com um código temporário gerado por outro Node ou pelo administrador do backend.

Para lembretes geográficos, selecione o celular que fornece sua localização, conceda as permissões de localização precisa e em segundo plano e cadastre o lugar no mapa. Depois, crie a regra pela interface ou pelo chat:

> Quando eu sair da faculdade, me lembre de comprar leite.

A área real representa o lugar. Uma área de proximidade, aprovada separadamente, pode iniciar verificações temporárias: entrar nela não equivale a chegar ao destino. Uma regra salva precisa estar sincronizada com o celular para funcionar localmente.

## Arquitetura

| Diretório | Responsabilidade |
| --- | --- |
| `backend/` | API e serviços .NET: conversa, integrações, memória, lembretes e gerenciamento de Nodes. |
| `apps/aegis-node/` | Aplicativo Tauri 2 para Android e Windows, com Rust e integração nativa Android. |
| `packages/aegis-client/` | Interface Vue compartilhada entre os clientes. |
| `frontend/aegis-pwa/` | Cliente web/PWA. |
| `docs/` | Documentação técnica e registros dos lançamentos. |

PostgreSQL mantém o estado persistente; Qdrant e Neo4j dão suporte às projeções da memória. O TTS é um serviço separado. A configuração de provedores e credenciais pertence ao backend, não ao frontend.

## Privacidade e limites

O uso de APIs de modelos, transcrição, Google, mapas e notificações envolve serviços externos conforme a configuração e as funções utilizadas. Administrar o backend localmente não significa que todo processamento ocorra no próprio dispositivo.

A localização usa eventos geográficos e aquisições temporárias, não GPS permanentemente ativo. Permissões, precisão e restrições do Android podem afetar a detecção. Com o backend inacessível, regras previamente sincronizadas podem notificar localmente; os requisitos de localização do Android continuam valendo. Ações pendentes são reconciliadas ao recuperar conexão.

Permanecem em investigação a captura de microfone em um computador Windows e a classificação de intenções temporárias na memória. Consulte as [limitações documentadas](docs/v0.7.1-release-notes.md#known-limitations).

## Desenvolvimento e documentação

O projeto é pessoal e usa configuração própria de infraestrutura. Revise [`.env.example`](.env.example), [`docker-compose.yml`](docker-compose.yml) e o [procedimento de operação privada](docs/operacao-privada.md), incluindo migrations, antes de executar uma instância. O [índice técnico](docs/README.md) reúne desenvolvimento, testes e histórico.

- [Cliente nativo: comandos de desenvolvimento e build](apps/aegis-node/README.md)
- [Funcionalidades introduzidas na v0.7.0](docs/v0.7.0-release-notes.md)
- [Correção da v0.7.1](docs/v0.7.1-release-notes.md)
- [Histórico técnico da implementação](docs/v0.7.0-completion.md)

Esta branch contém manutenção ainda não distribuída; os downloads acima continuam sendo os instaladores originais da v0.7.1. Os relatos antigos ficam no [arquivo histórico](docs/arquivo/README-ate-v0.7.1.md).
