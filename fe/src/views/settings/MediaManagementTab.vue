<template>
  <div class="tab-content">
    <div class="media-management-tab">
      <div v-if="props.settings" class="settings-form">
        <FileManagementSection
          :settings="localSettings"
          @update:settings="val => Object.assign(localSettings, val)"
        />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { reactive, watch, nextTick } from 'vue'
import type { ApplicationSettings } from '@/types'
import FileManagementSection from '@/components/settings/FileManagementSection.vue'

interface Props {
  settings: ApplicationSettings | null
}

const props = defineProps<Props>()
const emit = defineEmits<{
  'update:settings': [value: ApplicationSettings | null]
}>()

const localSettings = reactive<ApplicationSettings>({} as ApplicationSettings)
let isSyncing = false

watch(
  () => props.settings,
  (val) => {
    if (val) {
      isSyncing = true
      for (const key of Object.keys(localSettings) as Array<keyof ApplicationSettings>) {
        delete (localSettings as unknown as Record<string, unknown>)[key as string]
      }
      Object.assign(localSettings, val)
      nextTick(() => { isSyncing = false })
    } else {
      isSyncing = true
      for (const key of Object.keys(localSettings) as Array<keyof ApplicationSettings>) {
        delete (localSettings as unknown as Record<string, unknown>)[key as string]
      }
      nextTick(() => { isSyncing = false })
    }
  },
  { immediate: true, deep: true },
)

watch(
  localSettings,
  (val) => {
    if (isSyncing) return
    emit('update:settings', { ...val })
  },
  { deep: true },
)
</script>
