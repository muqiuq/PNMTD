<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import CommandLogTab from './udp/CommandLogTab.vue'
import FirmwareImagesTab from './udp/FirmwareImagesTab.vue'
import HostPickerDialog from './udp/HostPickerDialog.vue'
import ListenerCard from './udp/ListenerCard.vue'
import NodesTab from './udp/NodesTab.vue'
import UnknownSendersTab from './udp/UnknownSendersTab.vue'
import UpdatesTab from './udp/UpdatesTab.vue'

/**
 * UDP nodes (docs/udp-iot-plan.md §6.6), Admin only: the listener, and as tabs (?tab=…, so they can
 * be linked) every node defined, the command log of all nodes, firmware images, updates and unknown
 * senders. Users and Viewers reach their nodes through hosts and sensors; this is the cross-node view.
 */
const route = useRoute()
const router = useRouter()

const status = useQuery({ queryKey: [...keys.udp, 'status'], queryFn: api.udp.status, refetchInterval: 10_000 })
const tab = computed(() => (typeof route.query.tab === 'string' ? route.query.tab : 'nodes'))
const tabs = computed(() => {
  const to = (t: string) => ({ name: 'udp-nodes', query: t === 'nodes' ? {} : { tab: t } })
  return [
    { key: 'nodes', label: 'Nodes', icon: 'pi pi-wifi', to: to('nodes'), badge: status.data.value?.nodes || null },
    { key: 'log', label: 'Command log', icon: 'pi pi-list', to: to('log') },
    { key: 'firmware', label: 'Firmware', icon: 'pi pi-box', to: to('firmware') },
    { key: 'updates', label: 'Updates', icon: 'pi pi-upload', to: to('updates') },
    { key: 'unknown', label: 'Unknown senders', icon: 'pi pi-question-circle', to: to('unknown') },
  ]
})
const adding = ref(false)
</script>

<template>
  <div class="page">
    <PageHeader title="UDP nodes">
      <template #actions>
        <Button
          label="Protocol"
          icon="pi pi-book"
          size="small"
          severity="secondary"
          text
          @click="router.push({ name: 'udp-protocol' })"
        />
        <Button label="Add UDP node" icon="pi pi-plus" size="small" @click="adding = true" />
      </template>
    </PageHeader>
    <ErrorState
      v-if="status.isError.value && !status.data.value"
      :error="status.error.value"
      what="the listener"
      @retry="status.refetch()"
    />
    <ListenerCard v-else-if="status.data.value" :status="status.data.value" />

    <TabNav :items="tabs" :active="tab" label="UDP sections" />
    <CommandLogTab v-if="tab === 'log'" />
    <FirmwareImagesTab v-else-if="tab === 'firmware'" />
    <UpdatesTab v-else-if="tab === 'updates'" />
    <UnknownSendersTab v-else-if="tab === 'unknown'" />
    <NodesTab v-else />

    <HostPickerDialog v-model:visible="adding" />
  </div>
</template>
