<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { observeGoogleConnection } from '../services/googleConnection';
import AegisMark from '../components/AegisMark.vue';
import ChatMessage from '../components/ChatMessage.vue';
import ConversationSidebar from '../components/ConversationSidebar.vue';
import ConversationInfo from '../components/ConversationInfo.vue';
import ConversationMenu from '../components/ConversationMenu.vue';
import RenameConversationDialog from '../components/RenameConversationDialog.vue';
import { clientEnvironment } from '../services/clientEnvironment';
import { initialConversationId } from '../services/conversationSession';
import { dismissKeyboard, navigationLayer } from '../services/navigation';
import FeedbackDialog from '../components/FeedbackDialog.vue';
import {
  deleteConversation,
  cancelTurn,
  completeTurnWithoutSpeech,
  getConversation,
  getProactiveActivity, getProactiveMessages, markProactiveRead,
  getConversations,
  getEmailStatus,
  getHealth,
  getMemoryActivity,
  renameConversation,
  sendMessageStream,
  submitMessageFeedback
} from '../services/aegisApi';
import { useAegisVoice } from '../composables/useAegisVoice';
import { useAegisTranscription } from '../composables/useAegisTranscription';
import { useSidebarGesture } from '../composables/useSidebarGesture';
import { waitForEmailConnection } from '../services/emailConnectionPolling';
import { pollMemoryActivity } from '../services/memoryActivityPolling';
import { emailConnectionFailureMessage, emailConnectionSuccessMessage } from '../services/emailConnectionFeedback';
import type {
  ConversationSummary,
  FeedbackRating,
  LocalChatMessage,
  SubmitMessageFeedbackRequest
} from '../types/chat';

defineProps<{ appVersion?: string }>();
const emit = defineEmits<{ activityChanged: [active: boolean] }>();

const STORAGE_KEY = 'aegis.currentConversationId';

const messages = ref<LocalChatMessage[]>([]);
const conversationId = ref<string | null>(initialConversationId(localStorage, clientEnvironment().processSessionId));
const draft = ref('');
const isLoading = ref(false);
const isRestoring = ref(false);
const errorMessage = ref<string | null>(null);
const toolStatusMessage = ref<string | null>(null);
let toolStatusTimer: number | null = null;
const toolStatusState = ref<'started' | 'completed' | 'failed' | null>(null);
const emailConnectionState = ref<'idle' | 'pending' | 'connected' | 'failed'>('idle');
const emailConnectionMessage = ref<string | null>(null);
let emailConnectionAbortController: AbortController | null = null;
let stopObservingGoogle: (() => void) | null = null;
const messagesContainer = ref<HTMLElement | null>(null);
const messagesEnd = ref<HTMLElement | null>(null);
const composerInput = ref<HTMLTextAreaElement | null>(null);
const isComposerScrollable = ref(false);
const feedbackTarget = ref<{ message: LocalChatMessage; rating: FeedbackRating } | null>(null);
const feedbackStatusByMessageId = ref<Record<string, string>>({});
const feedbackErrorMessage = ref<string | null>(null);
const isSavingFeedback = ref(false);
const sidebarDrawerQuery = window.matchMedia('(max-width: 900px)');
const isSidebarOpen = ref(!sidebarDrawerQuery.matches);
const sidebarGestureSurface = ref<HTMLElement | null>(null);
const sidebarGesture = useSidebarGesture(sidebarGestureSurface, isSidebarOpen, sidebarDrawerQuery);
const activeConversationTitle = ref<string | null>(null);
const activeConversationCreatedAt = ref<string | null>(null);
const conversations = ref<ConversationSummary[]>([]);
const nextHistoryCursor = ref<string | null>(null);
const hasMoreHistory = ref(false);
const isLoadingHistory = ref(false);
const historyErrorMessage = ref<string | null>(null);
const deleteTarget = ref<ConversationSummary | null>(null);
const isDeletingConversation = ref(false);
const infoTarget = ref<ConversationSummary | null>(null);
const renameTarget = ref<ConversationSummary | null>(null);
const renameError = ref<string | null>(null);
const isRenaming = ref(false);
const drafts = new Map<string, string>();
let releaseSidebarLayer: (() => void) | undefined;
let releaseDeleteLayer: (() => void) | undefined;
const currentConversationSummary = computed<ConversationSummary>(() => conversations.value.find(c => c.id === conversationId.value) ?? {
  id: conversationId.value ?? '', title: activeConversationTitle.value, createdAt: activeConversationCreatedAt.value ?? '', updatedAt: '', messageCount: messages.value.length, lastMessagePreview: null
});
watch(isSidebarOpen, open => {
  releaseSidebarLayer?.(); releaseSidebarLayer = undefined;
  if (open) { dismissKeyboard(); if (sidebarDrawerQuery.matches) releaseSidebarLayer = navigationLayer(() => setSidebarOpen(false)); }
});
watch(deleteTarget, target => { releaseDeleteLayer?.(); releaseDeleteLayer = target ? navigationLayer(cancelDeleteConversation) : undefined; });
const healthState = ref<'checking' | 'online' | 'offline'>('checking');
const activeTurnId = ref<string | null>(null);
const turnStatus = ref<'thinking' | 'responding' | 'preparing_voice' | 'speaking' | 'interrupted' | 'voice_unavailable' | 'idle'>('idle');
let chatAbortController: AbortController | null = null;
let activeSpeechPlaybackId: string | null = null;
const activeSpeechMessageId = ref<string | null>(null);
const voice = useAegisVoice();
const transcription = useAegisTranscription(insertTranscriptIntoDraft);
let streamScrollFrame: number | null = null;
let historyRefreshTimer: number | null = null;
let memoryActivityAbort: AbortController | null = null;
let memoryActivityGeneration = 0;
let viewportCleanup: (() => void) | null = null;

