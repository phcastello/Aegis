<script setup lang="ts">
import DOMPurify from 'dompurify';
import MarkdownIt from 'markdown-it';
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, useId, watch } from 'vue';
import { clientEnvironment, externalUrl, openClientLink } from '../services/clientEnvironment';
import { isGoogleAuthorizationLink } from '../services/emailConnectionLinks';
import { renderWebResearchMarkdown, type ResearchLink } from '../services/webResearchPresentation';
import { isUsableResearchFavicon } from '../services/researchSites';
import { formatVideoTime, parseVideoCitation, type VideoCitation } from '../services/chatVideo';
import type { ChatAttachment, WebResearch } from '../types/chat';
import WebSourceIcon from './WebSourceIcon.vue';

const props = defineProps<{
  content: string;
  animateChanges?: boolean;
  webResearch?: WebResearch | null;
  resolveVideo?: (citation: VideoCitation) => ChatAttachment | undefined;
}>();
const emit = defineEmits<{ openVideo: [citation: VideoCitation] }>();

const messageElement = ref<HTMLElement | null>(null);

const markdown = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true
});

markdown.renderer.rules.table_open = (tokens, index, options, _environment, renderer) =>
  '<div class="markdown-table-scroll" role="region" aria-label="Tabela" tabindex="0">' +
  renderer.renderToken(tokens, index, options);
markdown.renderer.rules.table_close = (tokens, index, options, _environment, renderer) =>
  renderer.renderToken(tokens, index, options) + '</div>\n';

const defaultLinkOpen =
  markdown.renderer.rules.link_open ??
  ((tokens, index, options, _environment, renderer) =>
    renderer.renderToken(tokens, index, options));

markdown.renderer.rules.link_open = (tokens, index, options, environment, renderer) => {
  const link = tokens[index];
  const href = link.attrGet('href');
  if (href && /^aegis-video:/i.test(href)) {
    const citation = parseVideoCitation(href);
    const file = citation ? props.resolveVideo?.(citation) : undefined;
    const tag = file && citation ? 'button' : 'span';
    const end = tokens.findIndex((token, position) => position > index && token.type === 'link_close');
    if (end >= 0) tokens[end].tag = tag;
    link.tag = tag;
    link.attrs = file && citation ? [
      ['class', 'video-citation'],
      ['type', 'button'],
      ['data-video-citation', href],
      ['aria-label', `Abrir ${file.fileName} em ${formatVideoTime(citation.seconds)}`],
    ] : null;
    return renderer.renderToken(tokens, index, options);
  }
  // The OAuth callback must return to this app tab so its connection result is visible.
  const citation = tokens[index].attrGet('class')?.split(' ').includes('web-research-link');
  tokens[index].attrSet('target', !citation && isGoogleAuthorizationLink(tokens[index].attrGet('href')) ? '_self' : '_blank');
  tokens[index].attrSet('rel', 'noopener noreferrer');
  return defaultLinkOpen(tokens, index, options, environment, renderer);
};

async function followLink(event: MouseEvent): Promise<void> {
  if (clientEnvironment().externalLinks !== 'system' || (event.type === 'auxclick' && event.button !== 1)) return;
  const anchor = (event.target as Element).closest<HTMLAnchorElement>('a[href]');
  if (!anchor) return;
  event.preventDefault();
  const href = externalUrl(anchor.href);
  if (!href) return;
  try {
    await openClientLink(href, !anchor.classList.contains('web-research-link') && isGoogleAuthorizationLink(href));
    linkError.value = null;
  } catch { linkError.value = 'Não foi possível abrir o navegador. Tente novamente.'; }
}
const linkError = ref<string | null>(null);

const renderedResearch = computed(() => {
  const content = props.animateChanges
    ? props.content.replace(/(\[[^\]\r\n]+\])\(aegis-video:[^)\r\n]*$/i, '$1')
    : props.content;
  return renderWebResearchMarkdown(markdown, content, props.webResearch);
});
const renderedContent = computed(() => DOMPurify.sanitize(renderedResearch.value.html, { ADD_ATTR: ['referrerpolicy', 'target'] }));

