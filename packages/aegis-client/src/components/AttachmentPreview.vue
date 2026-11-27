<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue';
import { attachmentSize, type DraftAttachment } from '../services/chatAttachments';

const { file } = defineProps<{ file: DraftAttachment }>();
defineEmits<{ remove: [] }>();

const extension = /\.([a-z0-9]{1,10})$/i.exec(file.name)?.[1] ?? '';
const isImage = file.type.startsWith('image/') ||
  /^(avif|bmp|gif|heic|heif|ico|jpe?g|jfif|png|svg|tiff?|webp)$/i.test(extension);
const objectUrl = isImage && typeof file.source !== 'string' ? URL.createObjectURL(file.source) : undefined;
const thumbnail = ref(objectUrl ?? file.previewUrl);
const description = `${file.name} · ${attachmentSize(file.size)}`;
onBeforeUnmount(() => { if (objectUrl) URL.revokeObjectURL(objectUrl); });
</script>

<template>
  <li class="attachment-preview" :title="description">
    <img v-if="thumbnail" class="attachment-preview-image" :src="thumbnail" :alt="file.name"
      decoding="async" draggable="false" @error="thumbnail = undefined" />
    <template v-else>
      <div class="attachment-preview-symbol" aria-hidden="true">
        <svg viewBox="0 0 32 36" fill="none">
          <path d="M19 3H8a3 3 0 0 0-3 3v24a3 3 0 0 0 3 3h16a3 3 0 0 0 3-3V11L19 3Z" />
          <path d="M19 3v6a2 2 0 0 0 2 2h6" />
        </svg>
        <span v-if="extension && extension.length <= 5" class="attachment-preview-type">{{ extension }}</span>
      </div>
      <span class="attachment-preview-name">{{ file.name }}</span>
    </template>
    <button class="attachment-preview-remove" type="button" :aria-label="`Remover ${file.name}`" @click="$emit('remove')">
      <svg viewBox="0 0 16 16" aria-hidden="true"><path d="m4.5 4.5 7 7m0-7-7 7" /></svg>
    </button>
  </li>
</template>

<style scoped>
.attachment-preview {
  position: relative;
  display: grid;
  grid-template-rows: 1fr auto;
  flex: 0 0 var(--preview-size);
  height: var(--preview-size);
  min-width: 0;
  overflow: hidden;
  border-radius: 12px;
  background: var(--canvas);
  color: var(--text);
}
.attachment-preview-image {
  width: 100%;
  height: 100%;
  grid-row: 1 / -1;
  object-fit: cover;
}
.attachment-preview-symbol {
  position: relative;
  align-self: center;
  justify-self: center;
  color: var(--muted);
}
.attachment-preview-symbol svg { width: 28px; height: 32px; stroke: currentColor; stroke-width: 1.5; stroke-linejoin: round; }
.attachment-preview-type {
  position: absolute;
  bottom: 4px;
  left: 50%;
  transform: translateX(-50%);
  padding: 0 2px;
  background: var(--canvas);
  color: var(--accent-strong);
  font-size: 9px;
  line-height: 1.2;
  font-weight: 650;
  letter-spacing: .035em;
  text-transform: uppercase;
}
.attachment-preview-name {
  padding: 7px 8px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  background: var(--surface);
  font-size: .75rem;
  line-height: 1.3;
}
.attachment-preview-remove {
  position: absolute;
  inset: 0 0 auto auto;
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  border: 0;
  background: transparent;
  cursor: pointer;
}
.attachment-preview-remove svg {
  display: block;
  width: 21px;
  height: 21px;
  padding: 3px;
  border-radius: 50%;
  background: rgba(16, 24, 39, .85);
  color: white;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.7;
  stroke-linecap: round;
}
.attachment-preview-remove:focus-visible { outline: 2px solid var(--accent); outline-offset: -3px; border-radius: 9px; }
@media (hover: hover) and (pointer: fine) {
  .attachment-preview-remove { opacity: 0; }
  .attachment-preview:hover .attachment-preview-remove,
  .attachment-preview:focus-within .attachment-preview-remove { opacity: 1; }
}
@media (pointer: coarse) {
  .attachment-preview-remove { width: 40px; height: 40px; align-items: start; justify-items: end; padding: 5px; }
}
</style>
