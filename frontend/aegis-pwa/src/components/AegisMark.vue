<script setup lang="ts">
import { useId } from 'vue';

const shieldId = `${useId()}-shield`;
const coreId = `${useId()}-core`;
</script>

<template>
  <svg class="aegis-mark" viewBox="0 0 96 96" aria-hidden="true" focusable="false">
    <defs>
      <linearGradient :id="shieldId" x1="18" x2="78" y1="12" y2="84">
        <stop offset="0" stop-color="#d9e4ff" />
        <stop offset="0.52" stop-color="#91a9ee" />
        <stop offset="1" stop-color="#5f72b4" />
      </linearGradient>
      <linearGradient :id="coreId" x1="34" x2="62" y1="24" y2="72">
        <stop offset="0" stop-color="#f4f7ff" />
        <stop offset="1" stop-color="#aebff2" />
      </linearGradient>
    </defs>

    <path
      class="aegis-mark__plate"
      d="M48 8 76 20v22c0 20.3-11.3 35.2-28 44-16.7-8.8-28-23.7-28-44V20L48 8Z"
      :fill="`url(#${shieldId})`"
    />
    <path
      class="aegis-mark__field"
      d="M48 18 66 26v16c0 13.3-7.1 23.5-18 30.2C37.1 65.5 30 55.3 30 42V26l18-8Z"
      fill="#172033"
    />
    <path
      class="aegis-mark__spine"
      d="M48 25 60.5 68h-8.7l-3.8-14-3.8 14h-8.7L48 25Z"
      :fill="`url(#${coreId})`"
    />
    <path
      class="aegis-mark__crossbar"
      d="M42.4 48.4h11.2L48 34.2l-5.6 14.2Z"
      fill="#172033"
      fill-opacity="0.78"
    />
    <path
      class="aegis-mark__glint"
      d="M48 12 72 22v7L48 19 24 29v-7L48 12Z"
      fill="#fff"
      fill-opacity="0.22"
    />
  </svg>
</template>
