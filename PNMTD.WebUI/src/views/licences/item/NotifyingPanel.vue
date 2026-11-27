<script setup lang="ts">
import { computed } from 'vue'
import type { LicenseDetail } from '@/api/licenses'
import { useSession } from '@/stores/session'
import { notifyingLines } from './item'

/**
 * Who hears about the item (docs/licences-plan.md §5.2, §5.5, §8.7): the owner by e-mail (or that the owner has no e-mail address in
 * PNMT), the hosts' sensors through their rules, the people who chose items nobody owns, or nobody. Whether the owner turned these
 * e-mails off isn't shown: that choice is theirs; the owner reading it gets a link to it.
 */
const props = defineProps<{ item: LicenseDetail }>()
const session = useSession()
const lines = computed(() => notifyingLines(props.item.notifying))
const mine = computed(() => !!props.item.ownerId && session.user?.id === props.item.ownerId)
</script>

<template>
  <section class="panel" aria-labelledby="notifying-title">
    <div id="notifying-title" class="panel-header">Notifying</div>
    <div class="panel-body">
      <ul class="lines" data-test="notifying">
        <li v-for="line in lines" :key="line" :class="{ warn: line === 'nobody' || line.includes('no e-mail') }">
          {{ line.charAt(0).toUpperCase() + line.slice(1) }}
        </li>
      </ul>
      <p v-if="mine" class="muted small">
        You own it: what you are told is on
        <RouterLink :to="{ name: 'account', hash: '#notifications' }">Account → Notifications</RouterLink>.
      </p>
      <p v-else-if="item.status === 'Retired'" class="muted small">Retired: nobody is told about it any more.</p>
    </div>
  </section>
</template>

<style scoped>
.lines {
  margin: 0;
  padding-left: 1.1rem;
  display: grid;
  gap: var(--pnmt-space-1);
}

.warn {
  color: var(--pnmt-warn);
}

.small {
  margin: var(--pnmt-space-2) 0 0;
  font-size: 0.857rem;
}
</style>