type PreviewMode = 'hover' | 'focus' | 'pinned';
const previewId = useId();
const previewElement = ref<HTMLElement | null>(null);
const citationPreview = shallowRef<{
  group: HTMLElement; trigger: HTMLElement; source: ResearchLink; mode: PreviewMode;
} | null>(null);
const previewPosition = ref<{ left: string; top: string }>();
let closeTimer: ReturnType<typeof setTimeout> | undefined;

function keepCitationPreview(): void {
  clearTimeout(closeTimer);
}

function closeCitationPreview(): void {
  keepCitationPreview();
  const preview = citationPreview.value;
  preview?.trigger.removeAttribute('aria-describedby');
  if (preview?.mode === 'pinned') preview.trigger.setAttribute('aria-expanded', 'false');
  citationPreview.value = null;
  previewPosition.value = undefined;
}

function showCitationPreview(group: HTMLElement, trigger: HTMLElement, mode: PreviewMode): void {
  keepCitationPreview();
  const current = citationPreview.value;
  if (current?.group === group && (mode === 'hover' || mode === current.mode)) return;
  const source = renderedResearch.value.inlineCitations[Number(group.dataset.citationIndex)];
  if (!source) return;
  closeCitationPreview();
  trigger.setAttribute('aria-describedby', previewId);
  if (mode === 'pinned') trigger.setAttribute('aria-expanded', 'true');
  citationPreview.value = { group, trigger, source, mode };
}

function citationGroup(target: EventTarget | null): HTMLElement | null {
  return target instanceof Element ? target.closest<HTMLElement>('.web-citation-group[data-citation-index]') : null;
}

function enterCitation(event: PointerEvent): void {
  if (event.pointerType === 'touch') return;
  const group = citationGroup(event.target);
  const anchor = group?.querySelector<HTMLAnchorElement>('.web-citation');
  if (group && anchor) showCitationPreview(group, anchor, 'hover');
}

function leaveCitation(event: PointerEvent): void {
  const preview = citationPreview.value;
  if (!preview || preview.mode !== 'hover' || event.pointerType === 'touch') return;
  if (event.relatedTarget instanceof Node && (preview.group.contains(event.relatedTarget) ||
    previewElement.value?.contains(event.relatedTarget))) return;
  keepCitationPreview();
  closeTimer = setTimeout(closeCitationPreview, 140);
}

function focusCitation(event: FocusEvent): void {
  const group = citationGroup(event.target);
  if (group && event.target instanceof HTMLAnchorElement && event.target.matches(':focus-visible'))
    showCitationPreview(group, event.target, 'focus');
}

function blurCitation(event: FocusEvent): void {
  const preview = citationPreview.value;
  if (!preview || preview.mode === 'hover') return;
  if (event.relatedTarget instanceof Node && preview.group.contains(event.relatedTarget)) return;
  closeCitationPreview();
}

function clickMessage(event: MouseEvent): void {
  const video = event.target instanceof Element
    ? event.target.closest<HTMLButtonElement>('button[data-video-citation]') : null;
  if (video) {
    const citation = parseVideoCitation(video.dataset.videoCitation ?? '');
    if (citation && props.resolveVideo?.(citation)) emit('openVideo', citation);
    return;
  }
  const group = citationGroup(event.target);
  const details = event.target instanceof Element ? event.target.closest<HTMLButtonElement>('.web-citation__details') : null;
  if (group && details) {
    if (citationPreview.value?.trigger === details) closeCitationPreview();
    else showCitationPreview(group, details, 'pinned');
    return;
  }
  if (group) closeCitationPreview();
  void followLink(event);
}

function syncCitationIcon(image: HTMLImageElement): void {
  image.parentElement?.classList.toggle('web-citation__icon--ready', isUsableResearchFavicon(image));
}

