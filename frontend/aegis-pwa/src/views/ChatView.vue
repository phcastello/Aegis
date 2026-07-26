<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref } from 'vue';
import AegisMark from '../components/AegisMark.vue';
import ChatMessage from '../components/ChatMessage.vue';
import ConversationSidebar from '../components/ConversationSidebar.vue';
import FeedbackDialog from '../components/FeedbackDialog.vue';
import {
  deleteConversation,
  cancelTurn,
  getConversation,
  getConversations,
  renameConversation,
  sendMessageStream,
  submitMessageFeedback
} from '../services/aegisApi';
import { useAegisVoice } from '../composables/useAegisVoice';
import type {
  ConversationSummary,
  FeedbackRating,
  LocalChatMessage,
  SubmitMessageFeedbackRequest
} from '../types/chat';

const STORAGE_KEY = 'aegis.currentConversationId';

const messages = ref<LocalChatMessage[]>([]);
const conversationId = ref<string | null>(localStorage.getItem(STORAGE_KEY));
const draft = ref('');
const isLoading = ref(false);
const isRestoring = ref(false);
const errorMessage = ref<string | null>(null);
const messagesEnd = ref<HTMLElement | null>(null);
const composerInput = ref<HTMLTextAreaElement | null>(null);
const isComposerScrollable = ref(false);
const feedbackTarget = ref<{ message: LocalChatMessage; rating: FeedbackRating } | null>(null);
const feedbackStatusByMessageId = ref<Record<string, string>>({});
const feedbackErrorMessage = ref<string | null>(null);
const isSavingFeedback = ref(false);
const isSidebarOpen = ref(false);
const activeConversationTitle = ref<string | null>(null);
const conversations = ref<ConversationSummary[]>([]);
const nextHistoryCursor = ref<string | null>(null);
const hasMoreHistory = ref(false);
const isLoadingHistory = ref(false);
const historyErrorMessage = ref<string | null>(null);
const deleteTarget = ref<ConversationSummary | null>(null);
const isDeletingConversation = ref(false);
const activeTurnId = ref<string | null>(null);
const turnStatus = ref<'thinking' | 'responding' | 'preparing_voice' | 'speaking' | 'interrupted' | 'voice_unavailable' | 'idle'>('idle');
let chatAbortController: AbortController | null = null;
const voice = useAegisVoice();
let streamScrollFrame: number | null = null;
const historyRefreshTimers: number[] = [];
let viewportCleanup: (() => void) | null = null;

const canSend = computed(() => draft.value.trim().length > 0 && !isRestoring.value);
const hasActiveTurn = computed(() => activeTurnId.value !== null || isLoading.value || voice.isBusy.value);
const conversationLabel = computed(() => {
  const title = activeConversationTitle.value?.trim() || 'Nova conversa';
  return title.length > 48 ? `${title.slice(0, 48).trimEnd()}...` : title;
});
const COMPOSER_MAX_HEIGHT = 168;

function scrollToLatest(smooth = true): void {
  nextTick(() => {
    messagesEnd.value?.scrollIntoView({ behavior: smooth ? 'smooth' : 'auto', block: 'end' });
  });
}

function scrollToLatestDuringStream(): void {
  if (streamScrollFrame !== null) {
    return;
  }

  streamScrollFrame = requestAnimationFrame(() => {
    streamScrollFrame = null;
    scrollToLatest(false);
  });
}

function resizeComposer(): void {
  nextTick(() => {
    const input = composerInput.value;
    if (!input) {
      return;
    }

    input.style.height = 'auto';
    isComposerScrollable.value = input.scrollHeight > COMPOSER_MAX_HEIGHT;
    input.style.height = `${Math.min(input.scrollHeight, COMPOSER_MAX_HEIGHT)}px`;
  });
}

function focusComposer(): void {
  if (isRestoring.value) {
    return;
  }

  void nextTick(() => {
    composerInput.value?.focus({ preventScroll: true });
  });
}

function syncViewportHeight(): void {
  const viewport = window.visualViewport;
  const height = viewport?.height ?? window.innerHeight;

  document.documentElement.style.setProperty('--app-viewport-height', `${height}px`);
}

