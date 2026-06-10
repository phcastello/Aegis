<script setup lang="ts">
import DOMPurify from 'dompurify';
import MarkdownIt from 'markdown-it';
import { computed, ref, watch } from 'vue';

const props = defineProps<{
  content: string;
  animateChanges?: boolean;
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
  tokens[index].attrSet('target', '_blank');
  tokens[index].attrSet('rel', 'noopener noreferrer');
  return defaultLinkOpen(tokens, index, options, environment, renderer);
};

const renderedContent = computed(() => DOMPurify.sanitize(markdown.render(props.content)));

const ANIMATION_DURATION_MS = 600;
const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

interface WordFragment {
  start: number;
  end: number;
  startedAt: number;
}

let previousVisibleText = '';
let wordFragments: WordFragment[] = [];

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

    wordFragments = wordFragments.filter((fragment) => fragment.end <= stableLength);

    if (props.animateChanges && visibleText.length > stableLength) {
      const newText = visibleText.slice(stableLength);

      for (const match of newText.matchAll(/\S+/gu)) {
        const wordStart = stableLength + (match.index ?? 0);
        wordFragments.push({
          start: wordStart,
          end: wordStart + match[0].length,
          startedAt: now
        });
      }
    }

    applyWordFragments(element, wordFragments, now);
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
  fragments: WordFragment[],
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

  const wrapper = document.createElement('span');
  wrapper.className = 'stream-fade-fragment';
  animatedNode.parentNode?.replaceChild(wrapper, animatedNode);
  wrapper.appendChild(animatedNode);

  if (!prefersReducedMotion.matches && elapsed < ANIMATION_DURATION_MS) {
    const animation = wrapper.animate(
      [
        {
          opacity: 0,
          transform: 'translateY(0.65px)'
        },
        {
          opacity: 1,
          transform: 'translateY(0)'
        }
      ],
      {
        duration: ANIMATION_DURATION_MS,
        easing: 'cubic-bezier(0.18, 0.72, 0.28, 1)',
        fill: 'both'
      }
    );

    animation.currentTime = Math.min(elapsed, ANIMATION_DURATION_MS);
    animation.onfinish = () => animation.cancel();
  }
}
</script>

<template>
  <div ref="messageElement" class="markdown-message" v-html="renderedContent"></div>
</template>
