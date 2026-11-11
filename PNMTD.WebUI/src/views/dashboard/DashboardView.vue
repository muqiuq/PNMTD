<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import EmptyState from '@/components/EmptyState.vue'
import { useSession } from '@/stores/session'

// The dashboard of §5.5 arrives in phase 6.
const session = useSession()
</script>

<template>
  <div class="page">
    <PageHeader title="Dashboard" />
    <div class="panel">
      <EmptyState
        icon="pi pi-th-large"
        :title="`Signed in as ${session.user?.displayName} (${session.role})`"
        text="The dashboard arrives with the feature pages (phase 6)."
      />
    </div>
  </div>
</template>