let proactiveTimer: ReturnType<typeof setTimeout> | undefined;
let proactiveObserver: IntersectionObserver | undefined;
let proactiveBusy = false;
let proactiveAbort = new AbortController();
let lastActivity = '';
const readSequences = new Map<string, number>();
const latestProactiveSequences = new Map<string, number>();
let readBusy = false;
const visibleSequences = new Set<number>();
function observeProactiveMessages(): void {
  proactiveObserver?.disconnect(); visibleSequences.clear();
  messagesContainer.value?.querySelectorAll<HTMLElement>('[data-proactive-sequence]').forEach(el => proactiveObserver?.observe(el));
}
async function markVisibleProactive(): Promise<void> {
  const id = conversationId.value;
  if (!id || readBusy || document.visibilityState !== 'visible' || !document.hasFocus() ||
      document.querySelector('dialog[open]') || isSidebarOpen.value && sidebarDrawerQuery.matches || isRestoring.value) return;
  const sequence = Math.max(0, ...visibleSequences);
  if (sequence <= (readSequences.get(id) ?? 0)) return;
  readBusy = true;
  try {
    await markProactiveRead(id, sequence);
    readSequences.set(id, Math.max(sequence, readSequences.get(id) ?? 0));
    const summary = conversations.value.find(c => c.id === id);
    const newest = Math.max(0, ...messages.value.filter(m => m.conversationId === id).map(m => m.proactiveSequence ?? 0));
    if (summary) summary.hasUnreadProactive = Math.max(newest, latestProactiveSequences.get(id) ?? 0) > sequence;
  } catch { /* A later visibility/poll event retries; the persistent marker remains unread. */ }
  finally { readBusy = false; }
}
async function syncProactiveMessages(): Promise<void> {
  if (proactiveBusy || proactiveAbort.signal.aborted || document.visibilityState !== 'visible') return;
  proactiveBusy = true;
  try {
    const activity = await getProactiveActivity(proactiveAbort.signal);
    if (proactiveAbort.signal.aborted) return;
    const signature = JSON.stringify([...activity].sort((a,b) => a.conversationId.localeCompare(b.conversationId)));
    if (signature !== lastActivity) { if (activity.length) await refreshProactiveHistory(); lastActivity = signature; }
    for (const row of activity) {
      latestProactiveSequences.set(row.conversationId, Math.max(latestProactiveSequences.get(row.conversationId) ?? 0, row.lastProactiveSequence));
      const read = Math.max(readSequences.get(row.conversationId) ?? 0, row.readProactiveSequence);
      readSequences.set(row.conversationId, read);
      const summary = conversations.value.find(c => c.id === row.conversationId);
      if (summary) summary.hasUnreadProactive = row.lastProactiveSequence > read;
      if (row.conversationId !== conversationId.value || isRestoring.value || hasActiveTurn.value) continue;
      const after = Math.max(0, ...messages.value.map(m => m.proactiveSequence ?? 0));
      if (row.lastProactiveSequence <= after) continue;
      const incoming = await getProactiveMessages(row.conversationId, after, proactiveAbort.signal);
      if (proactiveAbort.signal.aborted || row.conversationId !== conversationId.value || isRestoring.value || hasActiveTurn.value) continue;
      const ids = new Set(messages.value.map(m => m.serverId ?? m.id));
      messages.value.push(...incoming.filter(m => !ids.has(m.id)).map(m => ({ ...m, serverId: m.id })));
      messages.value.sort((a,b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime() || a.id.localeCompare(b.id));
      // Preserve the reader's scroll; incoming messages are marked only at actual visibility.
    }
    await nextTick(); observeProactiveMessages(); await markVisibleProactive();
  } catch { /* Network recovery uses the same bounded poll and foreground hooks. */ }
  finally { proactiveBusy = false; }
}
function scheduleProactiveSync(): void {
  clearTimeout(proactiveTimer);
  if (proactiveAbort.signal.aborted || document.visibilityState !== 'visible') return;
  proactiveTimer = setTimeout(async () => { await syncProactiveMessages(); scheduleProactiveSync(); }, 30000);
}
function proactiveVisibilityChanged(): void { requestAnimationFrame(() => { void markVisibleProactive(); }); }
function proactiveForeground(): void { void syncProactiveMessages(); void markVisibleProactive(); scheduleProactiveSync(); }
watch(() => [conversationId.value, messages.value.length, isSidebarOpen.value], async () => { await nextTick(); observeProactiveMessages(); void markVisibleProactive(); });

const canSend = computed(() => draft.value.trim().length > 0 && !isRestoring.value && emailConnectionState.value !== 'pending');
const showErrorMessage = computed(() => !!errorMessage.value && ![
  emailConnectionState.value === 'failed' ? emailConnectionMessage.value : null,
  transcription.errorMessage.value,
  !voice.voiceAvailable.value ? voice.voiceMessage.value : null,
  toolStatusState.value === 'failed' ? toolStatusMessage.value : null
].includes(errorMessage.value));
const hasActiveTurn = computed(() => activeTurnId.value !== null || isLoading.value);
watch(() => hasActiveTurn.value || transcription.isBusy.value || voice.isBusy.value, active => emit('activityChanged', active));
const canStartRecording = computed(() =>
  !hasActiveTurn.value &&
  !isRestoring.value &&
  transcription.available.value &&
  (transcription.state.value === 'idle' || transcription.state.value === 'error'));
const conversationLabel = computed(() => {
  const title = activeConversationTitle.value?.trim() || 'Nova conversa';
  return title.length > 48 ? `${title.slice(0, 48).trimEnd()}...` : title;
});
const conversationCreatedLabel = computed(() => {
  if (!conversationId.value || !activeConversationCreatedAt.value) return null;
  const createdAt = new Date(activeConversationCreatedAt.value);
  if (Number.isNaN(createdAt.getTime())) return null;
  return `Criada em ${new Intl.DateTimeFormat('pt-BR', { day: 'numeric', month: 'long', year: 'numeric' }).format(createdAt)}`;
});
const COMPOSER_MAX_HEIGHT = 168;

function clearToolStatus(): void {
  if (toolStatusTimer !== null) window.clearTimeout(toolStatusTimer);
  toolStatusTimer = null;
  toolStatusState.value = null;
  toolStatusMessage.value = null;
}

function showToolStatus(message: string, state: 'started' | 'completed' | 'failed'): void {
  clearToolStatus();
  toolStatusState.value = state;
  toolStatusMessage.value = message;
  if (state !== 'started') {
    toolStatusTimer = window.setTimeout(clearToolStatus, state === 'completed' ? 1400 : 2400);
  }
}

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
  if (isRestoring.value || window.matchMedia('(pointer: coarse)').matches || clientEnvironment().processSessionId) return;

  void nextTick(() => {
    if (isSidebarOpen.value && sidebarDrawerQuery.matches) return;
    composerInput.value?.focus({ preventScroll: true });
  });
}

