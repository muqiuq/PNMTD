<script setup lang="ts">
import Tag from 'primevue/tag'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import { useSession } from '@/stores/session'
import { caseLink, reviewStateLabel, reviewStateSeverity } from './access'

/**
 * A reviewed login's verdict (docs/cookie-access-plan.md §5.4, §10.4), on a target's Access tab and in the audit log: a
 * small badge that opens its access case in Cookie's window, for readers with Cookie: Use.
 */
const props = defineProps<{ review: S['AccessReviewRef'] }>()
const session = useSession()
const linked = computed(() => !!props.review.caseId && session.can('Cookie', 'Use'))
const label = computed(() => reviewStateLabel(props.review.state))
</script>

<template>
  <RouterLink
    v-if="linked"
    :to="caseLink(review.caseId!)"
    class="review"
    :title="`Reviewed: ${label}. Open its access case`"
  >
    <Tag :value="label" :severity="reviewStateSeverity(review.state)" icon="pi pi-shield" class="small" />
  </RouterLink>
  <Tag
    v-else
    :value="label"
    :severity="reviewStateSeverity(review.state)"
    icon="pi pi-shield"
    class="small"
    :title="`Reviewed: ${label}`"
  />
</template>

<style scoped>
.review {
  display: inline-flex;
  text-decoration: none;
}

.small {
  font-size: 0.857rem;
}
</style>
