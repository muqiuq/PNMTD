<script setup lang="ts">
import { computed, inject, ref, watch } from 'vue'
import { imageUrl } from '../images'
import { imageLoaderKey, motionKey } from '../inject'
import type { BoardElement, BoardElementData } from '../types'

/**
 * An uploaded image (docs/board-messages-plan.md §9.4): fetched once per screen and shown from a blob: URL, since an <img> can't send
 * the board's token. Contain, cover or its own size; a background and a caption. With motion off, an animated image shows its first
 * frame: drawn once to a canvas, as a GIF in an <img> can't be paused.
 */
const props = defineProps<{ element: BoardElement; data: BoardElementData; fit: boolean }>()
const load = inject(imageLoaderKey, null)
const motion = inject(motionKey, ref(true))
const s = computed(() => props.element.settings)
const image = computed(() => props.data.image ?? null)

const url = ref<string | null>(null)
const failed = ref(false)

watch(
  () => [image.value?.fileId, motion.value] as const,
  async ([id, moving]) => {
    failed.value = false
    if (!id || !load) {
      url.value = null
      return
    }
    try {
      url.value = await imageUrl(load, id, !moving)
    } catch {
      url.value = null
      failed.value = true
    }
  },
  { immediate: true },
)

/** A share of the column's height in fit mode: the image fills it. Otherwise as tall as its aspect ratio makes it. */
const fill = computed(() => props.fit && props.element.height != null)
const ratio = computed(() =>
  image.value?.width && image.value?.height ? `${image.value.width} / ${image.value.height}` : undefined,
)
const alt = computed(() => s.value.alt ?? s.value.caption ?? 'Image')
</script>

<template>
  <figure class="board-image" :data-fill="fill ? 'true' : undefined" :data-background="s.background ?? 'None'">
    <div class="frame" :style="fill ? undefined : { aspectRatio: ratio }">
      <img v-if="url" :src="url" :alt="alt" :data-image-fit="s.imageFit ?? 'Contain'" data-test="board-image" />
      <span v-else-if="failed" class="board-muted">The image couldn't be loaded.</span>
    </div>
    <figcaption v-if="s.caption" class="caption">{{ s.caption }}</figcaption>
  </figure>
</template>

<style scoped>
.board-image {
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 0.3em;
  min-height: 0;
}

.board-image[data-fill='true'] {
  flex: 1 1 auto;
}

.frame {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 0;
  max-height: 80vh;
  overflow: hidden;
  border-radius: var(--board-radius);
}

.board-image[data-fill='true'] .frame {
  flex: 1 1 0;
  max-height: none;
}

.board-image[data-background='Black'] .frame {
  background: #000;
}

.board-image[data-background='White'] .frame {
  background: #fff;
}

img {
  display: block;
  width: 100%;
  height: 100%;
}

img[data-image-fit='Contain'] {
  object-fit: contain;
}

img[data-image-fit='Cover'] {
  object-fit: cover;
}

img[data-image-fit='Original'] {
  object-fit: scale-down;
}

.caption {
  text-align: center;
  font-size: 0.9em;
}
</style>