function normalizeEmailConnectError(rawCode: string | null, rawMessage: string | null): string | null {
  if (!rawCode && !rawMessage) {
    return null;
  }

  const message = rawMessage?.trim();
  switch (rawCode) {
    case 'oauth_callback_error':
      return message
        ? `Falha ao concluir a conexão com o Gmail: ${message}`
        : 'Falha ao concluir a conexão com o Gmail.';
    case 'google_http_error':
      return message
        ? `O Google rejeitou a conexão com o Gmail: ${message}`
        : 'O Google rejeitou a conexão com o Gmail.';
    case 'oauth_invalid_operation':
    case 'oauth_invalid_argument':
      return message
        ? `A configuração ou o estado da conexão Gmail está inválido: ${message}`
        : 'A configuração ou o estado da conexão Gmail está inválido.';
    case 'oauth_unknown_error':
      return message
        ? `Erro inesperado ao conectar o Gmail: ${message}`
        : 'Erro inesperado ao conectar o Gmail.';
    default:
      return message
        ? `Falha ao conectar o Gmail: ${message}`
        : 'Falha ao conectar o Gmail.';
  }
}

function consumeEmailConnectStatusFromUrl(): void {
  const url = new URL(window.location.href);
  const emailStatus = url.searchParams.get('email');
  const errorCode = url.searchParams.get('email_error_code');
  const errorMessageParam = url.searchParams.get('email_error_message');

  if (emailStatus === 'connected') {
    errorMessage.value = null;
  } else {
    const normalizedError = normalizeEmailConnectError(errorCode, errorMessageParam);
    if (normalizedError) {
      errorMessage.value = normalizedError;
    }
  }

  if (!emailStatus && !errorCode && !errorMessageParam) {
    return;
  }

  url.searchParams.delete('email');
  url.searchParams.delete('email_error_code');
  url.searchParams.delete('email_error_message');
  window.history.replaceState({}, document.title, `${url.pathname}${url.search}${url.hash}`);
}

