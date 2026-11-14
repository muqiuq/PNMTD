<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Menu from 'primevue/menu'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'

/**
 * *New in Redmine* (§6.6): Redmine's own form for a new ticket, in a new tab, prefilled with the object's name and its link
 * in PNMT. PNMT doesn't learn the new ticket's number: it is linked afterwards (*Link ticket…* finds it at the top).
 */
const props = defineProps<{ subject: string; path: string }>()
const status = useQuery({ queryKey: [...keys.redmine, 'status'], queryFn: api.redmine.status, staleTime: 300_000 })
const projects = computed(() => status.data.value?.projects ?? [])
const menu = ref<InstanceType<typeof Menu> | null>(null)

function url(project: S['RedmineProjectItem']) {
  const link = `${location.origin}/pnmt-admin${props.path}`
  const params = new URLSearchParams({
    'issue[subject]': `${props.subject}: `,
    'issue[description]': `In PNMT: ${link}`,
  })
  return `${project.url}/issues/new?${params}`
}
const items = computed(() => projects.value.map((p) => ({ label: p.name, url: url(p), target: '_blank' })))
</script>

<template>
  <template v-if="projects.length === 1">
    <Button
      as="a"
      :href="url(projects[0]!)"
      target="_blank"
      rel="noopener noreferrer"
      label="New in Redmine"
      icon="pi pi-external-link"
      size="small"
      text
    />
  </template>
  <template v-else-if="projects.length > 1">
    <Button
      label="New in Redmine"
      icon="pi pi-external-link"
      size="small"
      text
      aria-haspopup="true"
      @click="(e) => menu?.toggle(e)"
    />
    <Menu ref="menu" :model="items" popup />
  </template>
</template>
