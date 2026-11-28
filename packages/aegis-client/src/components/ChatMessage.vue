<script setup lang="ts">
import { computed, ref, useId } from 'vue';
import MarkdownMessage from './MarkdownMessage.vue';
import WebResearchActivity from './WebResearchActivity.vue';
import WebSourceStack from './WebSourceStack.vue';
import { mergeResearchSites, topResearchSites, type ResearchSite } from '../services/researchSites';
import { prepareWebResearch } from '../services/webResearchPresentation';
import { clientEnvironment, openClientLink } from '../services/clientEnvironment';
import { attachmentLinkUrl, attachmentSize, chatAttachmentUrl } from '../services/chatAttachments';
import { copyVideoCitationLabels, type VideoCitation } from '../services/chatVideo';
import MemoryActivityDisclosure from './MemoryActivityDisclosure.vue';
import type { ChatAttachment, FeedbackRating, LocalChatMessage } from '../types/chat';

const props = defineProps<{
  message: LocalChatMessage;
  feedbackStatus?: string | null;
  isPlaying?: boolean;
  activityStatus?: string | null;
  researchSites?: readonly ResearchSite[] | null;
  activityState?: 'working' | 'completed' | 'failed';
  resolveVideo?: (citation: VideoCitation) => ChatAttachment | undefined;
}>();
const emit = defineEmits<{
  feedback: [message: LocalChatMessage, rating: FeedbackRating];
  replay: [message: LocalChatMessage];
  stopPlayback: [];
  openVideo: [citation: VideoCitation];
}>();
const copied = ref(false);
const copyError = ref(false);
const sourcesId = useId();
const sourcesOpen = ref(false);
const sourceLinkError = ref(false);
const attachmentError = ref(false);
function attachmentUrl(file: ChatAttachment): string | undefined {
  if (file.linkUrl) return attachmentLinkUrl(file.linkUrl)?.href;
  const id = file.id;
  const messageId = props.message.serverId ?? props.message.id;
  if (messageId.startsWith('local-') || id.startsWith('local-')) return;
  return chatAttachmentUrl(messageId, id);
}
async function openAttachment(event: MouseEvent, file: ChatAttachment): Promise<void> {
  const url = attachmentUrl(file);
  if (!url || clientEnvironment().externalLinks !== 'system') return;
  event.preventDefault();
  try { await openClientLink(url); attachmentError.value = false; }
  catch { attachmentError.value = true; }
}
const showMessageActions = computed(() => !props.message.pending && !props.message.streaming && !props.message.interrupted);
const sources = computed(() => props.message.role === 'assistant' && !props.message.pending &&
  !props.message.streaming && props.message.webResearch
  ? prepareWebResearch(props.message.content, props.message.webResearch).sources : []);
const sourceSites = computed(() => topResearchSites(mergeResearchSites(new Map(),
  sources.value.map(source => ({ url: source.url, count: 1 })))));
const sourcesLabel = computed(() => `Fontes da pesquisa (${sources.value.length} ${sources.value.length === 1 ? 'link' : 'links'})`);

async function followSource(event: MouseEvent, href: string): Promise<void> {
  if (clientEnvironment().externalLinks !== 'system' || (event.type === 'auxclick' && event.button !== 1)) return;
  event.preventDefault();
  try {
    await openClientLink(href);
    sourceLinkError.value = false;
  } catch {
    sourceLinkError.value = true;
  }
}

async function copy(): Promise<void> {
  try {
    await navigator.clipboard.writeText(props.message.role === 'assistant'
      ? copyVideoCitationLabels(props.message.content) : props.message.content);
    copied.value = true;
    copyError.value = false;
    window.setTimeout(() => copied.value = false, 1800);
  } catch {
    copyError.value = true;
    window.setTimeout(() => copyError.value = false, 3000);
  }
}
</script>