function createLocalMessageId(): string {
  try {
    const randomUUID = globalThis.crypto?.randomUUID?.bind(globalThis.crypto);
    if (randomUUID) {
      return `local-${randomUUID()}`;
    }
  } catch {
    // The fallback below is enough for a temporary UI key.
  }

  return `local-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

function createLocalUserMessage(content: string): LocalChatMessage {
  return reactive({
    id: createLocalMessageId(),
    conversationId: conversationId.value ?? 'pending',
    role: 'user',
    content,
    createdAt: new Date().toISOString(),
    model: null,
    pending: true
  });
}

function createLocalAssistantMessage(): LocalChatMessage {
  return reactive({
    id: createLocalMessageId(),
    conversationId: conversationId.value ?? 'pending',
    role: 'assistant',
    content: '',
    createdAt: new Date().toISOString(),
    model: null,
    pending: true,
    streaming: true
  });
}

function getCompletedWordPrefix(content: string): string {
  let completedLength = 0;

  for (const match of content.matchAll(/\s+/gu)) {
    completedLength = (match.index ?? 0) + match[0].length;
  }

  return content.slice(0, completedLength);
}

async function restoreConversation(): Promise<void> {
  if (!conversationId.value) {
    return;
  }

  isRestoring.value = true;
  errorMessage.value = null;

  try {
    const conversation = await getConversation(conversationId.value);
    activeConversationTitle.value = conversation.title ?? 'Nova conversa';
    messages.value = conversation.messages.map((message) => ({
      ...message,
      serverId: message.id
    }));
    scrollToLatest();
    focusComposer();
  } catch {
    localStorage.removeItem(STORAGE_KEY);
    conversationId.value = null;
    activeConversationTitle.value = null;
    errorMessage.value = 'Não foi possível carregar a conversa anterior.';
  } finally {
    isRestoring.value = false;
  }
}

function mergeConversationSummary(summary: ConversationSummary): void {
  const withoutCurrent = conversations.value.filter((conversation) => conversation.id !== summary.id);
  conversations.value = [summary, ...withoutCurrent].sort((first, second) => {
    const dateDifference = new Date(second.updatedAt).getTime() - new Date(first.updatedAt).getTime();
    return dateDifference || second.id.localeCompare(first.id);
  });
}

async function loadConversationHistory(reset = false): Promise<void> {
  if (isLoadingHistory.value) {
    return;
  }

  if (!reset && !hasMoreHistory.value) {
    return;
  }

  isLoadingHistory.value = true;
  historyErrorMessage.value = null;

  try {
    const page = await getConversations(30, reset ? null : nextHistoryCursor.value);
    const incoming = reset ? page.items : [...conversations.value, ...page.items];
    const seen = new Set<string>();
    conversations.value = incoming.filter((conversation) => {
      if (seen.has(conversation.id)) {
        return false;
      }

      seen.add(conversation.id);
      return true;
    });
    nextHistoryCursor.value = page.nextCursor ?? null;
    hasMoreHistory.value = page.hasMore;

    const active = conversations.value.find((conversation) => conversation.id === conversationId.value);
    if (active) {
      activeConversationTitle.value = active.title ?? 'Nova conversa';
    }
  } catch {
    historyErrorMessage.value = 'Não foi possível carregar o histórico.';
  } finally {
    isLoadingHistory.value = false;
  }
}

function refreshHistoryAfterResponse(): void {
  void loadConversationHistory(true);

  for (const delay of [1800, 5200, 16000, 22000]) {
    historyRefreshTimers.push(
      window.setTimeout(() => {
        void loadConversationHistory(true);
      }, delay)
    );
  }
}

async function openConversation(targetConversationId: string): Promise<void> {
  if (isRestoring.value || targetConversationId === conversationId.value) {
    isSidebarOpen.value = false;
    return;
  }

  await stopActiveTurn('conversation_changed');

  isRestoring.value = true;
  errorMessage.value = null;
  feedbackTarget.value = null;
  feedbackErrorMessage.value = null;
  feedbackStatusByMessageId.value = {};

  try {
    const conversation = await getConversation(targetConversationId);
    conversationId.value = conversation.id;
    activeConversationTitle.value = conversation.title ?? 'Nova conversa';
    localStorage.setItem(STORAGE_KEY, conversation.id);
    messages.value = conversation.messages.map((message) => ({
      ...message,
      serverId: message.id
    }));
    isSidebarOpen.value = false;
    scrollToLatest(false);
    focusComposer();
  } catch {
    errorMessage.value = 'Não foi possível abrir esta conversa.';
    await loadConversationHistory(true);
  } finally {
    isRestoring.value = false;
  }
}

async function handleSubmit(): Promise<void> {
  if (!canSend.value) {
    return;
  }

  await voice.prepare();
  await stopActiveTurn('superseded_by_new_message');
  const content = draft.value.trim();
  const turnId = crypto.randomUUID();
  const localMessage = createLocalUserMessage(content);
  const assistantMessage = createLocalAssistantMessage();

  draft.value = '';
  resizeComposer();
  errorMessage.value = null;
  isLoading.value = true;
  turnStatus.value = 'thinking';
  activeTurnId.value = turnId;
  chatAbortController = new AbortController();
  messages.value.push(localMessage, assistantMessage);
  scrollToLatest();

  let streamedContent = '';

  try {
    await sendMessageStream(
      {
        turnId,
        conversationId: conversationId.value,
        content
      },
      {
        onConversation: (eventTurnId, streamConversationId) => {
          if (eventTurnId !== activeTurnId.value) return;
          conversationId.value = streamConversationId;
          localStorage.setItem(STORAGE_KEY, streamConversationId);
          localMessage.conversationId = streamConversationId;
          assistantMessage.conversationId = streamConversationId;
          localMessage.pending = false;
        },
        onToken: (eventTurnId, token) => {
          if (eventTurnId !== activeTurnId.value) return;
          streamedContent += token;
          turnStatus.value = 'responding';
          assistantMessage.content = getCompletedWordPrefix(streamedContent);
          scrollToLatestDuringStream();
        },
        onDone: ({ turnId: completedTurnId, conversationId: completedConversationId, messageId, conversationTitle }) => {
          if (completedTurnId !== activeTurnId.value) return;
          conversationId.value = completedConversationId;
          localStorage.setItem(STORAGE_KEY, completedConversationId);
          activeConversationTitle.value = conversationTitle ?? activeConversationTitle.value ?? 'Nova conversa';
          localMessage.conversationId = completedConversationId;
          localMessage.pending = false;
          assistantMessage.conversationId = completedConversationId;
          assistantMessage.serverId = messageId;
          assistantMessage.content = streamedContent;
          assistantMessage.pending = false;
          isLoading.value = false;
          scrollToLatest();

          window.setTimeout(() => {
            assistantMessage.streaming = false;
          }, 600);

          refreshHistoryAfterResponse();
          if (voice.autoSpeak.value) {
            turnStatus.value = 'preparing_voice';
            void voice.speak(completedTurnId, messageId).finally(() => {
              if (activeTurnId.value === completedTurnId) {
                activeTurnId.value = null;
                turnStatus.value = voice.voiceAvailable.value ? 'idle' : 'voice_unavailable';
              }
            });
          } else {
            activeTurnId.value = null;
            turnStatus.value = 'idle';
          }
        },
        onError: () => {
          if (activeTurnId.value !== turnId) return;
          errorMessage.value = 'A resposta da Aegis foi interrompida. Tente continuar em um instante.';
        }
      },
      chatAbortController.signal
    );
  } catch (error) {
    if (activeTurnId.value !== turnId) return;
    if ((error as DOMException).name === 'AbortError') {
      turnStatus.value = 'interrupted';
    } else {
      errorMessage.value = 'Não foi possível enviar a mensagem. Tente novamente em um instante.';
    }
    localMessage.pending = false;
    assistantMessage.pending = false;
    assistantMessage.streaming = false;
    if (!assistantMessage.content) {
      messages.value = messages.value.filter((message) => message.id !== assistantMessage.id);
    }
  } finally {
    if (activeTurnId.value === turnId) isLoading.value = false;
  }
}

async function stopActiveTurn(reason = 'user_stop'): Promise<void> {
  const turnId = activeTurnId.value;
  activeTurnId.value = null;
  chatAbortController?.abort();
  chatAbortController = null;
  void voice.stop();
  isLoading.value = false;
  if (turnId) {
    turnStatus.value = 'interrupted';
    void cancelTurn(turnId).catch(() => undefined);
  }
  void reason;
}

function toggleAutoSpeak(): void {
  voice.setAutoSpeak(!voice.autoSpeak.value);
}

function replayMessage(message: LocalChatMessage): void {
  if (!message.serverId) return;
  void (async () => {
    await stopActiveTurn('replay');
    const turnId = crypto.randomUUID();
    activeTurnId.value = turnId;
    turnStatus.value = 'preparing_voice';
    await voice.speak(turnId, message.serverId!, true);
    if (activeTurnId.value === turnId) {
      activeTurnId.value = null;
      turnStatus.value = voice.voiceAvailable.value ? 'idle' : 'voice_unavailable';
    }
  })();
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Enter' && !event.shiftKey && canSend.value) {
    event.preventDefault();
    void handleSubmit();
  }
}

function startNewConversation(): void {
  void stopActiveTurn('new_conversation');
  localStorage.removeItem(STORAGE_KEY);
  conversationId.value = null;
  activeConversationTitle.value = null;
  messages.value = [];
  draft.value = '';
  resizeComposer();
  errorMessage.value = null;
  feedbackTarget.value = null;
  feedbackErrorMessage.value = null;
  feedbackStatusByMessageId.value = {};
  isSidebarOpen.value = false;
  focusComposer();
}

async function handleRenameConversation(targetConversationId: string, title: string): Promise<void> {
  try {
    const summary = await renameConversation(targetConversationId, title);
    mergeConversationSummary(summary);
    if (conversationId.value === targetConversationId) {
      activeConversationTitle.value = summary.title ?? 'Nova conversa';
    }
  } catch {
    errorMessage.value = 'Não foi possível renomear a conversa.';
  }
}

function requestDeleteConversation(conversation: ConversationSummary): void {
  deleteTarget.value = conversation;
}

function cancelDeleteConversation(): void {
  if (isDeletingConversation.value) {
    return;
  }

  deleteTarget.value = null;
}

async function confirmDeleteConversation(): Promise<void> {
  if (!deleteTarget.value || isDeletingConversation.value) {
    return;
  }

  const targetId = deleteTarget.value.id;
  isDeletingConversation.value = true;

  try {
    await deleteConversation(targetId);
    conversations.value = conversations.value.filter((conversation) => conversation.id !== targetId);
  if (conversationId.value === targetId) {
      await stopActiveTurn('conversation_deleted');
      startNewConversation();
    }

    deleteTarget.value = null;
    await loadConversationHistory(true);
  } catch {
    errorMessage.value = 'Não foi possível apagar a conversa.';
  } finally {
    isDeletingConversation.value = false;
  }
}

function openFeedback(message: LocalChatMessage, rating: FeedbackRating): void {
  feedbackTarget.value = { message, rating };
  feedbackErrorMessage.value = null;
}

function closeFeedback(): void {
  if (isSavingFeedback.value) {
    return;
  }

  feedbackTarget.value = null;
  feedbackErrorMessage.value = null;
}

async function handleFeedbackSubmit(request: SubmitMessageFeedbackRequest): Promise<void> {
  if (!feedbackTarget.value) {
    return;
  }

  isSavingFeedback.value = true;
  feedbackErrorMessage.value = null;

  try {
    const messageId = feedbackTarget.value.message.serverId;
    if (!messageId) {
      throw new Error('A resposta ainda não está pronta para receber feedback.');
    }

    await submitMessageFeedback(messageId, request);
    feedbackStatusByMessageId.value = {
      ...feedbackStatusByMessageId.value,
      [messageId]: 'Feedback salvo'
    };
    feedbackTarget.value = null;
  } catch {
    feedbackErrorMessage.value = 'Não foi possível salvar o feedback.';
  } finally {
    isSavingFeedback.value = false;
  }
}

onMounted(() => {
  syncViewportHeight();
  consumeEmailConnectStatusFromUrl();
  const handleViewportChange = (): void => {
    syncViewportHeight();
  };

  window.addEventListener('resize', handleViewportChange);
  if (window.visualViewport) {
    window.visualViewport.addEventListener('resize', handleViewportChange);
    window.visualViewport.addEventListener('scroll', handleViewportChange);
  }
  viewportCleanup = () => {
    window.removeEventListener('resize', handleViewportChange);
    window.visualViewport?.removeEventListener('resize', handleViewportChange);
    window.visualViewport?.removeEventListener('scroll', handleViewportChange);
  };

  void loadConversationHistory(true);
  void restoreConversation();
  void voice.refreshStatus();
  focusComposer();
});

onBeforeUnmount(() => {
  void stopActiveTurn('view_unmounted');
  for (const timer of historyRefreshTimers) {
    window.clearTimeout(timer);
  }

  viewportCleanup?.();
});
</script>

<template>
  <main class="app-shell">
    <div class="hideout-shell">
      <ConversationSidebar
        :conversations="conversations"
        :active-conversation-id="conversationId"
        :disabled="isLoading || isRestoring"
        :open="isSidebarOpen"
        :has-more="hasMoreHistory"
        :is-loading-more="isLoadingHistory"
        :history-error="historyErrorMessage"
        @close="isSidebarOpen = false"
        @new-conversation="startNewConversation"
        @open-conversation="openConversation"
        @rename-conversation="handleRenameConversation"
        @request-delete-conversation="requestDeleteConversation"
        @load-more="loadConversationHistory(false)"
        @retry-history="loadConversationHistory(true)"
      />

      <section class="chat-panel" aria-label="Conversa com a Aegis">
      <header class="chat-header">
        <button type="button" class="sidebar-toggle" aria-label="Abrir histórico" @click="isSidebarOpen = true">
          <svg viewBox="0 0 20 20" aria-hidden="true">
            <path d="M4 5h12M4 10h12M4 15h12" />
          </svg>
        </button>

        <div class="conversation-heading">
          <span>{{ conversationId ? 'Conversa ativa' : 'Novo pensamento' }}</span>
          <div>
            <h1>{{ conversationLabel }}</h1>
            <p>{{ turnStatus === 'thinking' ? 'Aegis está pensando' : turnStatus === 'responding' ? 'Aegis está respondendo' : turnStatus === 'preparing_voice' ? 'Preparando voz' : voice.playbackState.value === 'playing' ? 'Aegis está falando' : turnStatus === 'interrupted' ? 'Interrompida' : !voice.voiceAvailable.value ? 'Voz indisponível' : 'Um espaço reservado para continuar.' }}</p>
          </div>
        </div>

        <div class="chat-voice-controls">
          <button
            type="button"
            class="voice-toggle"
            :aria-label="voice.autoSpeak.value ? 'Fala automática ligada' : 'Fala automática desligada'"
            :title="voice.autoSpeak.value ? 'Fala automática ligada' : 'Fala automática desligada'"
            @click="toggleAutoSpeak"
          >{{ voice.autoSpeak.value ? '🔊' : '🔇' }}</button>
          <button v-if="hasActiveTurn" type="button" class="stop-turn-button" @click="stopActiveTurn()">Parar</button>
        </div>

        <span class="presence-indicator" :class="{ 'presence-indicator--active': hasActiveTurn }">
          <i></i>
          {{ hasActiveTurn ? 'ativa' : 'presente' }}
        </span>
      </header>

      <div class="messages" aria-live="polite">
        <div v-if="isRestoring" class="empty-state">
          <AegisMark />
          <span>Carregando conversa...</span>
        </div>

        <div v-else-if="messages.length === 0" class="empty-state">
          <div class="empty-state__mark"><AegisMark /></div>
          <span class="empty-state__eyebrow">Aegis está presente</span>
          <strong>O que merece espaço agora?</strong>
          <span>Comece uma conversa ou continue um raciocínio em voz alta.</span>
        </div>

        <template v-else>
          <ChatMessage
            v-for="message in messages"
            :key="message.id"
            :message="message"
            :feedback-status="feedbackStatusByMessageId[message.serverId ?? message.id]"
            @feedback="openFeedback"
            @replay="replayMessage"
          />
        </template>

        <div ref="messagesEnd" class="messages-end"></div>
      </div>

      <p v-if="errorMessage || voice.voiceMessage" class="error-message" role="alert">{{ errorMessage ?? voice.voiceMessage }}</p>

      <form class="composer" @submit.prevent="handleSubmit">
        <div class="composer-field">
          <textarea
            v-model="draft"
            rows="1"
            placeholder="Escreva para a Aegis..."
            aria-label="Mensagem"
            :class="{ 'composer-input--scrollable': isComposerScrollable }"
            ref="composerInput"
            @input="resizeComposer"
            @keydown="handleKeydown"
          ></textarea>

          <button class="send-button" type="submit" :disabled="!canSend || isRestoring" aria-label="Enviar">
            <span>Enviar</span>
            <svg viewBox="0 0 20 20" aria-hidden="true" focusable="false">
              <path d="M3 10h12.2M10.8 5.6 15.2 10l-4.4 4.4" />
            </svg>
          </button>
        </div>
        <span class="composer-hint">Enter para enviar · Shift + Enter para nova linha</span>
      </form>
      </section>
    </div>

    <FeedbackDialog
      v-if="feedbackTarget"
      :message="feedbackTarget.message"
      :rating="feedbackTarget.rating"
      :is-saving="isSavingFeedback"
      :error-message="feedbackErrorMessage"
      @cancel="closeFeedback"
      @submit="handleFeedbackSubmit"
    />

    <div v-if="deleteTarget" class="confirm-overlay" role="dialog" aria-modal="true" aria-label="Apagar conversa">
      <div class="confirm-dialog">
        <p>Apagar esta conversa?</p>
        <span>Ela sairá do histórico e não será usada para continuar raciocínios futuros.</span>
        <div class="confirm-dialog__actions">
          <button type="button" class="ghost-button" :disabled="isDeletingConversation" @click="cancelDeleteConversation">
            Cancelar
          </button>
          <button
            type="button"
            class="delete-confirm-button"
            :disabled="isDeletingConversation"
            @click="confirmDeleteConversation"
          >
            Apagar
          </button>
        </div>
      </div>
    </div>
  </main>
</template>