function insertTranscriptIntoDraft(transcript: string): void {
  const text = transcript.trim();
  if (!text) return;

  const input = composerInput.value;
  const current = draft.value;
  const start = input?.selectionStart ?? current.length;
  const end = input?.selectionEnd ?? start;
  const before = current.slice(0, start);
  const after = current.slice(end);
  const leftSeparator = before && !/\s$/u.test(before) ? ' ' : '';
  const rightSeparator = after && !/^\s/u.test(after) ? ' ' : '';
  const insertion = `${leftSeparator}${text}${rightSeparator}`;
  draft.value = `${before}${insertion}${after}`;

  void nextTick(() => {
    const nextCursor = before.length + insertion.length;
    composerInput.value?.focus({ preventScroll: true });
    composerInput.value?.setSelectionRange(nextCursor, nextCursor);
    resizeComposer();
  });
}

function syncViewportHeight(): void {
  const viewport = window.visualViewport;
  const height = viewport?.height ?? window.innerHeight;

  document.documentElement.style.setProperty('--app-viewport-height', `${height}px`);
}

async function confirmEmailConnection(): Promise<void> {
  emailConnectionAbortController?.abort();
  const controller = new AbortController();
  emailConnectionAbortController = controller;
  emailConnectionState.value = 'pending';
  emailConnectionMessage.value = 'Conectando conta Google…';
  let connectedEmail: string | null = null;

  const result = await waitForEmailConnection(
    async (signal) => {
      const status = await getEmailStatus(signal);
      connectedEmail = status.emailAddress;
      return status.isConnected === true;
    },
    controller.signal
  );
  if (controller.signal.aborted) return;

  emailConnectionAbortController = null;
  emailConnectionState.value = result === 'connected' ? 'connected' : 'failed';
  emailConnectionMessage.value = result === 'connected'
    ? emailConnectionSuccessMessage(connectedEmail)
    : 'Não foi possível confirmar a conexão com a conta Google. Tente conectar novamente.';
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

function cancelMemoryActivityPolling(): void {
  memoryActivityGeneration++;
  memoryActivityAbort?.abort();
  memoryActivityAbort = null;
}

function startMemoryActivityPolling(message: LocalChatMessage): void {
  cancelMemoryActivityPolling();
  if (!message.memoryActivity?.pending || !message.serverId) return;
  const generation = memoryActivityGeneration;
  const messageId = message.serverId;
  const initialConversationId = message.conversationId;
  const controller = new AbortController();
  memoryActivityAbort = controller;
  const active = (): boolean => generation === memoryActivityGeneration &&
    conversationId.value === initialConversationId &&
    messages.value.includes(message) && message.serverId === messageId &&
    !controller.signal.aborted;
  void pollMemoryActivity(
    (signal) => getMemoryActivity(messageId, signal),
    (snapshot) => { message.memoryActivity = snapshot; },
    controller.signal,
    active
  );
}

function resumeMemoryActivityPolling(): void {
  const latest = [...messages.value].reverse().find((message) => message.role === 'assistant');
  if (latest) startMemoryActivityPolling(latest);
}

async function restoreConversation(): Promise<void> {
  if (!conversationId.value) {
    return;
  }

  isRestoring.value = true;
  errorMessage.value = null;
  clearToolStatus();

  try {
    const conversation = await getConversation(conversationId.value);
    activeConversationTitle.value = conversation.title ?? 'Nova conversa';
    activeConversationCreatedAt.value = conversation.createdAt;
    messages.value = conversation.messages.map((message) => ({
      ...message,
      serverId: message.id
    }));
    resumeMemoryActivityPolling();
    scrollToLatest();
    focusComposer();
  } catch {
    localStorage.removeItem(STORAGE_KEY);
    conversationId.value = null;
    activeConversationTitle.value = null;
    activeConversationCreatedAt.value = null;
    errorMessage.value = 'Não foi possível carregar a conversa anterior.';
  } finally {
    isRestoring.value = false;
  }
}

function mergeConversationSummary(summary: ConversationSummary): void {
  if (summary.id === conversationId.value) activeConversationCreatedAt.value = summary.createdAt;
  const withoutCurrent = conversations.value.filter((conversation) => conversation.id !== summary.id);
  conversations.value = [summary, ...withoutCurrent].sort((first, second) => {
    const dateDifference = new Date(second.updatedAt).getTime() - new Date(first.updatedAt).getTime();
    return dateDifference || second.id.localeCompare(first.id);
  });
}

async function refreshProactiveHistory(): Promise<void> {
  const page = await getConversations(30, null);
  if (conversations.value.length === 0) {
    nextHistoryCursor.value = page.nextCursor ?? null; hasMoreHistory.value = page.hasMore;
  }
  // Merge the current head without discarding pages the reader already loaded or their cursor.
  for (const summary of page.items) mergeConversationSummary(summary);
  const active = page.items.find(c => c.id === conversationId.value);
  if (active) activeConversationTitle.value = active.title ?? 'Nova conversa';
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
      activeConversationCreatedAt.value = active.createdAt;
    }
  } catch {
    historyErrorMessage.value = 'Não foi possível carregar o histórico.';
  } finally {
    isLoadingHistory.value = false;
  }
}