function onCitationIconEvent(event: Event): void {
  if (event.target instanceof HTMLImageElement && event.target.matches('.web-citation__favicon'))
    syncCitationIcon(event.target);
}

function positionCitationPreview(): void {
  const preview = citationPreview.value;
  const panel = previewElement.value;
  if (!preview || !panel) return;
  const anchor = preview.group.getBoundingClientRect();
  const width = panel.offsetWidth;
  const height = panel.offsetHeight;
  const edge = 8;
  const gap = 5;
  if (anchor.bottom <= 0 || anchor.top >= window.innerHeight || !preview.group.isConnected) {
    closeCitationPreview();
    return;
  }
  const top = anchor.bottom + gap + height + edge <= window.innerHeight || anchor.top < height + gap + edge
    ? anchor.bottom + gap : anchor.top - gap - height;
  previewPosition.value = {
    left: `${Math.max(edge, Math.min(anchor.left, window.innerWidth - width - edge))}px`,
    top: `${Math.max(edge, Math.min(top, window.innerHeight - height - edge))}px`
  };
}

function dismissCitationOutside(event: PointerEvent): void {
  const preview = citationPreview.value;
  if (preview && event.target instanceof Node && !preview.group.contains(event.target) &&
    !previewElement.value?.contains(event.target)) closeCitationPreview();
}

function dismissCitationOnEscape(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || !citationPreview.value) return;
  event.preventDefault();
  closeCitationPreview();
}

watch(citationPreview, async (preview, _previous, onCleanup) => {
  if (!preview) return;
  document.addEventListener('pointerdown', dismissCitationOutside);
  document.addEventListener('keydown', dismissCitationOnEscape);
  window.addEventListener('resize', positionCitationPreview);
  window.addEventListener('scroll', positionCitationPreview, true);
  onCleanup(() => {
    document.removeEventListener('pointerdown', dismissCitationOutside);
    document.removeEventListener('keydown', dismissCitationOnEscape);
    window.removeEventListener('resize', positionCitationPreview);
    window.removeEventListener('scroll', positionCitationPreview, true);
  });
  await nextTick();
  if (citationPreview.value === preview) positionCitationPreview();
});
watch(renderedResearch, closeCitationPreview);
onBeforeUnmount(closeCitationPreview);

const ANIMATION_DURATION_MS = 180;
const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

interface FadeFragment {
  start: number;
  end: number;
  startedAt: number;
}

let previousVisibleText = '';
let recentFragments: FadeFragment[] = [];

watch(
  renderedContent,
  () => {
    const element = messageElement.value;
    if (!element) {
      return;
    }

    // Images from cache may already be complete when v-html is replaced.
    element.querySelectorAll<HTMLImageElement>('.web-citation__favicon').forEach(syncCitationIcon);
    const visibleText = (element.textContent ?? '').trimEnd();
    const stableLength = visibleText.startsWith(previousVisibleText)
      ? previousVisibleText.length
      : findCommonPrefixLength(previousVisibleText, visibleText);
    const now = performance.now();

    // Only the recent revealed tail is faded. Old words are ordinary text nodes;
    // no wrapper per word or per character, and no animation tree grows over time.
    recentFragments = recentFragments.filter(fragment => fragment.end <= stableLength && now - fragment.startedAt < ANIMATION_DURATION_MS);
    if (!props.animateChanges || prefersReducedMotion.matches) recentFragments = [];
    else if (visibleText.length > stableLength && visibleText.slice(stableLength).trim()) {
      recentFragments.push({ start: stableLength, end: visibleText.length, startedAt: now });
      recentFragments = recentFragments.slice(-6);
    }
    if (recentFragments.length) applyWordFragments(element, recentFragments, now);
    previousVisibleText = visibleText;
  },
  { flush: 'post' }
);

function findCommonPrefixLength(previous: string, current: string): number {
  const limit = Math.min(previous.length, current.length);
  let index = 0;

  while (index < limit && previous[index] === current[index]) {
    index += 1;
  }

  return index;
}

