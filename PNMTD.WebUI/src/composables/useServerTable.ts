import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import { computed, ref, unref, watch, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { ListQuery } from '@/api/resources'

export interface Paged<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

interface Options<T, P extends Paged<T>> {
  key: readonly unknown[]
  /** A page, or an answer with more than `Paged`'s fields (Redmine's search has its freshness), kept in `query.data`. */
  fetch: (query: ListQuery & Record<string, unknown>) => Promise<P>
  defaultSort?: string
  pageSize?: number
  /** Extra query fields (filters), reactive; a change goes back to page 1. */
  filters?: Ref<Record<string, unknown>>
  /** Keep page, sort and search in the URL (?page=2&sort=-name&q=web), so a list can be linked. */
  syncUrl?: boolean
  /**
   * With syncUrl: more keys the list keeps in the URL (its filters), written with the page, sort and search in one replace, so the
   * two never race; undefined removes a key.
   */
  urlQuery?: Ref<Record<string, string | undefined>>
  /** Default 30 s; a ref to change it (Live: 10 s). */
  refetchInterval?: number | Ref<number>
  /** Off: nothing is fetched (a tab not shown); on again, a stale page is fetched at once. */
  enabled?: Ref<boolean>
}

/**
 * A v1 list as a PrimeVue DataTable in lazy mode (§5.1): server paging, sorting and search. The sort
 * goes to the API as "field" or "-field"; the search is debounced.
 */
export function useServerTable<T, P extends Paged<T> = Paged<T>>(options: Options<T, P>) {
  const route = useRoute()
  const router = useRouter()
  const fromUrl = options.syncUrl ? route.query : {}
  const page = ref(Number(fromUrl.page) || 1)
  const pageSize = ref(Number(fromUrl.pageSize) || options.pageSize || 50)
  const sort = ref(typeof fromUrl.sort === 'string' ? fromUrl.sort : (options.defaultSort ?? ''))
  const search = ref(typeof fromUrl.q === 'string' ? fromUrl.q : '')
  const debounced = ref(search.value)
  let timer: ReturnType<typeof setTimeout> | undefined
  watch(search, (value) => {
    clearTimeout(timer)
    timer = setTimeout(() => {
      debounced.value = value
      page.value = 1
    }, 250)
  })
  if (options.filters) {
    watch(options.filters, () => (page.value = 1), { deep: true })
  }

  const params = computed(() => {
    const extra = Object.fromEntries(
      Object.entries(options.filters?.value ?? {}).filter(([, v]) => v !== undefined && v !== null && v !== ''),
    )
    return {
      page: page.value,
      pageSize: pageSize.value,
      sort: sort.value || undefined,
      search: debounced.value.trim() || undefined,
      ...extra,
    }
  })

  if (options.syncUrl) {
    watch([params, () => options.urlQuery?.value], ([p]) => {
      const query = { ...route.query }
      for (const [k, v] of Object.entries(options.urlQuery?.value ?? {})) {
        if (v === undefined) delete query[k]
        else query[k] = v
      }
      const set = (k: string, v: unknown, empty: unknown) => {
        if (v === undefined || v === empty || v === '') delete query[k]
        else query[k] = String(v)
      }
      set('page', p.page, 1)
      set('pageSize', p.pageSize, options.pageSize ?? 50)
      set('sort', p.sort, options.defaultSort ?? '')
      set('q', p.search, undefined)
      void router.replace({ query })
    })
  }

  const query = useQuery({
    queryKey: computed(() => [...options.key, params.value]),
    queryFn: () => options.fetch(params.value),
    placeholderData: keepPreviousData,
    refetchInterval: computed(() => unref(options.refetchInterval) ?? 30_000),
    enabled: options.enabled ?? true,
  })

  const items = computed(() => query.data.value?.items ?? [])
  const total = computed(() => query.data.value?.totalCount ?? 0)

  /** v-bind on a DataTable (with :lazy="true", paginator). */
  const tableProps = computed(() => ({
    value: items.value,
    lazy: true,
    paginator: total.value > pageSize.value,
    rows: pageSize.value,
    first: (page.value - 1) * pageSize.value,
    totalRecords: total.value,
    loading: query.isFetching.value && !query.data.value,
    sortField: sort.value.replace(/^-/, '') || undefined,
    sortOrder: sort.value ? (sort.value.startsWith('-') ? -1 : 1) : 0,
    rowsPerPageOptions: [25, 50, 100, 200],
  }))

  function onPage(event: { page: number; rows: number }) {
    page.value = event.page + 1
    pageSize.value = event.rows
  }

  function onSort(event: { sortField?: unknown; sortOrder?: number | null }) {
    const field = typeof event.sortField === 'string' ? event.sortField : ''
    sort.value = field ? `${event.sortOrder === -1 ? '-' : ''}${field}` : (options.defaultSort ?? '')
    page.value = 1
  }

  /** The mobile list's pager. */
  function goTo(target: number) {
    page.value = Math.max(1, target)
  }

  return { query, items, total, page, pageSize, sort, search, params, tableProps, onPage, onSort, goTo }
}