function refreshHistoryAfterResponse(): void {
  if (historyRefreshTimer !== null) window.clearTimeout(historyRefreshTimer);
  void loadConversationHistory(true);
  const retry = (attempt: number): void => {
    if (attempt >= 2 || activeConversationTitle.value !== 'Nova conversa') return;
    historyRefreshTimer = window.setTimeout(async () => {
      historyRefreshTimer = null;
      if (activeConversationTitle.value !== 'Nova conversa') return;
      await loadConversationHistory(true);
      retry(attempt + 1);
    }, attempt === 0 ? 1800 : 3400);
  };
  retry(0);
}

function setSidebarOpen(open: boolean): void {
  isSidebarOpen.value = open;
  void nextTick(() => {
    document.querySelector<HTMLButtonElement>(open ? '#sidebar-collapse-button' : '#sidebar-expand-button')?.focus({ preventScroll: true });
  });
}

function closeSidebarDrawer(): void {
  if (sidebarDrawerQuery.matches) isSidebarOpen.value = false;
}

async function openConversation(targetConversationId: string): Promise<void> {
  dismissKeyboard();
  drafts.set(conversationId.value ?? 'new', draft.value);
  if (isRestoring.value || targetConversationId === conversationId.value) {
    closeSidebarDrawer();
    return;
  }
  if (historyRefreshTimer !== null) window.clearTimeout(historyRefreshTimer);
  historyRefreshTimer = null;
  cancelMemoryActivityPolling();

  transcription.discard();
  await stopActiveTurn('conversation_changed');

  isRestoring.value = true;
  errorMessage.value = null;
  feedbackTarget.value = null;
  feedbackErrorMessage.value = null;
  feedbackStatusByMessageId.value = {};

  try {
    const conversation = await getConversation(targetConversationId);
    conversationId.value = conversation.id;
    draft.value = drafts.get(conversation.id) ?? '';
    resizeComposer();
    activeConversationTitle.value = conversation.title ?? 'Nova conversa';
    activeConversationCreatedAt.value = conversation.createdAt;
    localStorage.setItem(STORAGE_KEY, conversation.id);
    messages.value = conversation.messages.map((message) => ({
      ...message,
      serverId: message.id
    }));
    resumeMemoryActivityPolling();
    closeSidebarDrawer();
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

  const content = draft.value.trim();
  cancelMemoryActivityPolling();
  transcription.discard();
  const turnId = crypto.randomUUID();
  const localMessage = createLocalUserMessage(content);
  const assistantMessage = createLocalAssistantMessage();

  draft.value = drafts.get('new') ?? '';
  resizeComposer();
  errorMessage.value = null;
  clearToolStatus();
  if (emailConnectionState.value === 'connected') {
    emailConnectionMessage.value = null;
  }
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
          if (!conversationId.value) activeConversationCreatedAt.value = localMessage.createdAt;
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
        onToolStatus: (eventTurnId, status) => {
          if (eventTurnId !== activeTurnId.value) return;
          turnStatus.value = 'thinking';
          showToolStatus(status.message, status.state);
        },
        onDone: ({ turnId: completedTurnId, conversationId: completedConversationId, messageId, conversationTitle, memoryActivity }) => {
          if (completedTurnId !== activeTurnId.value) return;
          if (!conversationId.value) activeConversationCreatedAt.value = localMessage.createdAt;
          conversationId.value = completedConversationId;
          localStorage.setItem(STORAGE_KEY, completedConversationId);
          activeConversationTitle.value = conversationTitle ?? activeConversationTitle.value ?? 'Nova conversa';
          localMessage.conversationId = completedConversationId;
          localMessage.pending = false;
          assistantMessage.conversationId = completedConversationId;
          assistantMessage.serverId = messageId;
          assistantMessage.memoryActivity = memoryActivity;
          assistantMessage.content = streamedContent;
          assistantMessage.pending = false;
          isLoading.value = false;
          if (toolStatusState.value === 'completed' || toolStatusState.value === 'failed') {
            showToolStatus(toolStatusMessage.value ?? '', toolStatusState.value);
          } else {
            clearToolStatus();
          }
          scrollToLatest();

          window.setTimeout(() => {
            assistantMessage.streaming = false;
          }, 600);

          refreshHistoryAfterResponse();
          startMemoryActivityPolling(assistantMessage);
          if (voice.autoSpeak.value) {
            activeTurnId.value = null;
            void playSpeech(completedTurnId, messageId);
          } else {
            void completeTurnWithoutSpeech(completedTurnId).catch(() => undefined);
            activeTurnId.value = null;
            turnStatus.value = 'idle';
          }
        },
        onError: (message) => {
          if (activeTurnId.value !== turnId) return;
          errorMessage.value = message;
        }
      },
      chatAbortController.signal
    );
  } catch (error) {
    if (activeTurnId.value !== turnId) return;
    if ((error as DOMException).name === 'AbortError') {
      turnStatus.value = 'interrupted';
    } else {
      errorMessage.value ??= error instanceof TypeError
        ? 'Servidor indisponível. Tente novamente em instantes.'
        : error instanceof Error ? error.message : 'Não foi possível enviar a mensagem.';
    }
    localMessage.pending = false;
    assistantMessage.pending = false;
    assistantMessage.streaming = false;
    clearToolStatus();
    if (!assistantMessage.content) {
      messages.value = messages.value.filter((message) => message.id !== assistantMessage.id);
    }
  } finally {
    if (activeTurnId.value === turnId) isLoading.value = false;
  }
}

