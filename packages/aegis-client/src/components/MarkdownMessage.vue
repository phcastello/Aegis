<script setup lang="ts">
import DOMPurify from 'dompurify';
import MarkdownIt from 'markdown-it';
import { computed, ref, watch } from 'vue';
import { clientEnvironment, externalUrl, openClientLink } from '../services/clientEnvironment';
import { isGoogleAuthorizationLink } from '../services/emailConnectionLinks';
import { renderWebResearchMarkdown } from '../services/webResearchPresentation';
import type { WebResearch } from '../types/chat';

const props = defineProps<{
  content: string;
  animateChanges?: boolean;
  webResearch?: WebResearch | null;
}>();

const messageElement = ref<HTMLElement | null>(null);

const markdown = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true
});

const defaultLinkOpen =
  markdown.renderer.rules.link_open ??
  ((tokens, index, options, _environment, renderer) =>
    renderer.renderToken(tokens, index, options));

markdown.renderer.rules.link_open = (tokens, index, options, environment, renderer) => {
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

const renderedResearch = computed(() => renderWebResearchMarkdown(markdown, props.content, props.webResearch));
const renderedContent = computed(() => DOMPurify.sanitize(renderedResearch.value.html));

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
  <div ref="messageElement" class="markdown-message" @click="followLink" @auxclick="followLink" v-html="renderedContent"></div>
  <div v-if="renderedResearch.unplacedCitations.length" class="web-research-list"
    @click="followLink" @auxclick="followLink">
    <span>Fontes citadas</span>
    <ul>
      <li v-for="source in renderedResearch.unplacedCitations" :key="source.url">
        <a class="web-research-link" :href="source.url" target="_blank" rel="noopener noreferrer">{{ source.title }}</a>
        <small>{{ source.host }}</small>
      </li>
    </ul>
  </div>
  <details v-if="renderedResearch.sources.length" class="web-research-list"
    @click="followLink" @auxclick="followLink">
    <summary>Fontes da pesquisa ({{ renderedResearch.sources.length }} {{ renderedResearch.sources.length === 1 ? 'link' : 'links' }})</summary>
    <ul>
      <li v-for="source in renderedResearch.sources" :key="source.url">
        <a class="web-research-link" :href="source.url" target="_blank" rel="noopener noreferrer">{{ source.title }}</a>
        <small>{{ source.host }}{{ source.kind === 'opened' ? ' · aberta' : '' }}</small>
      </li>
    </ul>
  </details>
  <p v-if="linkError" role="status">{{ linkError }}</p>
</template>

<style scoped>
.markdown-message :deep(.web-citation) {
  display: inline;
  margin-inline: .18em;
  white-space: nowrap;
  font-size: .85em;
  font-weight: 600;
  color: var(--accent-strong);
}
.web-research-list {
  margin-top: .7rem;
  width: 100%;
  color: var(--muted);
  font-size: .85rem;
  overflow-wrap: anywhere;
}
.web-research-list summary { cursor: pointer; }
.web-research-list summary:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 3px;
}
.web-research-list ul { margin: .4rem 0 0; padding-left: 1.25rem; }
.web-research-list li { margin-top: .3rem; }
.web-research-list a { color: var(--accent-strong); text-decoration: underline; }
.web-research-list small { display: block; font-size: .82em; }
</style>
