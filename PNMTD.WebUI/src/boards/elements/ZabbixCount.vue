<script setup lang="ts">
import { severityLabel } from '@/shared/zabbix'
import type { BoardElementData } from '../types'

/** Zabbix's open problems (§7.5): the total, and a tile per severity from the element's minimum, worst first. */
defineProps<{ data: BoardElementData }>()
</script>

<template>
  <div v-if="data.zabbix" class="zabbix">
    <div class="total">
      <span class="board-big">{{ data.zabbix.total }}</span>
      <span class="board-muted">{{ data.zabbix.total === 1 ? 'problem' : 'problems' }}</span>
    </div>
    <div class="board-tiles severities">
      <div
        v-for="s in data.zabbix.severities"
        :key="s.severity"
        class="board-tile sev"
        :data-sev="s.count > 0 ? s.severity : undefined"
      >
        <span class="board-tile-value">{{ s.count }}</span>
        <span class="board-tile-label">{{ severityLabel(s.severity) }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.zabbix {
  display: flex;
  align-items: center;
  gap: 0.8em;
}

.total {
  display: flex;
  flex-direction: column;
  align-items: center;
}

.severities {
  flex: 1;
  grid-template-columns: repeat(auto-fit, minmax(5em, 1fr));
}

.sev[data-sev='Disaster'] {
  background: var(--board-sev-5);
  color: var(--board-sev-on);
}

.sev[data-sev='High'] {
  background: var(--board-sev-4);
  color: var(--board-sev-on);
}

.sev[data-sev='Average'] {
  background: var(--board-sev-3);
  color: var(--board-sev-on);
}

.sev[data-sev='Warning'] {
  background: var(--board-sev-2);
  color: var(--board-sev-on);
}

.sev[data-sev='Information'] {
  background: var(--board-sev-1);
  color: var(--board-sev-on);
}

.sev[data-sev='NotClassified'] {
  background: var(--board-sev-0);
  color: var(--board-sev-on);
}
</style>