async function stopActiveTurn(reason = 'user_stop'): Promise<void> {
  clearToolStatus();
  const turnId = activeTurnId.value;
  if (reason === 'user_stop' && turnId) {
    errorMessage.value = null;
    const assistantMessage = messages.value[messages.value.length - 1];
    const userMessage = messages.value[messages.value.length - 2];
    if (assistantMessage?.role === 'assistant' && assistantMessage.pending) {
      assistantMessage.pending = false;
      assistantMessage.streaming = false;
      assistantMessage.interrupted = true;
      if (userMessage?.role === 'user') userMessage.pending = false;
      scrollToLatest(false);
    }
  }
  activeTurnId.value = null;
  chatAbortController?.abort();
  chatAbortController = null;
  stopPlayback();
  isLoading.value = false;
  if (turnId) {
    turnStatus.value = 'interrupted';
    void cancelTurn(turnId).catch(() => undefined);
  }
}

function stopPlayback(): void {
  activeSpeechPlaybackId = null;
  activeSpeechMessageId.value = null;
  void voice.stop();
  if (!hasActiveTurn.value) turnStatus.value = voice.voiceAvailable.value ? 'idle' : 'voice_unavailable';
}

async function playSpeech(turnId: string, assistantMessageId: string): Promise<void> {
  const playbackId = crypto.randomUUID();
  activeSpeechPlaybackId = playbackId;
  activeSpeechMessageId.value = assistantMessageId;
  turnStatus.value = 'preparing_voice';
  await voice.speak(turnId, assistantMessageId, true);
  if (activeSpeechPlaybackId === playbackId) {
    activeSpeechPlaybackId = null;
    activeSpeechMessageId.value = null;
    if (!hasActiveTurn.value) turnStatus.value = voice.voiceAvailable.value ? 'idle' : 'voice_unavailable';
  }
}