function applyWordFragments(
  element: HTMLElement,
  fragments: FadeFragment[],
  now: number
): void {
  const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
  const textNodes: Array<{ node: Text; start: number; end: number }> = [];
  let visibleOffset = 0;
  let currentNode = walker.nextNode();

  while (currentNode) {
    const node = currentNode as Text;
    textNodes.push({
      node,
      start: visibleOffset,
      end: visibleOffset + node.data.length
    });
    visibleOffset += node.data.length;
    currentNode = walker.nextNode();
  }

  for (const textNode of textNodes) {
    const intersections = fragments
      .filter((fragment) => fragment.start < textNode.end && fragment.end > textNode.start)
      .map((fragment) => ({
        start: Math.max(0, fragment.start - textNode.start),
        end: Math.min(textNode.end - textNode.start, fragment.end - textNode.start),
        elapsed: now - fragment.startedAt
      }))
      .sort((left, right) => right.start - left.start);

    for (const intersection of intersections) {
      wrapAnimatedText(
        textNode.node,
        intersection.start,
        intersection.end,
        intersection.elapsed
      );
    }
  }
}

function wrapAnimatedText(textNode: Text, start: number, end: number, elapsed: number): void {
  if (end < textNode.data.length) {
    textNode.splitText(end);
  }

  let animatedNode = start > 0 ? textNode.splitText(start) : textNode;
  if (!animatedNode.data) {
    return;
  }

  const leadingWhitespaceLength = animatedNode.data.match(/^\s+/)?.[0].length ?? 0;
  if (leadingWhitespaceLength > 0) {
    animatedNode = animatedNode.splitText(leadingWhitespaceLength);
  }

  if (!animatedNode.data) {
    return;
  }

  if (prefersReducedMotion.matches || elapsed >= ANIMATION_DURATION_MS) return;
  const wrapper = document.createElement('span');
  wrapper.className = 'stream-fade-fragment';
  animatedNode.parentNode?.replaceChild(wrapper, animatedNode);
  wrapper.appendChild(animatedNode);

  if (!prefersReducedMotion.matches && elapsed < ANIMATION_DURATION_MS) {
    const animation = wrapper.animate(
      [
        {
          opacity: 0.68
        },
        {
          opacity: 1
        }
      ],
      {
        duration: ANIMATION_DURATION_MS,
        easing: 'ease-out',
        fill: 'both'
      }
    );

    animation.currentTime = Math.min(elapsed, ANIMATION_DURATION_MS);
    animation.onfinish = () => {
      animation.cancel();
      if (wrapper.isConnected && wrapper.parentNode) wrapper.replaceWith(...Array.from(wrapper.childNodes));
    };
  }
}
</script>

<template>
  <div ref="messageElement" class="markdown-message" @click="clickMessage" @auxclick="followLink"
    @pointerover="enterCitation" @pointerout="leaveCitation" @focusin="focusCitation" @focusout="blurCitation"
    @load.capture="onCitationIconEvent" @error.capture="onCitationIconEvent" v-html="renderedContent"></div>
  <Teleport to="body">
    <div v-if="citationPreview" :id="previewId" ref="previewElement" class="web-citation-preview" role="tooltip"
      :style="previewPosition ?? { visibility: 'hidden' }" @pointerenter="keepCitationPreview" @pointerleave="leaveCitation">
      <div class="web-citation-preview__source">
        <WebSourceIcon :host="citationPreview.source.host" :label="citationPreview.source.siteName" />
        <span>{{ citationPreview.source.siteName }}</span>
      </div>
      <strong>{{ citationPreview.source.title }}</strong>
      <p v-if="citationPreview.source.snippet">{{ citationPreview.source.snippet }}</p>
    </div>
  </Teleport>
  <p v-if="linkError" role="status">{{ linkError }}</p>
</template>