<template>
  <article class="message-row" :class="[`message-row--${message.role}`, { 'message-row--active': !!activityStatus || !!message.interrupted }]">
    <div class="message-stack">
      <WebResearchActivity v-if="researchSites && activityStatus" :sites="researchSites"
        :state="activityState ?? 'working'" :label="activityStatus" />
      <div v-else-if="activityStatus" class="assistant-activity" :class="`assistant-activity--${activityState ?? 'working'}`" role="status" :aria-label="activityStatus">
        <span class="assistant-activity__text" :data-text="activityStatus" aria-hidden="true">{{ activityStatus }}</span>
      </div>
      <div v-else-if="message.interrupted" class="assistant-activity assistant-activity--interrupted" role="status">
        <span class="assistant-activity__text">Raciocínio interrompido</span>
      </div>
      <MemoryActivityDisclosure v-if="message.role === 'assistant' && message.memoryActivity?.sections.length"
        :activity="message.memoryActivity" />
      <div v-if="message.role === 'user' || message.content.trim() || message.attachments?.length || (message.streaming && !activityStatus)" class="message-bubble" :class="{ 'message-bubble--streaming': message.streaming }">
        <MarkdownMessage v-if="message.role !== 'user'" :content="message.content" :animate-changes="message.streaming"
          :web-research="message.streaming ? null : message.webResearch" :resolve-video="resolveVideo"
          @open-video="emit('openVideo', $event)" />
        <ul v-if="message.attachments?.length" class="attachment-list" :aria-label="message.role === 'assistant' ? 'Arquivos gerados' : 'Anexos'">
          <li v-for="file in message.attachments" :key="file.id" class="attachment-chip">
            <a v-if="attachmentUrl(file)" class="attachment-name" :href="attachmentUrl(file)" :download="file.linkUrl ? undefined : file.fileName"
              :target="file.linkUrl ? '_blank' : undefined" :rel="file.linkUrl ? 'noopener noreferrer' : undefined"
              @click="openAttachment($event, file)">{{ file.fileName }}</a>
            <span v-else class="attachment-name">{{ file.fileName }}</span><small>{{ file.linkUrl ? 'Link' : attachmentSize(file.sizeBytes) }}</small>
          </li>
        </ul>
        <span v-if="attachmentError" role="status">Não foi possível abrir o anexo.</span>
        <p v-if="message.role === 'user' && message.content">{{ message.content }}</p>
        <span v-if="message.streaming" class="generation-marker" role="status" aria-label="Aegis está respondendo"></span>
      </div>
      <div v-if="showMessageActions || sources.length" class="message-actions">
        <template v-if="showMessageActions && message.role === 'assistant'">
          <button class="message-action" aria-label="Marcar resposta como boa" @click="emit('feedback', message, 'good')"><svg viewBox="0 0 24 24"><path d="M7 10v10M4 10h3v10H4zM7 20h11l2-7a2 2 0 0 0-2-2h-3l1-4a3 3 0 0 0-3-3l-5 6Z" /></svg></button>
          <button class="message-action" aria-label="Marcar resposta como ruim" @click="emit('feedback', message, 'bad')"><svg viewBox="0 0 24 24"><path d="M7 14V4M4 4h3v10H4zM7 4h11l2 7a2 2 0 0 1-2 2h-3l1 4a3 3 0 0 1-3 3l-5-6Z" /></svg></button>
          <button class="message-action" :aria-label="isPlaying ? 'Interromper reprodução' : 'Ouvir resposta'" @click="isPlaying ? emit('stopPlayback') : emit('replay', message)">
            <span v-if="isPlaying" class="audio-bars"><i/><i/><i/><i/></span>
            <svg v-else viewBox="0 0 24 24"><path d="M4 10v4h4l5 4V6l-5 4H4Zm12-1a5 5 0 0 1 0 6m3-9a9 9 0 0 1 0 12" /></svg>
          </button>
        </template>
        <button v-if="showMessageActions" class="message-action" :aria-label="copied ? 'Mensagem copiada' : 'Copiar mensagem'" @click="copy"><svg v-if="copied" viewBox="0 0 24 24"><path d="m5 12 4 4L19 6" /></svg><svg v-else viewBox="0 0 24 24"><rect x="8" y="8" width="11" height="11" rx="2"/><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3"/></svg></button>
        <button v-if="sources.length" type="button" class="message-action message-action--sources"
          :aria-label="sourcesLabel" :title="sourcesLabel" :aria-expanded="sourcesOpen" :aria-controls="sourcesId"
          @click="sourcesOpen = !sourcesOpen">
          <WebSourceStack v-if="sourceSites.length" :sites="sourceSites" compact />
          <svg v-else viewBox="0 0 24 24" aria-hidden="true"><path d="M10 14a4 4 0 0 0 6 0l4-4a4 4 0 0 0-6-6l-2 2m2 4a4 4 0 0 0-6 0l-4 4a4 4 0 0 0 6 6l2-2"/></svg>
        </button>
        <span v-if="showMessageActions && copyError" class="message-action-error" role="status">Não foi possível copiar.</span>
      </div>
      <div v-if="sources.length" :id="sourcesId" class="message-research-sources" :hidden="!sourcesOpen">
        <ul v-if="sourcesOpen">
          <li v-for="source in sources" :key="source.url">
            <a :href="source.url" target="_blank" rel="noopener noreferrer"
              @click="followSource($event, source.url)" @auxclick="followSource($event, source.url)">{{ source.title }}</a>
            <small>{{ source.host }}{{ source.kind === 'opened' ? ' · aberta' : '' }}</small>
          </li>
        </ul>
        <p v-if="sourceLinkError" class="message-action-error" role="status">Não foi possível abrir o navegador.</p>
      </div>
    </div>
  </article>
</template>

<style scoped>
.message-action--sources {
  inline-size: auto;
  min-inline-size: 30px;
  flex: 0 0 auto;
  padding-inline: 5px;
  line-height: 1;
}
.message-action--sources[aria-expanded="true"] { background: var(--surface-soft); }
.message-research-sources {
  min-inline-size: 0;
  color: var(--muted);
  font-size: .85rem;
  overflow-wrap: anywhere;
}
.message-research-sources ul { margin: .25rem 0 0; padding-inline-start: 1.25rem; }
.message-research-sources li + li { margin-block-start: .3rem; }
.message-research-sources a { color: var(--accent-strong); text-decoration: underline; }
.message-research-sources small { display: block; font-size: .82em; }
@media (max-width: 620px) {
  .message-action--sources { min-inline-size: 38px; }
}
</style>