async function checkServerHealth(): Promise<void> {
  healthState.value = 'checking'; const controller = new AbortController(); const timeout = window.setTimeout(() => controller.abort(), 3000);
  try { healthState.value = await getHealth(controller.signal) ? 'online' : 'offline'; } catch { healthState.value = 'offline'; } finally { window.clearTimeout(timeout); }
}

function replayMessage(message: LocalChatMessage): void {
  if (!message.serverId) return;
  void (async () => {
    await stopActiveTurn('replay');
    const turnId = crypto.randomUUID();
    await playSpeech(turnId, message.serverId!);
  })();
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && transcription.isRecording.value) {
    event.preventDefault();
    transcription.discard();
    return;
  }

  if (event.key === 'Enter' && !event.shiftKey && canSend.value) {
    event.preventDefault();
    void handleSubmit();
  }
}

async function toggleRecording(): Promise<void> {
  if (transcription.isRecording.value) {
    transcription.stopRecording();
    return;
  }

  if (!canStartRecording.value) return;
  stopPlayback();
  await transcription.start();
}

function startNewConversation(): void {
  dismissKeyboard();
  cancelMemoryActivityPolling();
  if (historyRefreshTimer !== null) window.clearTimeout(historyRefreshTimer);
  historyRefreshTimer = null;
  transcription.discard();
  void stopActiveTurn('new_conversation');
  localStorage.removeItem(STORAGE_KEY);
  conversationId.value = null;
  activeConversationTitle.value = null;
  activeConversationCreatedAt.value = null;
  messages.value = [];
  draft.value = drafts.get('new') ?? '';
  resizeComposer();
  errorMessage.value = null;
  feedbackTarget.value = null;
  feedbackErrorMessage.value = null;
  feedbackStatusByMessageId.value = {};
  closeSidebarDrawer();
  focusComposer();
}

function requestRenameConversation(conversation: ConversationSummary): void {
  dismissKeyboard(); renameError.value = null; renameTarget.value = conversation;
}
async function handleRenameConversation(title: string): Promise<void> {
  if (!renameTarget.value || isRenaming.value) return;
  isRenaming.value = true; renameError.value = null;
  try {
    const targetId = renameTarget.value.id;
    const summary = await renameConversation(targetId, title);
    mergeConversationSummary(summary);
    if (conversationId.value === targetId) activeConversationTitle.value = summary.title ?? 'Nova conversa';
    renameTarget.value = null;
  } catch { renameError.value = 'Não consegui salvar o título. Tente novamente.'; }
  finally { isRenaming.value = false; }
}

