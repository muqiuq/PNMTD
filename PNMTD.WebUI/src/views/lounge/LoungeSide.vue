<script setup lang="ts">
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { axisPercent, strength } from './loungeFormat'

/**
 * The lounge's side panel (docs/lounge-plan.md §15.2): what each voice will never forget (its three keepsakes, §7.4), and
 * the convictions as two-marker axes with the last move and its reason (§6). No chart library: the axis is a line and
 * two markers.
 */
const props = defineProps<{ about: S['LoungeAbout'] | null }>()
const voiceName = (voice: string) => (voice === 'seraph' ? 'Seraph' : (props.about?.cookieName ?? 'Cookie'))
const initial = (voice: string) => voiceName(voice).slice(0, 1).toUpperCase()
const title = (voice: string, position: number) => `${voiceName(voice)}: ${strength(position)} (${position.toFixed(2)})`
</script>

<template>
  <aside class="side" aria-label="Never forgotten, and the convictions">
    <section>
      <h2 class="heading">Never forgotten</h2>
      <template v-for="voice in ['cookie', 'seraph']" :key="voice">
        <h3 class="voice" :class="voice">{{ voiceName(voice) }}</h3>
        <ul v-if="(voice === 'cookie' ? about?.cookie : about?.seraph)?.length" class="keepsakes">
          <li v-for="k in voice === 'cookie' ? about!.cookie : about!.seraph" :key="k.id">{{ k.text }}</li>
        </ul>
        <p v-else class="muted small">Nothing yet: each night, {{ voiceName(voice) }} may keep up to three.</p>
      </template>
    </section>
    <section v-if="about?.convictions.length">
      <h2 class="heading">Convictions</h2>
      <div v-for="c in about.convictions" :key="c.key" class="conviction">
        <div class="question">{{ c.question }}</div>
        <div class="axis" role="img" :aria-label="`${title('cookie', c.cookie)}; ${title('seraph', c.seraph)}`">
          <span class="line" />
          <span
            class="marker cookie"
            :class="{ owner: c.owner === 'cookie' }"
            :style="{ left: `${axisPercent(c.cookie)}%` }"
            :title="title('cookie', c.cookie)"
            >{{ initial('cookie') }}</span
          >
          <span
            class="marker seraph"
            :class="{ owner: c.owner === 'seraph' }"
            :style="{ left: `${axisPercent(c.seraph)}%` }"
            :title="title('seraph', c.seraph)"
            >S</span
          >
        </div>
        <div class="poles small muted">
          <span :title="c.minus">{{ c.minus }}</span>
          <span :title="c.plus">{{ c.plus }}</span>
        </div>
        <p v-if="c.lastMove" class="move small">
          {{ voiceName(c.lastMove.voice) }} moved a little, <RelativeTime :value="c.lastMove.at" />: “{{
            c.lastMove.why
          }}”
        </p>
      </div>
    </section>
  </aside>
</template>

<style scoped>
.side {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-4);
  padding: var(--pnmt-space-3);
}

.heading {
  margin: 0 0 var(--pnmt-space-2);
  font-size: 1rem;
}

.voice {
  margin: var(--pnmt-space-2) 0 var(--pnmt-space-1);
  font-size: 0.857rem;
  font-weight: 600;
}

.voice.seraph {
  color: var(--p-violet-700);
}

:root.pnmt-dark .voice.seraph {
  color: var(--p-violet-300);
}

.keepsakes {
  margin: 0;
  padding-left: 1.1rem;
}

.keepsakes li + li {
  margin-top: 2px;
}

.conviction + .conviction {
  margin-top: var(--pnmt-space-3);
}

.question {
  font-weight: 500;
}

/* Inset by a marker's half width and the owner's ring (11 + 4 px, and a little air), so that ±1 stays inside the panel. */
.axis {
  position: relative;
  height: 32px;
  margin: var(--pnmt-space-1) 18px 0;
}

.line {
  position: absolute;
  left: 0;
  right: 0;
  top: 50%;
  border-top: 2px solid var(--pnmt-border);
}

.marker {
  position: absolute;
  top: 50%;
  transform: translate(-50%, -50%);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border-radius: 50%;
  font-size: 0.75rem;
  font-weight: 700;
  color: var(--p-primary-contrast-color);
  background: var(--p-primary-color);
}

.marker.seraph {
  background: var(--p-violet-500);
}

.marker.owner {
  box-shadow:
    0 0 0 2px var(--pnmt-panel),
    0 0 0 4px currentColor;
}

.poles {
  display: flex;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
}

/* The poles are sentences: up to three lines each, the whole in the title. */
.poles span {
  max-width: 48%;
  display: -webkit-box;
  -webkit-line-clamp: 3;
  line-clamp: 3;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.poles span:last-child {
  text-align: right;
}

.move {
  margin: var(--pnmt-space-1) 0 0;
}

.small {
  font-size: 0.857rem;
}
</style>