<style scoped>
.markdown-message :deep(.video-citation) {
  padding: .12em .45em;
  border: 0;
  border-radius: var(--radius-inline);
  background: var(--surface-soft);
  color: var(--accent-strong);
  font: inherit;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  vertical-align: baseline;
  cursor: pointer;
}
.markdown-message :deep(.video-citation:hover) { background: var(--surface-raised); }
.markdown-message :deep(.video-citation:focus-visible) { outline: 2px solid var(--accent); }
.markdown-message :deep(.web-citation-group) {
  display: inline-flex;
  align-items: stretch;
  max-inline-size: min(13rem, calc(100% - .5em));
  margin-inline: .28em .14em;
  vertical-align: middle;
  white-space: nowrap;
  font-size: .79em;
  font-weight: 500;
  line-height: 1.35;
  border-radius: var(--radius-inline);
  background: var(--surface-soft);
}
.markdown-message :deep(.web-citation) {
  display: inline-flex;
  align-items: center;
  min-inline-size: 0;
  gap: .36em;
  padding: .25em .52em;
  border-radius: inherit;
  color: var(--muted);
  text-decoration: none;
}
.markdown-message :deep(.web-citation:hover) { color: var(--text); }
.markdown-message :deep(.web-citation-group:hover),
.markdown-message :deep(.web-citation-group:focus-within) { background: var(--surface-raised); }
.markdown-message :deep(.web-citation:focus-visible),
.markdown-message :deep(.web-citation__details:focus-visible) {
  outline: 2px solid var(--accent);
  outline-offset: -1px;
}
.markdown-message :deep(.web-citation__label) {
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  line-height: 1.4;
}
.markdown-message :deep(.web-citation__icon) {
  position: relative;
  display: inline-grid;
  place-items: center;
  flex: 0 0 1.2em;
  block-size: 1.2em;
  color: var(--muted);
}
.markdown-message :deep(.web-citation__icon::before) { content: attr(data-monogram); font-size: .83em; }
.markdown-message :deep(.web-citation__favicon) {
  position: absolute;
  inset: 0;
  inline-size: 100%;
  block-size: 100%;
  object-fit: contain;
  visibility: hidden;
}
.markdown-message :deep(.web-citation__icon--ready::before) { visibility: hidden; }
.markdown-message :deep(.web-citation__icon--ready .web-citation__favicon) { visibility: visible; }
.markdown-message :deep(.web-citation__details) {
  display: none;
  place-items: center;
  flex: 0 0 26px;
  min-block-size: 26px;
  padding: 0;
  border-radius: 0 var(--radius-inline) var(--radius-inline) 0;
  color: var(--muted);
  background: transparent;
  cursor: pointer;
}
.markdown-message :deep(.web-citation__details span) {
  display: inline-grid;
  place-items: center;
  inline-size: 1.15em;
  block-size: 1.15em;
  font-size: .88em;
  font-weight: 650;
}
.markdown-message :deep(.web-citation__details span::before) { content: 'i'; }
@media (any-pointer: coarse), (hover: none) {
  .markdown-message :deep(.web-citation__details) { display: inline-grid; }
}
.web-citation-preview {
  position: fixed;
  z-index: 50;
  box-sizing: border-box;
  inline-size: min(18rem, calc(100vw - 16px));
  max-block-size: calc(100dvh - 16px);
  overflow: auto;
  overflow-wrap: anywhere;
  padding: .7rem .82rem;
  border: 1px solid var(--line-strong);
  border-radius: var(--radius-sm);
  background: var(--surface);
  box-shadow: 0 12px 34px #0007;
  color: var(--text);
  font-size: .84rem;
  line-height: 1.35;
}
.web-citation-preview__source {
  display: flex;
  align-items: center;
  gap: .42rem;
  margin-block-end: .38rem;
  color: var(--accent-strong);
  font-size: .88em;
}
.web-citation-preview p { margin: .4rem 0 0; color: var(--muted); }
</style>