function requestDeleteConversation(conversation: ConversationSummary): void {
  dismissKeyboard();
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

let explicitDestination: string | null = null;
function navigateToConversation(id: string): void {
  if (isRestoring.value) { explicitDestination = id; return; }
  void openConversation(id);
}
watch(isRestoring, restoring => {
  if (!restoring && explicitDestination) { const id = explicitDestination; explicitDestination = null; void openConversation(id); }
});
defineExpose({ openConversation: navigateToConversation });
onMounted(() => {
  syncViewportHeight();
  proactiveObserver = new IntersectionObserver(entries => {
    for (const entry of entries) { const sequence = Number((entry.target as HTMLElement).dataset.proactiveSequence); if (entry.isIntersecting && entry.intersectionRatio >= 1) visibleSequences.add(sequence); else visibleSequences.delete(sequence); }
    void markVisibleProactive();
  }, { root: messagesContainer.value, threshold: 1 });
  window.addEventListener('aegis-chat-visible', proactiveVisibilityChanged); window.addEventListener('aegis-navigation-layer', proactiveVisibilityChanged);
  document.addEventListener('visibilitychange', proactiveForeground); window.addEventListener('focus', proactiveForeground); window.addEventListener('online', proactiveForeground);
  void syncProactiveMessages(); scheduleProactiveSync();
  stopObservingGoogle = observeGoogleConnection({
    started: () => {
      emailConnectionState.value = 'pending';
      emailConnectionMessage.value = 'Conectando conta Google…';
    },
    connected: () => void confirmEmailConnection(),
    failed: (code) => {
      const message = emailConnectionFailureMessage(code);
      if (message) {
        emailConnectionAbortController?.abort();
        emailConnectionState.value = 'failed';
        emailConnectionMessage.value = message;
      }
    }
  });
  const handleViewportChange = (): void => {
    syncViewportHeight();
  };
  const handleSidebarBreakpointChange = (event: MediaQueryListEvent): void => {
    isSidebarOpen.value = !event.matches;
  };

  sidebarDrawerQuery.addEventListener('change', handleSidebarBreakpointChange);
  window.addEventListener('resize', handleViewportChange);
  if (window.visualViewport) {
    window.visualViewport.addEventListener('resize', handleViewportChange);
    window.visualViewport.addEventListener('scroll', handleViewportChange);
  }
  viewportCleanup = () => {
    sidebarDrawerQuery.removeEventListener('change', handleSidebarBreakpointChange);
    window.removeEventListener('resize', handleViewportChange);
    window.visualViewport?.removeEventListener('resize', handleViewportChange);
    window.visualViewport?.removeEventListener('scroll', handleViewportChange);
  };

  void loadConversationHistory(true);
  void restoreConversation();
  void voice.refreshStatus();
  void transcription.refreshStatus();
  void checkServerHealth();
  focusComposer();
});

onBeforeUnmount(() => {
  proactiveAbort.abort(); clearTimeout(proactiveTimer); proactiveObserver?.disconnect();
  window.removeEventListener('aegis-chat-visible', proactiveVisibilityChanged); window.removeEventListener('aegis-navigation-layer', proactiveVisibilityChanged);
  document.removeEventListener('visibilitychange', proactiveForeground); window.removeEventListener('focus', proactiveForeground); window.removeEventListener('online', proactiveForeground);
  cancelMemoryActivityPolling();
  clearToolStatus();
  emailConnectionAbortController?.abort();
  stopObservingGoogle?.();
  transcription.dispose();
  void stopActiveTurn('view_unmounted');
  if (historyRefreshTimer !== null) window.clearTimeout(historyRefreshTimer);

  viewportCleanup?.();
  releaseSidebarLayer?.(); releaseDeleteLayer?.();
});
</script>

<template>
  <main class="app-shell">
    <div ref="sidebarGestureSurface" class="hideout-shell" :class="{ 'hideout-shell--dragging': sidebarGesture.dragging.value, 'hideout-shell--sidebar-open': isSidebarOpen }" :style="sidebarGesture.style.value">
      <button
        v-if="!isSidebarOpen"
        id="sidebar-expand-button"
        type="button"
        class="sidebar-toggle sidebar-toggle--floating"
        aria-label="Abrir histórico"
        title="Expandir barra lateral"
        aria-controls="conversation-sidebar"
        :aria-expanded="false"
        @click="setSidebarOpen(true)"
      >
        <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="3" /><path d="M9 4v16" /></svg>
      </button>
      <ConversationSidebar
        :app-version="appVersion"
        :conversations="conversations"
        :active-conversation-id="conversationId"
        :disabled="isLoading || isRestoring"
        :open="isSidebarOpen"
        :has-more="hasMoreHistory"
        :is-loading-more="isLoadingHistory"
        :history-error="historyErrorMessage"
        @close="setSidebarOpen(false)"
        @new-conversation="startNewConversation"
        @open-conversation="openConversation"
        @rename-conversation="requestRenameConversation"
        @info-conversation="infoTarget = $event"
        @request-delete-conversation="requestDeleteConversation"
        @load-more="loadConversationHistory(false)"
        @retry-history="loadConversationHistory(true)"
      ><template #global-navigation><slot name="global-navigation" /></template></ConversationSidebar>

      <section class="chat-panel" aria-label="Conversa com a Aegis">
      <header class="chat-header">
        <div class="conversation-heading">
          <span>{{ conversationId ? 'Conversa ativa' : 'Nova conversa' }}</span>
          <div>
            <h1>{{ conversationLabel }}</h1>
            <p v-if="conversationCreatedLabel">{{ conversationCreatedLabel }}</p>
          </div>
        </div>

        <div class="chat-voice-controls">
          <ConversationMenu :title="activeConversationTitle || 'Nova conversa'" show-title :disabled="!conversationId || isRestoring" @info="infoTarget = currentConversationSummary" @rename="requestRenameConversation(currentConversationSummary)" @delete="requestDeleteConversation(currentConversationSummary)" />
        </div>

        <span class="server-indicator" :class="`server-indicator--${healthState}`" role="img" :aria-label="healthState === 'checking' ? 'Verificando servidor' : healthState === 'online' ? 'Servidor disponível' : 'Servidor indisponível'"><i></i></span>
      </header>

      <div v-if="$slots.notice" class="chat-notices"><slot name="notice" /></div>
      <div ref="messagesContainer" class="messages" aria-live="polite">
        <div v-if="isRestoring" class="empty-state">
          <AegisMark />
          <span>Carregando conversa...</span>
        </div>

        <div v-else-if="messages.length === 0" class="empty-state">
          <div class="empty-state__mark"><AegisMark /></div>
          <span class="empty-state__eyebrow">Aegis</span>
          <strong>Nova conversa</strong>
          <span>Digite ou fale uma mensagem.</span>
        </div>

        <template v-else>
          <template v-for="message in messages" :key="message.id">
          <ChatMessage
            :message="message"
            :feedback-status="feedbackStatusByMessageId[message.serverId ?? message.id]"
            :is-playing="voice.isBusy.value && message.serverId === activeSpeechMessageId"
            :activity-status="message.role === 'assistant' && message.id === messages[messages.length - 1]?.id
              ? (toolStatusMessage ?? (message.pending && isLoading && (turnStatus === 'thinking' || !message.content.trim()) ? 'Pensando…' : null))
              : null"
            :activity-state="toolStatusState === 'completed' ? 'completed' : toolStatusState === 'failed' ? 'failed' : 'working'"
            @feedback="openFeedback"
            @replay="replayMessage"
            @stop-playback="stopPlayback"
          />
          <span v-if="message.isProactive && message.proactiveSequence" :data-proactive-sequence="message.proactiveSequence" class="proactive-read-sentinel" aria-hidden="true"></span>
          </template>
        </template>

        <div ref="messagesEnd" class="messages-end"></div>
      </div>

      <form class="composer" @submit.prevent="hasActiveTurn ? stopActiveTurn() : handleSubmit()">
        <p v-if="emailConnectionMessage" class="email-connection-notice" :class="`email-connection-notice--${emailConnectionState}`" role="status">{{ emailConnectionMessage }}</p>
        <p v-if="showErrorMessage" class="composer-error" role="alert">{{ errorMessage }}</p>
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

          <button
            class="record-button"
            :class="{ 'record-button--recording': transcription.isRecording.value, 'record-button--transcribing': transcription.isTranscribing.value }"
            type="button"
            :disabled="transcription.isTranscribing.value || (!transcription.isRecording.value && !canStartRecording)"
            :aria-label="transcription.isRecording.value ? 'Encerrar gravação' : transcription.isTranscribing.value ? 'Transcrevendo gravação' : 'Gravar mensagem de voz'"
            :title="transcription.isRecording.value ? 'Encerrar gravação' : transcription.isTranscribing.value ? 'Transcrevendo…' : 'Gravar mensagem de voz'"
            @click="toggleRecording"
          >
            <span v-if="transcription.isRecording.value" class="recording-bars" aria-hidden="true"><i :style="{ height: `${2 + transcription.inputLevel.value * 18}px` }"/><i :style="{ height: `${2 + transcription.inputLevel.value * 24}px` }"/><i :style="{ height: `${2 + transcription.inputLevel.value * 18}px` }"/></span>
            <svg v-else viewBox="0 0 24 24" aria-hidden="true"><rect x="9" y="3" width="6" height="12" rx="3"/><path d="M6 11a6 6 0 0 0 12 0M12 17v4m-4 0h8"/></svg>
          </button>

          <button
            v-if="transcription.isBusy.value"
            class="record-discard-button"
            type="button"
            :aria-label="transcription.isTranscribing.value ? 'Cancelar transcrição' : 'Descartar gravação'"
            :title="transcription.isTranscribing.value ? 'Cancelar transcrição' : 'Descartar gravação'"
            @click="transcription.discard"
          ><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 7h14M9 7V5h6v2m-8 0 1 13h8l1-13M10 11v5m4-5v5"/></svg></button>

          <button class="send-button" :class="{ 'send-button--stop': hasActiveTurn }" type="submit" :disabled="(!hasActiveTurn && !canSend) || isRestoring || emailConnectionState === 'pending'" :aria-label="hasActiveTurn ? 'Interromper geração' : 'Enviar mensagem'">
            <svg v-if="hasActiveTurn" viewBox="0 0 20 20" aria-hidden="true"><rect x="6.25" y="6.25" width="7.5" height="7.5" rx="1.2" /></svg>
            <svg v-else viewBox="0 0 20 20" aria-hidden="true" focusable="false"><path d="M10 16V4m0 0L5.8 8.2M10 4l4.2 4.2" /></svg>
          </button>
        </div>
        <div v-if="transcription.isRecording.value" class="voice-input-status"><meter min="0" max="1" :value="transcription.inputLevel.value" aria-label="Nível real da entrada de áudio" /><span role="status">{{ transcription.inputStatus.value }}</span></div>
        <p v-if="transcription.errorMessage.value" class="composer-error" role="status">{{ transcription.errorMessage.value }}</p>
        <p v-if="voice.voiceMessage.value && !voice.voiceAvailable.value" class="composer-error" role="status">{{ voice.voiceMessage.value }}</p>
        <span class="composer-hint">{{ transcription.isRecording.value ? 'Gravando… · Esc para descartar' : transcription.isTranscribing.value ? 'Transcrevendo…' : transcription.notice.value ?? 'Enter para enviar · Shift + Enter para nova linha' }}</span>
      </form>
      </section>
    </div>

    <ConversationInfo v-if="infoTarget" :conversation-id="infoTarget.id" :created-at="infoTarget.createdAt" @close="infoTarget = null" />
    <RenameConversationDialog v-if="renameTarget" :title="renameTarget.title || 'Nova conversa'" :saving="isRenaming" :error="renameError" @cancel="renameTarget = null" @save="handleRenameConversation" />

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
