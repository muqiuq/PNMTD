<script setup lang="ts">
import QRCode from 'qrcode'
import { ref, watchEffect } from 'vue'

/** A QR code drawn in the browser (§5.1): the otpauth:// URI never leaves the page. */
const props = withDefaults(defineProps<{ text: string; size?: number; label?: string }>(), {
  size: 184,
  label: 'QR code',
})
const src = ref('')

watchEffect(async () => {
  src.value = await QRCode.toDataURL(props.text, { width: props.size, margin: 1, errorCorrectionLevel: 'M' })
})
</script>

<template>
  <img v-if="src" class="qr-code" :src="src" :width="size" :height="size" :alt="label" />
</template>

<style scoped>
.qr-code {
  display: block;
  /* White behind the code in the dark theme too: phone cameras read dark on light. */
  background: #fff;
  padding: 4px;
  border-radius: var(--p-border-radius-md);
}
</style>
