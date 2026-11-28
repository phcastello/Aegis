<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, shallowReactive, watch } from 'vue';
import { observeGoogleConnection } from '../services/googleConnection';
import AegisMark from '../components/AegisMark.vue';
import RuriMascot from '../components/RuriMascot.vue';
import ChatMessage from '../components/ChatMessage.vue';
import VideoAttachmentDialog from '../components/VideoAttachmentDialog.vue';
import AttachmentPreview from '../components/AttachmentPreview.vue';
import ChatAttachmentPicker from '../components/ChatAttachmentPicker.vue';
import BackgroundResearchStatus from '../components/BackgroundResearchStatus.vue';
import WebResearchActivity from '../components/WebResearchActivity.vue';
import { mergeResearchSites, topResearchSites } from '../services/researchSites';
import { cancelResearchJob, isResearchPending, listResearchJobs, type ResearchJob } from '../services/backgroundResearch';
import ConversationSidebar from '../components/ConversationSidebar.vue';
import ConversationInfo from '../components/ConversationInfo.vue';
import ConversationMenu from '../components/ConversationMenu.vue';
import RecordingComposer from '../components/RecordingComposer.vue';
import RenameConversationDialog from '../components/RenameConversationDialog.vue';
import { clientEnvironment } from '../services/clientEnvironment';
import { attachmentSize, checkAttachmentLimits, draftLink, pickedFile, releaseAttachment, sharedAttachmentFile, uploadAttachment, type DraftAttachment } from '../services/chatAttachments';
import { resolveVideoCitation, type VideoCitation } from '../services/chatVideo';
import { initialConversationId } from '../services/conversationSession';
import { ConversationSyncQueue, mergeConversationMessages } from '../services/conversationSync';
import { NativeVoiceSegments, parseNativeVoiceStreamEvent, StreamTextReveal, STREAM_FRAME_MS, type NativeVoiceToolStatus } from '../services/streamTextPresentation';
import { dismissKeyboard, hasNavigationLayer, navigationLayer } from '../services/navigation';
import { isMessageEndVisible } from '../services/proactiveVisibility';
import FeedbackDialog from '../components/FeedbackDialog.vue';
import {
  AegisApiError,
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
import { useAegisTranscription, type TranscriptionIntent } from '../composables/useAegisTranscription';
import { useSidebarGesture } from '../composables/useSidebarGesture';
import { waitForEmailConnection } from '../services/emailConnectionPolling';
import { pollMemoryActivity } from '../services/memoryActivityPolling';
import { emailConnectionSuccessMessage } from '../services/emailConnectionFeedback';
import type {
  Conversation,
  ConversationSummary,
  FeedbackRating,
  LocalChatMessage,
  SubmitMessageFeedbackRequest
} from '../types/chat';

const props = defineProps<{ appVersion?: string; conversationPushActive?: boolean; nativeVoiceActive?: boolean }>();
const emit = defineEmits<{ activityChanged: [active: boolean] }>();

const STORAGE_KEY = 'aegis.currentConversationId';

// These contexts belong to this chat view. A running turn keeps its own context
// even when the reader selects another conversation (including an unsaved one).
type ConversationContext = {
  id: string | null;
  messages: LocalChatMessage[];
  draft: string;
  files: DraftAttachment[];
  title: string | null;
  createdAt: string | null;
  error: string | null;
  loaded: boolean;
  remoteUpdatedAt: string | null;
  remoteMessageCount: number | null;
  revision: number;
  following: boolean;
  scrollTop: number | null;
  toolMessage: string | null;
  toolState: 'started' | 'completed' | 'failed' | null;
  researchActive: boolean;
  researchSites: Map<string, number>;
  retry: { turnId: string; content: string; files: DraftAttachment[]; conversationId: string | null } | null;
};
function createConversationContext(id: string | null = null): ConversationContext {
  return reactive({ id, messages: [], draft: '', files: [], title: null, createdAt: null, error: null,
    loaded: false, remoteUpdatedAt: null, remoteMessageCount: null, revision: 0, following: true, scrollTop: null, retry: null, toolMessage: null, toolState: null,
    researchActive: false, researchSites: new Map<string, number>() });
}
const contexts = new Map<string, ConversationContext>();
const deletedConversations = new Set<string>();
let newConversation = createConversationContext();
const initialId = initialConversationId(localStorage, clientEnvironment().processSessionId);
const selected = ref(initialId ? createConversationContext(initialId) : newConversation);
const unsentConversations = ref<ConversationContext[]>([]);
const hasDraft = (owner: ConversationContext): boolean => Boolean(owner.draft.trim() || owner.files.length);
const unsentConversation = computed(() => unsentConversations.value.find(owner => owner !== selected.value && hasDraft(owner)) ?? null);
if (initialId) contexts.set(initialId, selected.value);
const messages = computed(() => selected.value.messages);
const videoTarget = ref<{ citation: VideoCitation; fileName: string } | null>(null);
const resolveVideo = (citation: VideoCitation) => resolveVideoCitation(messages.value, citation);
function openVideo(citation: VideoCitation): void {
  const file = resolveVideo(citation);
  if (file) videoTarget.value = { citation, fileName: file.fileName };
}
watch(selected, () => { videoTarget.value = null; });
const conversationId = computed(() => selected.value.id);
const draft = computed({ get: () => selected.value.draft, set: value => { selected.value.draft = value; } });
const isLoading = computed(() => selectedHasActiveTurn.value);
const isRestoring = ref(false);
const errorMessage = computed({ get: () => selected.value.error, set: value => { selected.value.error = value; } });
const toolStatusMessage = computed(() => selected.value.toolMessage);
const toolStatusState = computed(() => selected.value.toolState);
const toolStatusTimers = new Map<ConversationContext, number>();
const selectedResearchSites = computed(() => selected.value.researchActive ? topResearchSites(selected.value.researchSites) : null);
const cancellingResearch = shallowReactive(new Set<string>());
const researchByConversation = shallowReactive(new Map<string, ResearchJob[]>());
const pendingResearchReads = new Map<string, { controller: AbortController; refreshRequested: boolean }>();
const selectedJobByMessage = computed(() => {
  const jobs = conversationId.value ? researchByConversation.get(conversationId.value) ?? [] : [];
  return new Map(jobs.filter(job => !job.resultMessageId || !messages.value.some(message =>
    (message.serverId ?? message.id) === job.resultMessageId)).map(job => [job.userMessageId, job]));
});
let researchTimer: ReturnType<typeof setTimeout> | undefined;
const emailConnectionState = ref<'idle' | 'pending' | 'connected' | 'failed'>('idle');
const emailConnectionMessage = ref<string | null>(null);
let emailConnectionAbortController: AbortController | null = null;
let stopObservingGoogle: (() => void) | null = null;
const messagesContainer = ref<HTMLElement | null>(null);
const messagesContent = ref<HTMLElement | null>(null);
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
const activeConversationTitle = computed({ get: () => selected.value.title, set: value => { selected.value.title = value; } });
const activeConversationCreatedAt = computed({ get: () => selected.value.createdAt, set: value => { selected.value.createdAt = value; } });
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
let healthController: AbortController | null = null;
let lastHealthCheck = 0;
const MAX_CONCURRENT_TURNS = 3;
type ActiveTurn = {
  id: string; owner: ConversationContext; user: LocalChatMessage; assistant: LocalChatMessage;
  controller: AbortController; accepted: boolean; uploading: boolean; status: 'thinking' | 'responding';
};
const activeTurns = shallowReactive(new Map<string, ActiveTurn>());
const selectedTurn = computed(() => [...activeTurns.values()].find(turn => turn.owner === selected.value));
const selectedHasActiveTurn = computed(() => !!selectedTurn.value);
const hasActiveTurn = computed(() => activeTurns.size > 0);
const turnStatus = computed(() => selectedTurn.value?.status ?? 'idle');
const generatingConversationIds = computed(() => [...activeTurns.values()].flatMap(turn => turn.owner.id ? [turn.owner.id] : []));
let activeSpeechPlaybackId: string | null = null;
let activeSpeechOwner: ConversationContext | null = null;
const activeSpeechMessageId = ref<string | null>(null);
const voice = useAegisVoice();
const transcription = useAegisTranscription(insertTranscriptIntoDraft);
// Never hide the controls of a recording the user explicitly began.
const nativeComposerActive = computed(() => Boolean(props.nativeVoiceActive && !transcription.isBusy.value));
let capture: { owner: ConversationContext; text: string; start: number; end: number } | null = null;
let streamScrollFrame: number | null = null;
let layoutObserver: ResizeObserver | null = null;
let conversationLoad = 0;
let measuredScrollOwner: ConversationContext | null = null;
let lastScrollTop = 0;
let lastScrollHeight = 0;
let lastViewportHeight = 0;
let touchY: number | null = null;
let manualScroll = false;
let manualDirection = 0;
let manualScrollUntil = 0;
let programmaticScrollTop: number | null = null;
const historyRefreshTimers = new Map<string, number>();
let historyReloadRequested = false;
let disposed = false;
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
let proactiveReadFrame: number | undefined;
function observeProactiveMessages(): void {
  proactiveObserver?.disconnect();
  messagesContainer.value?.querySelectorAll<HTMLElement>('[data-proactive-sequence]').forEach(el => proactiveObserver?.observe(el));
}
function canReadProactive(id: string): boolean {
  return id === conversationId.value && !proactiveAbort.signal.aborted && document.visibilityState === 'visible' &&
    !document.querySelector('dialog[open]') && !hasNavigationLayer() &&
    !(isSidebarOpen.value && sidebarDrawerQuery.matches) && !isRestoring.value;
}
function visibleProactiveSequence(id: string): number {
  const container = messagesContainer.value;
  if (!container) return 0;
  const current = new Set(messages.value.filter(m => m.conversationId === id && m.isProactive).map(m => m.proactiveSequence));
  let sequence = 0;
  for (const el of Array.from(container.querySelectorAll<HTMLElement>('[data-proactive-sequence]'))) {
    const candidate = Number(el.dataset.proactiveSequence);
    if (el.dataset.proactiveConversation === id && current.has(candidate) && isMessageEndVisible(el, container)) sequence = Math.max(sequence, candidate);
  }
  return sequence;
}
async function markVisibleProactive(): Promise<void> {
  const id = conversationId.value;
  if (!id || readBusy || !canReadProactive(id)) return;
  const sequence = visibleProactiveSequence(id);
  if (sequence <= (readSequences.get(id) ?? 0)) return;
  readBusy = true;
  let acknowledged = false;
  try {
    const foreground = clientEnvironment().isForeground;
    if (!(foreground ? await foreground() : document.hasFocus())) return;
    // A native check crosses an async boundary: selection, overlays and layout
    // may have changed while waiting for the Activity reply.
    if (!canReadProactive(id) || visibleProactiveSequence(id) < sequence) return;
    await markProactiveRead(id, sequence);
    const read = Math.max(sequence, readSequences.get(id) ?? 0);
    readSequences.set(id, read);
    acknowledged = true;
    const summary = conversations.value.find(c => c.id === id);
    const newest = Math.max(0, ...messages.value.filter(m => m.conversationId === id).map(m => m.proactiveSequence ?? 0));
    if (summary) summary.hasUnreadProactive = Math.max(newest, latestProactiveSequences.get(id) ?? 0) > read;
  } catch { /* A later visibility/poll event retries; the persistent marker remains unread. */ }
  finally {
    readBusy = false;
    if (acknowledged) proactiveVisibilityChanged();
  }
}
async function syncProactiveMessages(): Promise<void> {
  if (proactiveBusy || proactiveAbort.signal.aborted || document.visibilityState !== 'visible') return;
  proactiveBusy = true;
  try {
    const activity = await getProactiveActivity(proactiveAbort.signal);
    if (proactiveAbort.signal.aborted) return;
    const signature = JSON.stringify([...activity].sort((a,b) => a.conversationId.localeCompare(b.conversationId)));
    if (signature !== lastActivity) { if (activity.length) await refreshConversationHead(); lastActivity = signature; }
    for (const row of activity) {
      latestProactiveSequences.set(row.conversationId, Math.max(latestProactiveSequences.get(row.conversationId) ?? 0, row.lastProactiveSequence));
      const read = Math.max(readSequences.get(row.conversationId) ?? 0, row.readProactiveSequence);
      readSequences.set(row.conversationId, read);
      const summary = conversations.value.find(c => c.id === row.conversationId);
      if (summary) summary.hasUnreadProactive = (latestProactiveSequences.get(row.conversationId) ?? 0) > read;
      if (row.conversationId !== conversationId.value || isRestoring.value || selectedHasActiveTurn.value) continue;
      const after = Math.max(0, ...messages.value.map(m => m.proactiveSequence ?? 0));
      if (row.lastProactiveSequence <= after) continue;
      const incoming = await getProactiveMessages(row.conversationId, after, proactiveAbort.signal);
      if (proactiveAbort.signal.aborted || row.conversationId !== conversationId.value || isRestoring.value || selectedHasActiveTurn.value) continue;
      const ids = new Set(messages.value.map(m => m.serverId ?? m.id));
      selected.value.revision++;
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
function proactiveVisibilityChanged(): void {
  if (proactiveReadFrame !== undefined) return;
  proactiveReadFrame = requestAnimationFrame(() => { proactiveReadFrame = undefined; void markVisibleProactive(); });
}
function proactiveForeground(): void { void syncProactiveMessages(); void markVisibleProactive(); scheduleProactiveSync(); }
watch(() => [conversationId.value, messages.value.length, isSidebarOpen.value, isRestoring.value], async () => { await nextTick(); observeProactiveMessages(); proactiveVisibilityChanged(); });

const deletingSelectedConversation = computed(() => isDeletingConversation.value && deleteTarget.value?.id === conversationId.value);
const canSend = computed(() => hasDraft(selected.value) && !selectedHasActiveTurn.value && !isRestoring.value && !deletingSelectedConversation.value && !transcription.isBusy.value && emailConnectionState.value !== 'pending');
const showErrorMessage = computed(() => !!errorMessage.value && ![
  emailConnectionState.value === 'failed' ? emailConnectionMessage.value : null,
  transcription.errorMessage.value,
  !voice.voiceAvailable.value ? voice.voiceMessage.value : null,
  toolStatusState.value === 'failed' ? toolStatusMessage.value : null
].includes(errorMessage.value));
watch(() => hasActiveTurn.value || transcription.isBusy.value || voice.isBusy.value, active => emit('activityChanged', active));
const canStartRecording = computed(() =>
  !selectedHasActiveTurn.value &&
  !isRestoring.value &&
  !deletingSelectedConversation.value &&
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

function clearToolStatus(owner = selected.value): void {
  const timer = toolStatusTimers.get(owner);
  if (timer !== undefined) window.clearTimeout(timer);
  toolStatusTimers.delete(owner);
  owner.toolState = null;
  owner.toolMessage = null;
  owner.researchActive = false;
  owner.researchSites = new Map();
}

function showToolStatus(
  message: string, state: 'started' | 'completed' | 'failed', owner = selected.value, isResearch = false
): void {
  const sites = owner.researchSites;
  clearToolStatus(owner);
  owner.toolState = state;
  owner.toolMessage = message;
  owner.researchActive = isResearch;
  owner.researchSites = sites;
  if (state !== 'started') {
    toolStatusTimers.set(owner, window.setTimeout(() => {
      const observed = owner.researchSites;
      clearToolStatus(owner);
      owner.researchSites = observed;
    }, state === 'completed' ? 1500 : 2400));
  }
}

function rememberResearchJobs(id: string, snapshots: readonly ResearchJob[]): void {
  if (disposed || deletedConversations.has(id)) return;
  const previous = researchByConversation.get(id) ?? [];
  if (!previous.length && !snapshots.length) return;
  const jobs = new Map(previous.map(job => [job.id, job]));
  const owner = contexts.get(id);
  const hasMessage = (messageId: string): boolean => owner?.messages.some(message =>
    (message.serverId ?? message.id) === messageId) ?? false;
  let reconcile = false;
  for (const snapshot of snapshots) {
    if (!snapshot || snapshot.conversationId !== id) continue;
    const prior = jobs.get(snapshot.id);
    if (prior && (Date.parse(prior.updatedAt) > Date.parse(snapshot.updatedAt) ||
      !isResearchPending(prior) && isResearchPending(snapshot))) continue;
    jobs.set(snapshot.id, snapshot);
    // A locally accepted typed message gets its canonical user ID from the saved conversation.
    if (owner && ((!prior && !hasMessage(snapshot.userMessageId)) ||
      (snapshot.state === 'completed' && prior?.state !== 'completed' &&
        snapshot.resultMessageId && !hasMessage(snapshot.resultMessageId)))) reconcile = true;
  }
  const ordered = [...jobs.values()].sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt));
  researchByConversation.set(id, ordered.slice(0, 20));
  if (reconcile) conversationChanged(id, false);
  scheduleResearchRefresh();
}

async function refreshResearchJobs(id: string, ensureFresh = false): Promise<void> {
  if (disposed || deletedConversations.has(id) || document.visibilityState !== 'visible' || !navigator.onLine) return;
  const pending = pendingResearchReads.get(id);
  if (pending) {
    // A GET issued before the completed turn may not include its newly delegated job.
    if (ensureFresh) pending.refreshRequested = true;
    return;
  }
  const controller = new AbortController();
  const read = { controller, refreshRequested: false };
  pendingResearchReads.set(id, read);
  try {
    const jobs = await listResearchJobs(id, controller.signal);
    if (!controller.signal.aborted && Array.isArray(jobs)) rememberResearchJobs(id, jobs);
  } catch { /* Keep the most recent status on disconnect and retry while jobs are active. */ }
  finally {
    pendingResearchReads.delete(id);
    if (read.refreshRequested && !controller.signal.aborted) void refreshResearchJobs(id);
    scheduleResearchRefresh();
  }
}

function scheduleResearchRefresh(): void {
  clearTimeout(researchTimer);
  if (disposed || document.visibilityState !== 'visible' || !navigator.onLine) return;
  const running = [...researchByConversation].filter(([, jobs]) => jobs.some(isResearchPending));
  if (!running.length) return;
  researchTimer = setTimeout(async () => {
    await Promise.allSettled(running.map(([id]) => refreshResearchJobs(id)));
    scheduleResearchRefresh();
  }, 2300);
}

function researchForeground(): void {
  if (conversationId.value) void refreshResearchJobs(conversationId.value);
  scheduleResearchRefresh();
}

watch(conversationId, researchForeground);

async function stopBackgroundResearch(job: ResearchJob): Promise<void> {
  if (cancellingResearch.has(job.id)) return;
  cancellingResearch.add(job.id);
  try {
    rememberResearchJobs(job.conversationId, [await cancelResearchJob(job.id)]);
  } catch {
    const owner = contexts.get(job.conversationId);
    if (owner) owner.error = 'Não foi possível cancelar a pesquisa. Tente novamente.';
  } finally {
    cancellingResearch.delete(job.id);
  }
}

function scrollToLatest(protectUserScroll = false): void {
  const owner = selected.value;
  void nextTick(() => {
    const container = messagesContainer.value;
    if (!container || selected.value !== owner || !owner.following) return;
    // A scrollbar/assistive upward movement can precede the scroll event while
    // the continuously revealing text changes height. Do not overwrite it in
    // this animation frame. A growing tail cannot itself move scrollTop up.
    if (protectUserScroll && measuredScrollOwner === owner && container.clientHeight === lastViewportHeight && container.scrollHeight >= lastScrollHeight &&
        container.scrollTop < lastScrollTop - 48) {
      owner.following = false;
      owner.scrollTop = container.scrollTop;
      programmaticScrollTop = null;
      return;
    }
    // Scroll only this region. scrollIntoView also moved the host/viewport and
    // restarted smooth scrolling on every chunk and loading-state transition.
    container.scrollTop = container.scrollHeight;
    programmaticScrollTop = container.scrollTop;
    measuredScrollOwner = owner;
    lastScrollTop = container.scrollTop;
    lastScrollHeight = container.scrollHeight;
    lastViewportHeight = container.clientHeight;
    owner.scrollTop = container.scrollTop;
  });
}

function scrollToLatestDuringStream(): void {
  if (!selected.value.following) return;
  if (streamScrollFrame !== null) {
    return;
  }

  streamScrollFrame = requestAnimationFrame(() => {
    streamScrollFrame = null;
    scrollToLatest(true);
  });
}

function suspendFollowing(): void {
  selected.value.following = false;
  noteScrollIntent(-1);
  programmaticScrollTop = null;
}
function noteScrollIntent(direction: number): void {
  manualScroll = true;
  manualDirection = direction;
  manualScrollUntil = performance.now() + 1500;
}
function handleMessagesWheel(event: WheelEvent): void {
  if (event.deltaY < 0) suspendFollowing();
  else if (event.deltaY > 0) noteScrollIntent(1);
}
function handleMessagesTouchStart(event: TouchEvent): void {
  touchY = event.touches[0]?.clientY ?? null;
  noteScrollIntent(0);
}
function handleMessagesTouchMove(event: TouchEvent): void {
  const y = event.touches[0]?.clientY ?? null;
  if (y !== null && touchY !== null && y > touchY) suspendFollowing();
  else if (y !== null && touchY !== null && y < touchY) noteScrollIntent(1);
  touchY = y;
}
function handleMessagesKey(event: KeyboardEvent): void {
  if (event.target instanceof Element && event.target.closest('textarea, input, button, [contenteditable]')) return;
  if (['ArrowUp', 'PageUp', 'Home'].includes(event.key) || (event.key === ' ' && event.shiftKey)) suspendFollowing();
  else if (['ArrowDown', 'PageDown', 'End', ' '].includes(event.key)) noteScrollIntent(1);
}
function handleMessagesScroll(): void {
  const container = messagesContainer.value;
  if (!container) return;
  const top = container.scrollTop;
  const nearEnd = container.scrollHeight - top - container.clientHeight <= 48;
  const ownScroll = programmaticScrollTop !== null && Math.abs(top - programmaticScrollTop) <= 1;
  const recentInput = manualScroll && performance.now() <= manualScrollUntil;
  // Scrollbar dragging and assistive navigation can scroll without DOM input
  // events. Stable geometry distinguishes them from reflow/keyboard resizing.
  const stableGeometry = measuredScrollOwner === selected.value && container.scrollHeight === lastScrollHeight && container.clientHeight === lastViewportHeight;
  const movingUp = top < lastScrollTop - 1;
  const upwardDuringGrowth = measuredScrollOwner === selected.value && top < lastScrollTop - 48 && container.clientHeight === lastViewportHeight && container.scrollHeight >= lastScrollHeight;
  const userScroll = recentInput || stableGeometry || upwardDuringGrowth;
  if (!ownScroll && userScroll && movingUp) selected.value.following = false;
  if (nearEnd && !ownScroll && userScroll && !movingUp && (recentInput ? manualDirection >= 0 : top > lastScrollTop + 1)) selected.value.following = true;
  measuredScrollOwner = selected.value;
  lastScrollTop = top;
  lastScrollHeight = container.scrollHeight;
  lastViewportHeight = container.clientHeight;
  selected.value.scrollTop = top;
  programmaticScrollTop = null;
}

function restoreReadingPosition(owner: ConversationContext): void {
  manualScroll = false;
  void nextTick(() => {
    const container = messagesContainer.value;
    if (!container || selected.value !== owner) return;
    if (owner.following || owner.scrollTop === null) scrollToLatest();
    else {
      container.scrollTop = owner.scrollTop;
      measuredScrollOwner = owner;
      lastScrollTop = container.scrollTop;
      lastScrollHeight = container.scrollHeight;
      lastViewportHeight = container.clientHeight;
      programmaticScrollTop = container.scrollTop;
    }
  });
}

function handleMessagesLayout(): void {
  if (selected.value.following) { scrollToLatestDuringStream(); return; }
  const container = messagesContainer.value;
  if (!container) return;
  measuredScrollOwner = selected.value;
  lastScrollHeight = container.scrollHeight;
  lastViewportHeight = container.clientHeight;
  lastScrollTop = container.scrollTop;
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

function focusComposer(explicit = false): void {
  if (nativeComposerActive.value || isRestoring.value || (!explicit && (window.matchMedia('(pointer: coarse)').matches || clientEnvironment().processSessionId))) return;

  const owner = selected.value;
  // On touch, keep the immediate focus in the user activation when the existing
  // textarea is available; nextTick also covers the recording UI disappearing.
  if (explicit) composerInput.value?.focus({ preventScroll: true });
  void nextTick(() => {
    if (selected.value !== owner || (isSidebarOpen.value && sidebarDrawerQuery.matches)) return;
    composerInput.value?.focus({ preventScroll: true });
    if (explicit) composerInput.value?.setSelectionRange(draft.value.length, draft.value.length);
  });
}

function cancelRecording(): void {
  capture = null;
  transcription.discard();
  resizeComposer();
}

function insertTranscriptIntoDraft(transcript: string, intent: TranscriptionIntent): void {
  const text = transcript.trim();
  const session = capture;
  capture = null;
  if (!text || !session || selected.value !== session.owner || draft.value !== session.text) return;

  const current = session.text;
  const { start, end } = session;
  const before = current.slice(0, start);
  const after = current.slice(end);
  const leftSeparator = before && !/\s$/u.test(before) ? ' ' : '';
  const rightSeparator = after && !/^\s/u.test(after) ? ' ' : '';
  const insertion = `${leftSeparator}${text}${rightSeparator}`;
  draft.value = `${before}${insertion}${after}`;

  if (intent === 'send') {
    // The same submit path owns acceptance, idempotency and recoverable drafts.
    void handleSubmit();
    return;
  }

  void nextTick(() => {
    if (selected.value !== session.owner) return;
    const nextCursor = before.length + insertion.length;
    if (!hasNavigationLayer() && !document.querySelector('dialog[open]') && !(isSidebarOpen.value && sidebarDrawerQuery.matches)) {
      composerInput.value?.focus({ preventScroll: true });
    }
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

function stageFiles(owner: ConversationContext, files: DraftAttachment[]): void {
  if (!files.length) return;
  try {
    checkAttachmentLimits([...owner.files, ...files]);
    owner.files.push(...files);
    owner.error = null;
  } catch (error) {
    files.forEach(file => releaseAttachment(file, true));
    owner.error = error instanceof Error ? error.message : 'Não foi possível anexar os arquivos.';
  }
}

function pasteFiles(event: ClipboardEvent): void {
  const clipboard = event.clipboardData;
  const files = Array.from(clipboard?.files ?? [], pickedFile);
  const link = files.length === 0 ? draftLink(clipboard?.getData('text/plain') ?? '') : null;
  if (!files.length && !link) return;
  event.preventDefault();
  stageFiles(selected.value, link ? [link] : files);
}

function dragFiles(event: DragEvent): void {
  if (!event.dataTransfer?.types.includes('Files')) return;
  event.preventDefault();
  if (event.type === 'drop') stageFiles(selected.value, Array.from(event.dataTransfer.files, pickedFile));
}

let receivingShare = false;
let shareRequested = false;
async function receiveSharedFiles(): Promise<void> {
  const take = clientEnvironment().takeSharedAttachments;
  if (!take || disposed) return;
  shareRequested = true;
  if (receivingShare || isRestoring.value) return;
  receivingShare = true;
  try {
    while (shareRequested && !disposed) {
      shareRequested = false;
      const owner = selected.value;
      try {
        const result = await take();
        if (!result) return;
        if (disposed) { result.files.forEach(file => releaseAttachment(sharedAttachmentFile(file))); return; }
        const attachments: DraftAttachment[] = result.files.map(sharedAttachmentFile);
        const sharedText: string[] = [];
        for (const text of result.texts ?? []) {
          const draftLines: string[] = [];
          for (const line of text.split(/\r?\n/)) {
            const link = draftLink(line);
            if (link) attachments.push(link);
            else draftLines.push(line);
          }
          const remaining = draftLines.join('\n').trim();
          if (remaining) sharedText.push(remaining);
        }
        stageFiles(owner, attachments);
        if (sharedText.length) owner.draft = [owner.draft, ...sharedText].filter(Boolean).join('\n');
        if (result.error) owner.error = result.error;
        if (selected.value === owner && (attachments.length || sharedText.length || result.error)) {
          closeSidebarDrawer();
          resizeComposer();
          window.dispatchEvent(new Event('aegis-show-shared-draft'));
        }
      } catch { if (!disposed) owner.error = 'Não foi possível ler o compartilhamento. Tente compartilhar novamente.'; }
    }
  } finally { receivingShare = false; }
}

function createLocalUserMessage(content: string, files: DraftAttachment[]): LocalChatMessage {
  // Transport stores chunked files first and links after them. Match that history order
  // when reconciling the provisional message, even if the draft interleaved both kinds.
  const attachments = [...files.filter(file => file.kind === 'file'), ...files.filter(file => file.kind === 'link')];
  return reactive({
    id: createLocalMessageId(),
    conversationId: conversationId.value ?? 'pending',
    role: 'user',
    content,
    attachments: attachments.map(file => ({ id: createLocalMessageId(), fileName: file.name, contentType: file.type,
      sizeBytes: file.size, linkUrl: file.kind === 'link' ? file.url : undefined })),
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

// Network reception and visible text are deliberately independent. One bounded
// timer per actively revealing message coalesces bursts into real text prefixes.
type TextPresentation = {
  owner: ConversationContext; message: LocalChatMessage; reveal: StreamTextReveal;
  timer?: ReturnType<typeof setTimeout>; complete: boolean; floor: string; onSettled?: () => void;
};
const textPresentations = new Map<LocalChatMessage, TextPresentation>();
const reducedStreamMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
function createPresentation(owner: ConversationContext, message: LocalChatMessage, floor = '', onSettled?: () => void): TextPresentation {
  const presentation: TextPresentation = { owner, message, reveal: new StreamTextReveal(), complete: false, floor, onSettled };
  textPresentations.set(message, presentation);
  return presentation;
}
function renderPresentation(p: TextPresentation, text: string): void {
  // A snapshot that reached the screen before the local stream must never rewind.
  if (p.floor && p.floor.startsWith(text) && text.length < p.floor.length) return;
  p.floor = '';
  if (p.message.content !== text) {
    p.message.content = text;
    if (selected.value === p.owner) scrollToLatestDuringStream();
  }
}
function settlePresentation(p: TextPresentation, flush = false): void {
  if (textPresentations.get(p.message) !== p) return;
  clearTimeout(p.timer); p.timer = undefined;
  if (flush) renderPresentation(p, p.reveal.finish());
  p.message.streaming = false;
  textPresentations.delete(p.message);
  if (!disposed) p.onSettled?.();
}
function pumpPresentation(p: TextPresentation): void {
  p.timer = undefined;
  if (disposed || textPresentations.get(p.message) !== p) return;
  if (!p.owner.messages.includes(p.message)) { settlePresentation(p); return; }
  const immediate = reducedStreamMotion.matches || document.visibilityState !== 'visible' || selected.value !== p.owner;
  renderPresentation(p, immediate ? p.reveal.finish() : p.reveal.step(performance.now()));
  if (p.reveal.pending) p.timer = setTimeout(() => pumpPresentation(p), STREAM_FRAME_MS);
  else if (p.complete) settlePresentation(p);
}
function appendPresentation(p: TextPresentation, text: string): void {
  p.reveal.append(text, performance.now());
  if (p.timer !== undefined) return;
  if (reducedStreamMotion.matches || document.visibilityState !== 'visible' || selected.value !== p.owner) pumpPresentation(p);
  else p.timer = setTimeout(() => pumpPresentation(p), STREAM_FRAME_MS);
}
function completePresentation(p: TextPresentation): void {
  p.complete = true;
  if (!p.reveal.pending) settlePresentation(p);
  else if (p.timer === undefined) pumpPresentation(p);
}
function flushPresentationOnHidden(): void {
  if (document.visibilityState === 'visible') return;
  nativeResearch.value = null;
  for (const p of textPresentations.values()) { clearTimeout(p.timer); pumpPresentation(p); }
}

// Native Windows hands-free sends only committed segments of an accepted turn.
// Existing server snapshots still own history/metadata; the streaming marker
// prevents a full snapshot from jumping ahead of this message's presentation.
type NativeVoiceTurn = {
  protocol: NativeVoiceSegments; owner: ConversationContext; presentation?: TextPresentation; generationDone: boolean;
  researchSites: Map<string, number>;
};
const nativeVoiceTurns = new Map<string, NativeVoiceTurn>();
const nativeResearch = ref<{ turnId: string; conversationId: string; status: NativeVoiceToolStatus; sites: ReturnType<typeof topResearchSites> } | null>(null);
const selectedNativeResearch = computed(() => nativeResearch.value?.conversationId === selected.value.id ? nativeResearch.value : null);
let nativeResearchTimer: ReturnType<typeof setTimeout> | undefined;
const closedNativeVoiceTurns = new Set<string>();
function rememberClosedNativeTurn(id: string): void {
  closedNativeVoiceTurns.add(id);
  if (closedNativeVoiceTurns.size > 64) closedNativeVoiceTurns.delete(closedNativeVoiceTurns.values().next().value!);
}
function finishNativeVoiceTurn(turnId: string, interrupted = false, reconcile = true): void {
  const turn = nativeVoiceTurns.get(turnId);
  if (nativeResearch.value?.turnId === turnId) {
    clearTimeout(nativeResearchTimer);
    if (!interrupted && turn?.generationDone && nativeResearch.value.status.category === 'web_search' && nativeResearch.value.sites.length &&
      nativeResearch.value.status.state !== 'failed') {
      nativeResearch.value = { ...nativeResearch.value, status: { category: 'web_search', state: 'completed', message: 'Pesquisa concluída.' } };
      nativeResearchTimer = setTimeout(() => {
        if (nativeResearch.value?.turnId === turnId) nativeResearch.value = null;
      }, 1500);
    } else nativeResearch.value = null;
  }
  if (!turn) { rememberClosedNativeTurn(turnId); return; }
  nativeVoiceTurns.delete(turnId); rememberClosedNativeTurn(turnId);
  if (turn.presentation) {
    if (!turn.generationDone || interrupted) {
      // Commit boundaries, not the displayed prefix, are authoritative at interruption.
      settlePresentation(turn.presentation, true);
      if (!turn.generationDone && interrupted) turn.presentation.message.interrupted = true;
    } else completePresentation(turn.presentation);
  }
  if (reconcile) conversationChanged(turn.protocol.conversationId, false, turn.generationDone);
}
function acceptNativeVoiceStream(payload: unknown): void {
  const event = parseNativeVoiceStreamEvent(payload);
  if (!event || disposed) return;
  const conversation = event.conversationId;
  if (conversation && deletedConversations.has(conversation)) return;
  if (closedNativeVoiceTurns.has(event.turnId)) {
    if (conversation && (event.type === 'done' || event.type === 'error' || event.type === 'interrupted')) conversationChanged(conversation, false);
    return;
  }
  let turn = nativeVoiceTurns.get(event.turnId);
  if (!turn && conversation && (event.type === 'conversation' || event.type === 'final_segment' && event.sequence === 0)) {
    // One node has at most one live microphone interaction. Complete old display
    // tails without allowing a late event to attach to the new turn.
    for (const id of [...nativeVoiceTurns.keys()]) finishNativeVoiceTurn(id, true);
    const owner = contexts.get(conversation) ?? createConversationContext(conversation);
    if (!contexts.has(conversation)) contexts.set(conversation, owner);
    turn = { owner, protocol: new NativeVoiceSegments(event.turnId, conversation), generationDone: false, researchSites: new Map() };
    nativeVoiceTurns.set(event.turnId, turn);
    conversationChanged(conversation, false);
  }
  if (!turn) {
    if (event.type === 'tool_status' || event.type === 'web_research_sources') return; // Progress cannot establish a turn.
    if (conversation) conversationChanged(conversation, false);
    if (event.type !== 'conversation' && event.type !== 'final_segment') rememberClosedNativeTurn(event.turnId);
    return;
  }
  if (!turn.protocol.matches(event)) return;
  if (event.type === 'conversation') return;
  if (event.type === 'web_research_sources') {
    turn.researchSites = mergeResearchSites(turn.researchSites, event.sources ?? []);
    if (nativeResearch.value?.turnId === event.turnId)
      nativeResearch.value = { ...nativeResearch.value, sites: topResearchSites(turn.researchSites) };
    return;
  }
  if (event.type === 'tool_status') {
    if (event.toolStatus && conversation) {
      nativeResearch.value = { turnId: event.turnId, conversationId: conversation, status: event.toolStatus, sites: topResearchSites(turn.researchSites) };
      if (selected.value === turn.owner) scrollToLatestDuringStream();
    }
    return;
  }
  if (event.type === 'final_segment') {
    const text = turn.protocol.append(event);
    if (text === null || !event.assistantMessageId) { finishNativeVoiceTurn(event.turnId, true); return; }
    if (!text) return; // A byte-identical duplicate event is idempotent.
    if (!turn.presentation) {
      const existing = turn.owner.messages.find(message => (message.serverId ?? message.id) === event.assistantMessageId);
      if (existing && existing.role !== 'assistant') { finishNativeVoiceTurn(event.turnId, true); return; }
      const message: LocalChatMessage = existing ?? reactive({ id: event.assistantMessageId, serverId: event.assistantMessageId,
        conversationId: turn.protocol.conversationId, role: 'assistant', content: '', createdAt: new Date().toISOString(), pending: false, streaming: true });
      message.streaming = true;
      if (!existing) turn.owner.messages.push(message);
      const id = turn.protocol.conversationId;
      turn.presentation = createPresentation(turn.owner, message, existing?.content ?? '', () => {
        // Once the local animation no longer protects content, refresh metadata
        // from the authoritative saved row (no additional polling).
        if (contexts.has(id) && !deletedConversations.has(id)) conversationChanged(id, false);
      });
    }
    appendPresentation(turn.presentation, text);
  } else if (event.type === 'done') {
    turn.generationDone = true;
    finishNativeVoiceTurn(event.turnId);
  } else finishNativeVoiceTurn(event.turnId, event.type !== 'ignored');
}
function clearNativeVoiceStreams(reconcile = false): void {
  for (const id of [...nativeVoiceTurns.keys()]) finishNativeVoiceTurn(id, true, reconcile);
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
  if (conversationId.value) await openConversation(conversationId.value, true);
}

let summaryRevision = 0;
const summaryRevisions = new Map<string, number>();
function mergeConversationSummary(summary: ConversationSummary): void {
  if (deletedConversations.has(summary.id)) return;
  summaryRevisions.set(summary.id, ++summaryRevision);
  const context = contexts.get(summary.id);
  if (context) { context.title = summary.title ?? 'Nova conversa'; context.createdAt = summary.createdAt; }
  if (summary.id === conversationId.value) activeConversationCreatedAt.value = summary.createdAt;
  const withoutCurrent = conversations.value.filter((conversation) => conversation.id !== summary.id);
  conversations.value = [summary, ...withoutCurrent].sort((first, second) => {
    const dateDifference = new Date(second.updatedAt).getTime() - new Date(first.updatedAt).getTime();
    return dateDifference || second.id.localeCompare(first.id);
  });
}

function applyRemoteConversationDeletion(id: string): void {
  researchByConversation.delete(id);
  pendingResearchReads.get(id)?.controller.abort();
  for (const [turnId, turn] of nativeVoiceTurns) if (turn.protocol.conversationId === id) finishNativeVoiceTurn(turnId, true, false);
  deletedConversations.add(id);
  summaryRevisions.set(id, ++summaryRevision);
  const owner = contexts.get(id);
  contexts.delete(id);
  conversations.value = conversations.value.filter(row => row.id !== id);
  let recovery: ConversationContext | undefined;
  if (owner && hasDraft(owner)) {
    recovery = createConversationContext();
    recovery.draft = owner.draft;
    recovery.files = owner.files;
    unsentConversations.value = [...unsentConversations.value.filter(context => context !== owner), recovery];
  }
  if (conversationId.value === id) {
    startNewConversation(false);
    if (recovery) {
      selected.value = recovery;
      resizeComposer();
      restoreReadingPosition(recovery);
    }
  }
  if (infoTarget.value?.id === id) infoTarget.value = null;
  if (renameTarget.value?.id === id) renameTarget.value = null;
  if (deleteTarget.value?.id === id) deleteTarget.value = null;
}

// Invalidation never navigates to an unrelated chat or replaces a live turn.
const conversationSync = new ConversationSyncQueue<{ snapshot: Conversation | null; revision: number; summaryRevision: number }>(async id => {
  const revision = contexts.get(id)?.revision ?? 0;
  const summaryRevision = summaryRevisions.get(id) ?? 0;
  try {
    return { snapshot: await getConversation(id), revision, summaryRevision };
  } catch (error) {
    if (error instanceof AegisApiError && error.status === 404) return { snapshot: null, revision, summaryRevision };
    throw error;
  }
}, (id, { snapshot, revision, summaryRevision }) => {
  if (disposed || deletedConversations.has(id)) return true;
  const owner = contexts.get(id);
  if ((owner && owner.revision !== revision) || (summaryRevisions.get(id) ?? 0) !== summaryRevision) return false;
  if (!snapshot) { applyRemoteConversationDeletion(id); return true; }
  mergeConversationSummary({ ...conversations.value.find(row => row.id === id), id,
    title: snapshot.title, titleSource: snapshot.titleSource, createdAt: snapshot.createdAt,
    updatedAt: snapshot.updatedAt, messageCount: snapshot.messages.length,
    lastMessagePreview: snapshot.messages.at(-1)?.content ?? null });
  if (owner) {
    owner.revision++;
    owner.messages = mergeConversationMessages(owner.messages, snapshot.messages);
    owner.remoteUpdatedAt = snapshot.updatedAt;
    owner.remoteMessageCount = snapshot.messages.length;
    owner.loaded = true;
    if (owner === selected.value) { resumeMemoryActivityPolling(); void nextTick(observeProactiveMessages); }
  }
  return true;
}, id => disposed || [...activeTurns.values()].some(turn => turn.owner.id === id), schedulePushRecovery);
let conversationSyncTimer: ReturnType<typeof setTimeout> | undefined;
const conversationRetryTimers = new Map<string, ReturnType<typeof setTimeout>>();
function flushConversationChanges(): void { void conversationSync.flush(); }
function resetConversationChanges(): void {
  clearNativeVoiceStreams();
  conversationSync.reset(); clearTimeout(conversationSyncTimer); clearTimeout(pushRecoveryTimer); pushRecoveryTimer = undefined;
  for (const timer of conversationRetryTimers.values()) clearTimeout(timer);
  conversationRetryTimers.clear();
}
function conversationChanged(id: string, awaitTitle = true, refreshAfterTurn = false): void {
  if (deletedConversations.has(id)) return;
  if (refreshAfterTurn || conversationId.value === id || researchByConversation.has(id)) void refreshResearchJobs(id, refreshAfterTurn);
  conversationSync.invalidate(id);
  clearTimeout(conversationSyncTimer);
  conversationSyncTimer = setTimeout(flushConversationChanges, 120);
  // Legacy/native local invalidations need bounded title reconciliation; a server
  // push announces the title commit separately, so retry only failed reads.
  const previous = conversationRetryTimers.get(id);
  if (previous !== undefined) clearTimeout(previous);
  conversationRetryTimers.delete(id);
  if (!awaitTitle) return;
  const retry = (attempt: number): void => {
    conversationRetryTimers.set(id, setTimeout(async () => {
      conversationRetryTimers.delete(id);
      if (disposed || deletedConversations.has(id)) return;
      conversationSync.invalidate(id); await conversationSync.flush();
      const summary = conversations.value.find(row => row.id === id);
      if (attempt < 3 && (conversationSync.pending(id) || !summary || summary.titleSource === 'default' || !summary.title || summary.title === 'Nova conversa')) retry(attempt + 1);
    }, attempt === 0 ? 1800 : 3400));
  };
  retry(0);
}
async function reconcileConversations(allKnown = false): Promise<void> {
  if (disposed || document.visibilityState !== 'visible') return;
  if (allKnown) for (const [id, owner] of contexts) {
    if (owner.loaded && !deletedConversations.has(id)) conversationSync.invalidate(id);
  }
  if (selected.value.id && !conversationSync.pending(selected.value.id)) conversationSync.invalidate(selected.value.id);
  await Promise.allSettled([refreshConversationHead(allKnown), conversationSync.flush()]);
  schedulePushRecovery();
}
watch(() => [...activeTurns.values()].map(turn => turn.owner.id).filter((id): id is string => !!id), (current, previous) => {
  for (const id of new Set([...current, ...previous])) if (conversationSync.pending(id)) conversationSync.invalidate(id);
  flushConversationChanges();
});

let conversationHeadRead: Promise<void> | undefined;
let conversationHeadAbort: AbortController | undefined;
let conversationHeadTimer: ReturnType<typeof setTimeout> | undefined;
let requestedReconciliation = 0;
let completedReconciliation = 0;
let pushRecoveryTimer: ReturnType<typeof setTimeout> | undefined;
let pushRecoveryDelay = 1000;

function summaryUnchanged(previous: ConversationSummary | undefined, incoming: ConversationSummary): boolean {
  return previous !== undefined && previous.title === incoming.title && previous.titleSource === incoming.titleSource &&
    previous.createdAt === incoming.createdAt && previous.updatedAt === incoming.updatedAt &&
    previous.messageCount === incoming.messageCount && previous.lastMessagePreview === incoming.lastMessagePreview &&
    previous.hasUnreadProactive === incoming.hasUnreadProactive;
}

function refreshConversationHead(allKnown = false): Promise<void> {
  if (disposed) return Promise.resolve();
  if (allKnown) requestedReconciliation++;
  if (conversationHeadRead) return conversationHeadRead;
  const reconcileRevision = requestedReconciliation;
  const controller = new AbortController();
  conversationHeadAbort = controller;
  const timeout = setTimeout(() => controller.abort(), 8000);
  const revision = summaryRevision;
  conversationHeadRead = (async () => {
    const page = await getConversations(30, null, controller.signal);
    if (disposed || controller.signal.aborted) return;
    const previous = new Map(conversations.value.map(summary => [summary.id, summary]));
    // Retain loaded pages. If an entirely new head displaced every known row,
    // restart pagination from that head so the intervening rows remain reachable.
    if (previous.size === 0 || (page.hasMore && !page.items.some(row => previous.has(row.id)))) {
      nextHistoryCursor.value = page.nextCursor ?? null;
      hasMoreHistory.value = page.hasMore;
    }
    for (const summary of page.items) {
      if (deletedConversations.has(summary.id) || (summaryRevisions.get(summary.id) ?? 0) > revision) continue;
      const old = previous.get(summary.id);
      if (!summaryUnchanged(old, summary)) mergeConversationSummary(summary);
      const owner = contexts.get(summary.id);
      const messageCountChanged = old?.messageCount !== summary.messageCount;
      // Only fetch a detail snapshot when a loaded context is behind the server.
      // The existing queue defers this work during a local streaming turn.
      if (owner?.loaded && (owner.remoteUpdatedAt !== summary.updatedAt ||
          (messageCountChanged && owner.remoteMessageCount !== summary.messageCount)) &&
          !conversationSync.pending(summary.id)) conversationSync.invalidate(summary.id);
    }
    if (reconcileRevision > completedReconciliation) {
      completedReconciliation = reconcileRevision;
      const incoming = new Set(page.items.map(row => row.id));
      // Reconnection recovered the recent head; reconcile older rows already
      // loaded in the sidebar too, including deletions missed while disconnected.
      for (const id of previous.keys()) if (!incoming.has(id) && !deletedConversations.has(id) && !conversationSync.pending(id))
        conversationSync.invalidate(id);
    }
    flushConversationChanges();
  })().finally(() => {
    clearTimeout(timeout);
    conversationHeadRead = undefined;
    if (conversationHeadAbort === controller) conversationHeadAbort = undefined;
    schedulePushRecovery();
  });
  return conversationHeadRead;
}

// Retry failed invalidations only. No timer remains once the push channel's
// outstanding work is reconciled, even if a GET failed while the socket stayed online.
function schedulePushRecovery(): void {
  const headDirty = requestedReconciliation > completedReconciliation;
  const active = props.conversationPushActive && !disposed && document.visibilityState === 'visible' && navigator.onLine;
  const needsRetry = active && ((headDirty && !conversationHeadRead) || conversationSync.retryable());
  if (!needsRetry) {
    clearTimeout(pushRecoveryTimer);
    pushRecoveryTimer = undefined;
    if (!active || (!headDirty && !conversationSync.hasPending())) pushRecoveryDelay = 1000;
    return;
  }
  if (pushRecoveryTimer !== undefined) return;
  pushRecoveryTimer = setTimeout(async () => {
    pushRecoveryTimer = undefined;
    if (disposed || !props.conversationPushActive || document.visibilityState !== 'visible' || !navigator.onLine) { schedulePushRecovery(); return; }
    pushRecoveryDelay = Math.min(pushRecoveryDelay * 2, 30000);
    await Promise.allSettled([
      requestedReconciliation > completedReconciliation ? refreshConversationHead() : Promise.resolve(),
      conversationSync.flush()
    ]);
    schedulePushRecovery();
  }, pushRecoveryDelay);
}

// Native Android writes already emit a targeted local invalidation. Poll less
// frequently there, and only while its Activity is actually in the foreground.
function scheduleConversationHeadSync(immediate = false): void {
  clearTimeout(conversationHeadTimer);
  if (disposed || document.visibilityState !== 'visible' || !navigator.onLine) return;
  if (props.conversationPushActive) {
    if (immediate) void reconcileConversations(true);
    return;
  }
  const foreground = clientEnvironment().isForeground;
  conversationHeadTimer = setTimeout(async () => {
    if (disposed || document.visibilityState !== 'visible' || props.conversationPushActive) return;
    try {
      if (!foreground || await foreground()) await refreshConversationHead();
    } catch { /* The next bounded poll or reconnect retries. */ }
    scheduleConversationHeadSync();
  }, immediate ? 0 : foreground ? 15000 : 5000);
}

function conversationHeadForeground(): void { scheduleConversationHeadSync(true); schedulePushRecovery(); }
watch(() => props.conversationPushActive, active => {
  if (active) {
    for (const timer of historyRefreshTimers.values()) clearTimeout(timer);
    historyRefreshTimers.clear();
  }
  scheduleConversationHeadSync(true);
  schedulePushRecovery();
});

async function loadConversationHistory(reset = false): Promise<void> {
  if (isLoadingHistory.value) {
    if (reset) historyReloadRequested = true;
    return;
  }

  if (!reset && !hasMoreHistory.value) {
    return;
  }

  isLoadingHistory.value = true;
  historyErrorMessage.value = null;

  try {
    const revision = summaryRevision;
    const page = await getConversations(30, reset ? null : nextHistoryCursor.value);
    if (disposed) return;
    const updatedDuringRead = conversations.value.filter(row => (summaryRevisions.get(row.id) ?? 0) > revision);
    const incoming = reset ? [...updatedDuringRead, ...page.items] : [...updatedDuringRead, ...conversations.value, ...page.items];
    const seen = new Set<string>();
    conversations.value = incoming.filter((conversation) => {
      if (seen.has(conversation.id) || deletedConversations.has(conversation.id)) {
        return false;
      }

      seen.add(conversation.id);
      return true;
    }).sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime() || b.id.localeCompare(a.id));
    for (const summary of page.items) if ((summaryRevisions.get(summary.id) ?? 0) <= revision) summaryRevisions.set(summary.id, ++summaryRevision);
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
    if (!disposed && historyReloadRequested) { historyReloadRequested = false; void loadConversationHistory(true); }
  }
}

function refreshHistoryAfterResponse(ownerId: string): void {
  if (props.conversationPushActive) return;
  const existing = historyRefreshTimers.get(ownerId);
  if (existing !== undefined) window.clearTimeout(existing);
  const refresh = async (attempt: number): Promise<void> => {
    historyRefreshTimers.delete(ownerId);
    if (disposed || deletedConversations.has(ownerId)) return;
    await loadConversationHistory(true);
    const title = conversations.value.find(c => c.id === ownerId)?.title?.trim();
    if (disposed || props.conversationPushActive || attempt >= 4 || deletedConversations.has(ownerId) || (title && title !== 'Nova conversa')) return;
    historyRefreshTimers.set(ownerId, window.setTimeout(() => { void refresh(attempt + 1); }, attempt === 0 ? 1800 : 3400));
  };
  void refresh(0);
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

async function openConversation(targetConversationId: string, restoring = false): Promise<void> {
  if (!restoring && targetConversationId === conversationId.value && !isRestoring.value) {
    closeSidebarDrawer();
    return;
  }
  dismissKeyboard();
  cancelMemoryActivityPolling();
  cancelRecording();
  stopPlayback();
  const request = ++conversationLoad;
  const previous = selected.value;
  const owner = contexts.get(targetConversationId) ?? createConversationContext(targetConversationId);
  contexts.set(targetConversationId, owner);
  selected.value = owner;
  localStorage.setItem(STORAGE_KEY, targetConversationId);
  feedbackTarget.value = null;
  feedbackErrorMessage.value = null;
  feedbackStatusByMessageId.value = {};
  closeSidebarDrawer();
  resizeComposer();

  // A history response must never replace the live messages of its own turn.
  if ([...activeTurns.values()].some(turn => turn.owner === owner)) {
    isRestoring.value = false;
    restoreReadingPosition(owner);
    return;
  }
  isRestoring.value = !owner.loaded;
  const revision = owner.revision;
  const metadataRevision = summaryRevisions.get(targetConversationId) ?? 0;
  try {
    const conversation = await getConversation(targetConversationId);
    if (request !== conversationLoad || selected.value !== owner || owner.revision !== revision) return;
    if ((summaryRevisions.get(targetConversationId) ?? 0) === metadataRevision) {
      owner.title = conversation.title ?? 'Nova conversa';
      owner.createdAt = conversation.createdAt;
    }
    owner.revision++;
    owner.messages = mergeConversationMessages(owner.messages, conversation.messages);
    owner.remoteUpdatedAt = conversation.updatedAt;
    owner.remoteMessageCount = conversation.messages.length;
    owner.loaded = true;
    resumeMemoryActivityPolling();
  } catch {
    if (request !== conversationLoad || selected.value !== owner || owner.revision !== revision) return;
    owner.error = restoring ? 'Não foi possível carregar a conversa anterior.' : 'Não foi possível abrir esta conversa.';
    if (!owner.loaded) {
      selected.value = previous !== owner ? previous : newConversation;
      selected.value.error = owner.error;
      if (selected.value.id) localStorage.setItem(STORAGE_KEY, selected.value.id);
      else localStorage.removeItem(STORAGE_KEY);
      isRestoring.value = false;
      resizeComposer();
      restoreReadingPosition(selected.value);
    }
  } finally {
    if (request === conversationLoad && selected.value === owner) {
      isRestoring.value = false;
      restoreReadingPosition(owner);
    }
  }
}

function associateTurnConversation(turn: ActiveTurn, id: string, titleSource?: string | null): void {
  const owner = turn.owner;
  owner.id = id;
  owner.createdAt ??= turn.user.createdAt;
  owner.loaded = true;
  contexts.set(id, owner);
  turn.user.conversationId = id;
  turn.user.pending = false;
  turn.assistant.conversationId = id;
  turn.accepted = true;
  owner.retry?.files.forEach(file => releaseAttachment(file));
  owner.retry = null;
  if (owner === selected.value) localStorage.setItem(STORAGE_KEY, id);
  const previous = conversations.value.find(c => c.id === id);
  mergeConversationSummary({ ...previous, titleSource: titleSource ?? previous?.titleSource, id, title: owner.title ?? previous?.title, createdAt: owner.createdAt,
    updatedAt: new Date().toISOString(), messageCount: Math.max(owner.messages.length, previous?.messageCount ?? 0),
    lastMessagePreview: turn.user.content });
}

async function handleSubmit(): Promise<void> {
  if (!canSend.value) return;
  const owner = selected.value;
  try { checkAttachmentLimits(owner.files); }
  catch (error) { owner.error = (error as Error).message; return; }
  if (activeTurns.size >= MAX_CONCURRENT_TURNS) {
    owner.error = 'Já há três conversas respondendo. Aguarde uma terminar para enviar.';
    return;
  }
  stopPlayback();
  owner.revision++;
  const content = owner.draft.trim();
  const files = owner.files.slice();
  unsentConversations.value = unsentConversations.value.filter(context => context !== owner);
  cancelMemoryActivityPolling();
  cancelRecording();
  const retry = owner.retry?.content === content && owner.retry.files.length === files.length &&
    files.every((file, index) => file === owner.retry!.files[index]) ? owner.retry : null;
  const turnId = retry?.turnId ?? crypto.randomUUID();
  const requestConversationId = retry ? retry.conversationId : owner.id;
  const localMessage = createLocalUserMessage(content, files);
  const assistantMessage = createLocalAssistantMessage();
  const controller = new AbortController();
  const turn: ActiveTurn = reactive({ id: turnId, owner, user: localMessage, assistant: assistantMessage, controller,
    accepted: false, uploading: files.some(file => file.kind === 'file'), status: 'thinking' });
  activeTurns.set(turnId, turn);
  // Consume the submitted draft, including the unsaved-conversation slot.
  if (newConversation === owner) newConversation = createConversationContext();
  owner.draft = '';
  owner.files = [];
  owner.retry = { turnId, content, files, conversationId: requestConversationId };
  resizeComposer();
  owner.error = null;
  clearToolStatus();
  if (emailConnectionState.value === 'connected') emailConnectionMessage.value = null;
  owner.messages.push(localMessage, assistantMessage);
  owner.following = true;
  scrollToLatest();
  const presentation = createPresentation(owner, assistantMessage);
  const owns = (): boolean => activeTurns.get(turnId) === turn && !controller.signal.aborted;

  try {
    const attachmentUploadIds: string[] = [];
    const attachmentLinks: string[] = [];
    for (const file of files) {
      if (file.kind === 'link') {
        attachmentLinks.push(file.url);
        continue;
      }
      attachmentUploadIds.push(await uploadAttachment(file, controller.signal, bytes => {
        if (owns()) owner.toolMessage = `Enviando ${file.name} · ${attachmentSize(bytes)} de ${attachmentSize(file.size)}`;
      }));
    }
    if (!owns()) return;
    turn.uploading = false;
    clearToolStatus(owner);
    await sendMessageStream({ turnId, conversationId: requestConversationId, content,
      attachmentUploadIds: attachmentUploadIds.length ? attachmentUploadIds : undefined,
      attachmentLinks: attachmentLinks.length ? attachmentLinks : undefined }, {
      onConversation: (eventTurnId, id) => {
        if (!owns() || eventTurnId !== turnId) return;
        associateTurnConversation(turn, id);
      },
      onToken: (eventTurnId, token) => {
        if (!owns() || eventTurnId !== turnId) return;
        turn.status = 'responding';
        appendPresentation(presentation, token);
      },
      onToolStatus: (eventTurnId, status) => {
        if (!owns() || eventTurnId !== turnId) return;
        turn.status = 'thinking';
        showToolStatus(status.message, status.state, owner, status.category === 'web_search');
      },
      onWebResearchSources: (eventTurnId, sources) => {
        if (!owns() || eventTurnId !== turnId) return;
        owner.researchSites = mergeResearchSites(owner.researchSites, sources);
        if (!owner.toolMessage) showToolStatus('Pesquisando na web…', 'started', owner, true);
      },
      onDone: ({ turnId: completedTurnId, conversationId: id, messageId, conversationTitle, titleSource, memoryActivity, webResearch, attachments }) => {
        if (!owns() || completedTurnId !== turnId) return;
        owner.title = conversationTitle ?? owner.title ?? 'Nova conversa';
        associateTurnConversation(turn, id, titleSource);
        assistantMessage.serverId = messageId;
        assistantMessage.memoryActivity = memoryActivity;
        assistantMessage.webResearch = webResearch;
        assistantMessage.attachments = attachments;
        assistantMessage.pending = false;
        completePresentation(presentation);
        activeTurns.delete(turnId);
        if (files.length) conversationChanged(id, false);
        void refreshResearchJobs(id, true);
        if (owner.toolState === 'completed' || owner.toolState === 'failed') {
          showToolStatus(owner.toolMessage ?? '', owner.toolState, owner, owner.researchActive);
        } else if (owner.researchActive && owner.researchSites.size) {
          showToolStatus('Pesquisa concluída.', 'completed', owner, true);
        } else clearToolStatus(owner);
        refreshHistoryAfterResponse(id);
        if (selected.value === owner) {
          scrollToLatest();
          startMemoryActivityPolling(assistantMessage);
        }
        // Background completions remain silent and never reclaim navigation/focus.
        if (voice.autoSpeak.value && selected.value === owner && document.visibilityState === 'visible' && !activeSpeechPlaybackId) void playSpeech(turnId, messageId);
        else void completeTurnWithoutSpeech(turnId).catch(() => undefined);
      },
      onError: message => { if (owns()) owner.error = message; }
    }, controller.signal);
  } catch (error) {
    if (!owns()) return;
    // Confirm transport failures with the health endpoint; model/tool errors and
    // failed operations do not establish that the server is unavailable.
    if (error instanceof TypeError) void checkServerHealth(true);
    owner.error ??= error instanceof TypeError
      ? 'Servidor indisponível. Tente novamente em instantes.'
      : error instanceof Error ? error.message : 'Não foi possível enviar a mensagem.';
    settlePresentation(presentation, true);
    if (!turn.accepted) {
      // No acceptance acknowledgement: retain the prompt for a manual retry,
      // with the same turn ID. Never automatically resend a possibly accepted turn.
      owner.draft = owner.draft ? `${content}\n${owner.draft}` : content;
      owner.files = [...files, ...owner.files];
      owner.messages = owner.messages.filter(message => message !== localMessage && message !== assistantMessage);
      if (!owner.id && !unsentConversations.value.includes(owner)) unsentConversations.value.push(owner);
      if (selected.value === owner) resizeComposer();
    } else {
      localMessage.pending = false;
      assistantMessage.pending = false;
      assistantMessage.streaming = false;
      if (!presentation.reveal.content) owner.messages = owner.messages.filter(message => message !== assistantMessage);
    }
  } finally {
    if (activeTurns.get(turnId) === turn) {
      activeTurns.delete(turnId);
      clearToolStatus(owner);
    }
  }
}

async function stopActiveTurn(reason = 'user_stop', turn = selectedTurn.value): Promise<void> {
  if (!turn) return;
  // Invalidate only this turn before abort/cancel crosses an async boundary.
  activeTurns.delete(turn.id);
  clearToolStatus(turn.owner);
  turn.controller.abort();
  if (activeSpeechOwner === turn.owner) stopPlayback();
  const presentation = textPresentations.get(turn.assistant);
  if (presentation) settlePresentation(presentation, true);
  if (turn.uploading && turn.owner.retry) {
    const { files, content } = turn.owner.retry;
    const owner = turn.owner;
    owner.messages = owner.messages.filter(message => message !== turn.user && message !== turn.assistant);
    if (reason === 'user_stop') {
      owner.files = [...files, ...owner.files];
      owner.draft = [content, owner.draft].filter(Boolean).join('\n');
      owner.error = null;
      if (!owner.id && !unsentConversations.value.includes(owner)) unsentConversations.value.push(owner);
    } else {
      files.forEach(file => releaseAttachment(file, true));
      owner.retry = null;
    }
    return;
  }
  turn.owner.retry?.files.forEach(file => releaseAttachment(file));
  turn.assistant.pending = false;
  turn.assistant.streaming = false;
  turn.assistant.interrupted = true;
  turn.user.pending = false;
  turn.owner.retry = null;
  if (reason === 'user_stop') turn.owner.error = null;
  await cancelTurn(turn.id).catch(() => undefined);
}

function stopPlayback(): void {
  activeSpeechPlaybackId = null;
  activeSpeechOwner = null;
  activeSpeechMessageId.value = null;
  void voice.stop();
}

async function playSpeech(turnId: string, assistantMessageId: string): Promise<void> {
  const playbackId = crypto.randomUUID();
  activeSpeechPlaybackId = playbackId;
  activeSpeechOwner = selected.value;
  activeSpeechMessageId.value = assistantMessageId;
  await voice.speak(turnId, assistantMessageId, true);
  if (activeSpeechPlaybackId === playbackId) {
    activeSpeechPlaybackId = null;
    activeSpeechOwner = null;
    activeSpeechMessageId.value = null;
  }
}

async function checkServerHealth(force = false): Promise<void> {
  if (disposed || document.visibilityState !== 'visible' || healthController || (!force && Date.now() - lastHealthCheck < 30000)) return;
  const controller = new AbortController();
  healthController = controller;
  lastHealthCheck = Date.now();
  const timeout = window.setTimeout(() => controller.abort(), 3000);
  try {
    const healthy = await getHealth(controller.signal);
    if (!disposed) healthState.value = healthy === null ? 'checking' : healthy ? 'online' : 'offline';
  } catch (error) {
    if (!disposed && (error instanceof TypeError || controller.signal.aborted)) healthState.value = 'offline';
  } finally {
    window.clearTimeout(timeout);
    healthController = null;
  }
}
function serverForeground(): void { void checkServerHealth(); }
function serverNetworkChanged(): void { void checkServerHealth(true); }

function replayMessage(message: LocalChatMessage): void {
  if (!message.serverId) return;
  void (async () => {
    stopPlayback();
    const turnId = crypto.randomUUID();
    await playSpeech(turnId, message.serverId!);
  })();
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && transcription.isBusy.value) {
    event.preventDefault();
    cancelRecording();
    return;
  }

  if (event.key === 'Enter' && !event.shiftKey && canSend.value) {
    event.preventDefault();
    void handleSubmit();
  }
}

async function toggleRecording(): Promise<void> {
  if (!canStartRecording.value) return;
  stopPlayback();
  const input = composerInput.value;
  capture = { owner: selected.value, text: draft.value,
    start: input?.selectionStart ?? draft.value.length, end: input?.selectionEnd ?? draft.value.length };
  void dismissKeyboard();
  await transcription.start();
}

function startNewConversation(explicit = true): void {
  cancelMemoryActivityPolling();
  cancelRecording();
  stopPlayback();
  ++conversationLoad;
  isRestoring.value = false;
  localStorage.removeItem(STORAGE_KEY);
  selected.value = newConversation;
  selected.value.error = null;
  feedbackTarget.value = null;
  feedbackErrorMessage.value = null;
  feedbackStatusByMessageId.value = {};
  closeSidebarDrawer();
  resizeComposer();
  restoreReadingPosition(selected.value);
  if (explicit) focusComposer(true);
}


function recoverUnsentConversation(): void {
  const owner = unsentConversation.value;
  if (!owner) return;
  cancelRecording();
  cancelMemoryActivityPolling();
  ++conversationLoad;
  isRestoring.value = false;
  selected.value = owner;
  closeSidebarDrawer();
  resizeComposer();
  restoreReadingPosition(owner);
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
    if (capture?.owner.id === targetId) cancelRecording();
    const turn = [...activeTurns.values()].find(turn => turn.owner.id === targetId);
    if (turn) await stopActiveTurn('conversation_deleted', turn);
    await deleteConversation(targetId);
    deletedConversations.add(targetId);
    researchByConversation.delete(targetId);
    pendingResearchReads.get(targetId)?.controller.abort();
    contexts.get(targetId)?.files.forEach(file => releaseAttachment(file, true));
    contexts.delete(targetId);
    conversations.value = conversations.value.filter((conversation) => conversation.id !== targetId);
  if (conversationId.value === targetId) {
      startNewConversation(false);
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
// The native hands-free session selects its own chat without foregrounding the
// window or focusing the keyboard. Drafts belong to their original contexts.
function navigateNativeVoiceConversation(id: string | null): void {
  if (transcription.isBusy.value) return;
  if (id) {
    if (conversationId.value !== id) void openConversation(id);
    conversationChanged(id, false);
  } else if (conversationId.value && !selectedHasActiveTurn.value) {
    startNewConversation(false);
  }
}
watch(nativeComposerActive, active => { if (!active) resizeComposer(); });
watch(() => props.nativeVoiceActive, active => { if (!active) clearNativeVoiceStreams(true); });
watch(isRestoring, restoring => {
  if (!restoring && shareRequested) void receiveSharedFiles();
  if (!restoring && explicitDestination) { const id = explicitDestination; explicitDestination = null; void openConversation(id); }
});
defineExpose({ openConversation: navigateToConversation, navigateNativeVoiceConversation, acceptNativeVoiceStream, conversationChanged, reconcileConversations, resetConversationChanges });
onMounted(() => {
  document.addEventListener('visibilitychange', serverForeground);
  document.addEventListener('visibilitychange', flushPresentationOnHidden);
  window.addEventListener('focus', serverForeground);
  window.addEventListener('aegis-app-visibility', serverForeground);
  window.addEventListener('online', serverNetworkChanged);
  window.addEventListener('offline', serverNetworkChanged);
  document.addEventListener('visibilitychange', conversationHeadForeground);
  window.addEventListener('focus', conversationHeadForeground);
  window.addEventListener('online', conversationHeadForeground);
  window.addEventListener('offline', conversationHeadForeground);
  scheduleConversationHeadSync();
  syncViewportHeight();
  layoutObserver = new ResizeObserver(handleMessagesLayout);
  if (messagesContainer.value) layoutObserver.observe(messagesContainer.value);
  if (messagesContent.value) layoutObserver.observe(messagesContent.value);
  proactiveObserver = new IntersectionObserver(proactiveVisibilityChanged, { root: messagesContainer.value, threshold: [0, 1] });
  messagesContainer.value?.addEventListener('scroll', proactiveVisibilityChanged, { passive: true });
  window.addEventListener('aegis-chat-visible', proactiveVisibilityChanged); window.addEventListener('aegis-navigation-layer', proactiveVisibilityChanged);
  window.addEventListener('aegis-app-visibility', proactiveForeground);
  document.addEventListener('visibilitychange', proactiveForeground); window.addEventListener('focus', proactiveForeground); window.addEventListener('online', proactiveForeground);
  document.addEventListener('visibilitychange', researchForeground);
  window.addEventListener('aegis-app-visibility', researchForeground);
  window.addEventListener('online', researchForeground);
  researchForeground();
  void syncProactiveMessages(); scheduleProactiveSync();
  stopObservingGoogle = observeGoogleConnection({
    started: () => {
      emailConnectionState.value = 'pending';
      emailConnectionMessage.value = 'Conectando conta Google…';
    },
    connected: () => void confirmEmailConnection()
  });
  const handleViewportChange = (): void => {
    syncViewportHeight();
    proactiveVisibilityChanged();
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
  window.addEventListener('aegis-shared-attachments', receiveSharedFiles);
  void restoreConversation().finally(receiveSharedFiles);
  void voice.refreshStatus();
  void transcription.refreshStatus();
  void checkServerHealth();
});

onBeforeUnmount(() => {
  window.removeEventListener('aegis-shared-attachments', receiveSharedFiles);
  document.removeEventListener('visibilitychange', flushPresentationOnHidden);
  for (const p of textPresentations.values()) clearTimeout(p.timer);
  textPresentations.clear(); nativeVoiceTurns.clear(); closedNativeVoiceTurns.clear();
  disposed = true;
  conversationSync.dispose(); clearTimeout(conversationSyncTimer); clearTimeout(pushRecoveryTimer); for (const timer of conversationRetryTimers.values()) clearTimeout(timer); conversationRetryTimers.clear();
  healthController?.abort();
  clearTimeout(conversationHeadTimer);
  conversationHeadAbort?.abort();
  document.removeEventListener('visibilitychange', conversationHeadForeground);
  window.removeEventListener('focus', conversationHeadForeground);
  window.removeEventListener('online', conversationHeadForeground);
  window.removeEventListener('offline', conversationHeadForeground);
  document.removeEventListener('visibilitychange', serverForeground);
  window.removeEventListener('focus', serverForeground);
  window.removeEventListener('aegis-app-visibility', serverForeground);
  window.removeEventListener('online', serverNetworkChanged);
  window.removeEventListener('offline', serverNetworkChanged);
  proactiveAbort.abort(); clearTimeout(proactiveTimer); proactiveObserver?.disconnect();
  if (proactiveReadFrame !== undefined) cancelAnimationFrame(proactiveReadFrame);
  messagesContainer.value?.removeEventListener('scroll', proactiveVisibilityChanged);
  window.removeEventListener('aegis-chat-visible', proactiveVisibilityChanged); window.removeEventListener('aegis-navigation-layer', proactiveVisibilityChanged);
  window.removeEventListener('aegis-app-visibility', proactiveForeground);
  document.removeEventListener('visibilitychange', proactiveForeground); window.removeEventListener('focus', proactiveForeground); window.removeEventListener('online', proactiveForeground);
  document.removeEventListener('visibilitychange', researchForeground);
  window.removeEventListener('aegis-app-visibility', researchForeground);
  window.removeEventListener('online', researchForeground);
  clearTimeout(researchTimer);
  clearTimeout(nativeResearchTimer);
  for (const { controller } of pendingResearchReads.values()) controller.abort();
  cancelMemoryActivityPolling();
  clearToolStatus();
  emailConnectionAbortController?.abort();
  stopObservingGoogle?.();
  transcription.dispose();
  capture = null;
  ++conversationLoad;
  layoutObserver?.disconnect();
  if (streamScrollFrame !== null) cancelAnimationFrame(streamScrollFrame);
  for (const turn of [...activeTurns.values()]) void stopActiveTurn('view_unmounted', turn);
  for (const context of new Set([selected.value, newConversation, ...unsentConversations.value, ...contexts.values()]))
    context.files.forEach(file => releaseAttachment(file, true));
  stopPlayback();
  for (const timer of historyRefreshTimers.values()) window.clearTimeout(timer);
  historyRefreshTimers.clear();
  for (const timer of toolStatusTimers.values()) window.clearTimeout(timer);
  toolStatusTimers.clear();

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
        :generating-conversation-ids="generatingConversationIds"
        :active-conversation-id="conversationId"
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

      <section class="chat-panel" aria-label="Conversa com a Aegis" @dragover="dragFiles" @drop="dragFiles">
      <header class="chat-header">
        <div class="conversation-heading">
          <span>{{ conversationId ? 'Conversa ativa' : 'Nova conversa' }}</span>
          <div>
            <h1>{{ conversationLabel }}</h1>
            <p v-if="conversationCreatedLabel">{{ conversationCreatedLabel }}</p>
          </div>
        </div>

        <div v-if="$slots['voice-status'] || conversationId || healthState === 'offline'" class="chat-voice-controls">
          <slot name="voice-status" />
          <span v-if="healthState === 'offline'" class="server-indicator" role="status" title="Servidor indisponível">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m3 3 18 18M9.5 4.4A16.4 16.4 0 0 1 22 8M2 8a16.6 16.6 0 0 1 3.3-2.1M13.5 9.2A11.3 11.3 0 0 1 18.5 12M5.5 12a11.2 11.2 0 0 1 3-1.9M9 16a4.7 4.7 0 0 1 6 0M12 20h.01" /></svg>
            <span class="sr-only">Servidor indisponível</span>
          </span>
          <ConversationMenu v-if="conversationId" :title="activeConversationTitle || 'Nova conversa'" show-title :disabled="isRestoring" @info="infoTarget = currentConversationSummary" @rename="requestRenameConversation(currentConversationSummary)" @delete="requestDeleteConversation(currentConversationSummary)" />
        </div>
      </header>

      <div v-if="$slots.notice" class="chat-notices"><slot name="notice" /></div>
      <div ref="messagesContainer" class="messages" aria-live="polite" tabindex="0"
        @scroll="handleMessagesScroll" @wheel.passive="handleMessagesWheel"
        @touchstart.passive="handleMessagesTouchStart" @touchmove.passive="handleMessagesTouchMove"
        @pointerdown="noteScrollIntent(0)" @pointermove="($event.buttons === 1) && noteScrollIntent(0)" @keydown="handleMessagesKey">
        <div ref="messagesContent" class="messages-content">
        <div v-if="isRestoring" class="empty-state">
          <AegisMark />
          <span>Carregando conversa...</span>
        </div>

        <div v-else-if="messages.length === 0" class="empty-state">
          <RuriMascot />
          <span class="empty-state__eyebrow">Aegis</span>
          <strong>Nova conversa</strong>
          <span>Digite ou fale uma mensagem.</span>
        </div>

        <template v-else>
          <template v-for="message in messages" :key="message.id">
          <ChatMessage
            :message="message"
            :data-proactive-sequence="message.isProactive ? message.proactiveSequence : undefined"
            :data-proactive-conversation="message.isProactive ? message.conversationId : undefined"
            :feedback-status="feedbackStatusByMessageId[message.serverId ?? message.id]"
            :is-playing="voice.isBusy.value && message.serverId === activeSpeechMessageId"
            :activity-status="message.role === 'assistant' && message.id === messages[messages.length - 1]?.id
              ? (toolStatusMessage ?? (message.pending && selectedHasActiveTurn && isLoading && (turnStatus === 'thinking' || !message.content.trim()) ? 'Pensando…' : null))
              : null"
            :activity-state="toolStatusState === 'completed' ? 'completed' : toolStatusState === 'failed' ? 'failed' : 'working'"
            :research-sites="message.role === 'assistant' && message.id === messages[messages.length - 1]?.id ? selectedResearchSites : null"
            :resolve-video="resolveVideo"
            @open-video="openVideo"
            @feedback="openFeedback"
            @replay="replayMessage"
            @stop-playback="stopPlayback"
          />
          <BackgroundResearchStatus :job="selectedJobByMessage.get(message.serverId ?? message.id)"
            :cancelling="cancellingResearch" @cancel="stopBackgroundResearch" />
          </template>
        </template>

        <div v-if="selectedNativeResearch" class="message-row message-row--assistant">
          <div class="message-stack">
            <WebResearchActivity v-if="selectedNativeResearch.status.category === 'web_search'" :sites="selectedNativeResearch.sites"
              :state="selectedNativeResearch.status.state === 'started' ? 'working' : selectedNativeResearch.status.state"
              :label="selectedNativeResearch.status.message" />
            <span v-else class="background-turn-notice" role="status">{{ selectedNativeResearch.status.message }}</span>
          </div>
        </div>
        <div class="messages-end"></div>
        </div>
      </div>

      <form class="composer" @submit.prevent="!nativeComposerActive && (selectedHasActiveTurn ? stopActiveTurn() : handleSubmit())">
        <p v-if="unsentConversation && unsentConversation !== selected" class="background-turn-notice" role="status">
          Uma mensagem não pôde ser enviada.
          <button type="button" @click="recoverUnsentConversation">Recuperar mensagem</button>
        </p>
        <p v-if="emailConnectionMessage" class="email-connection-notice" :class="`email-connection-notice--${emailConnectionState}`" role="status">{{ emailConnectionMessage }}</p>
        <p v-if="showErrorMessage" class="composer-error" role="alert">{{ errorMessage }}</p>
        <div class="composer-box" :class="{ 'composer-box--with-files': selected.files.length }">
          <ul v-if="selected.files.length" class="attachment-previews" aria-label="Anexos da mensagem">
            <AttachmentPreview v-for="(file, index) in selected.files" :key="file.localId" :file="file"
              @remove="releaseAttachment(selected.files.splice(index, 1)[0]!, true)" />
          </ul>
          <div v-show="nativeComposerActive"><slot name="native-voice-composer" /></div>
          <template v-if="!nativeComposerActive">
            <RecordingComposer v-if="transcription.isBusy.value" :state="transcription.state.value" :levels="transcription.inputLevels.value"
              @cancel="cancelRecording" @review="transcription.stopRecording('review')" @send="transcription.stopRecording('send')" />
            <div v-else class="composer-field">
              <ChatAttachmentPicker @files="stageFiles(selected, $event.map(pickedFile))" />
              <textarea
                v-model="draft"
                rows="1"
                placeholder="Escreva para a Aegis..."
                aria-label="Mensagem"
                :class="{ 'composer-input--scrollable': isComposerScrollable }"
                ref="composerInput"
                @input="resizeComposer"
                @paste="pasteFiles"
                @keydown="handleKeydown"
              ></textarea>

              <button
                class="record-button"
                type="button"
                :disabled="!canStartRecording"
                aria-label="Gravar mensagem de voz"
                title="Gravar mensagem de voz"
                @click="toggleRecording"
              >
                <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="9" y="3" width="6" height="12" rx="3"/><path d="M6 11a6 6 0 0 0 12 0M12 17v4m-4 0h8"/></svg>
              </button>

              <button class="send-button" :class="{ 'send-button--stop': selectedHasActiveTurn }" type="submit" :disabled="(!selectedHasActiveTurn && !canSend) || isRestoring || emailConnectionState === 'pending'" :aria-label="selectedHasActiveTurn ? 'Interromper geração' : 'Enviar mensagem'">
                <svg v-if="selectedHasActiveTurn" viewBox="0 0 20 20" aria-hidden="true"><rect x="6.25" y="6.25" width="7.5" height="7.5" rx="1.2" /></svg>
                <svg v-else viewBox="0 0 20 20" aria-hidden="true" focusable="false"><path d="M10 16V4m0 0L5.8 8.2M10 4l4.2 4.2" /></svg>
              </button>
            </div>
          </template>
        </div>
        <template v-if="!nativeComposerActive">
          <p v-if="transcription.errorMessage.value" class="composer-error" role="status">{{ transcription.errorMessage.value }}</p>
          <p v-if="voice.voiceMessage.value && !voice.voiceAvailable.value" class="composer-error" role="status">{{ voice.voiceMessage.value }}</p>
          <span class="composer-hint">{{ transcription.isBusy.value ? 'Esc para cancelar' : 'Enter para enviar · Shift + Enter para nova linha' }}</span>
        </template>
      </form>
      </section>
    </div>

    <ConversationInfo v-if="infoTarget" :conversation-id="infoTarget.id" :created-at="infoTarget.createdAt" @close="infoTarget = null" />
    <VideoAttachmentDialog v-if="videoTarget" :citation="videoTarget.citation" :file-name="videoTarget.fileName" @close="videoTarget = null" />
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
