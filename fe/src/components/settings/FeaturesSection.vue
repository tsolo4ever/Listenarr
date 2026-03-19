<template>
  <div class="form-section">
    <h3>
      <PhToggleLeft /> Features
    </h3>
    <div class="form-body">
      <CheckboxCard :modelValue="settings.enableMetadataProcessing" @update:modelValue="updateEnableMetadataProcessing" title="Enable Metadata Processing" description="Automatically fetch and embed audiobook metadata" />

      <CheckboxCard :modelValue="settings.enableCoverArtDownload" @update:modelValue="updateEnableCoverArtDownload" title="Enable Cover Art Download" description="Download and embed cover art for audiobooks" />

      <CheckboxCard :modelValue="settings.enableNotifications" @update:modelValue="updateEnableNotifications" title="Enable Notifications" description="Receive notifications for downloads and events" />

      <CheckboxCard :modelValue="settings.showCompletedExternalDownloads" @update:modelValue="updateShowCompletedExternalDownloads" title="Show completed external downloads in Activity" description="When enabled, completed torrents/NZBs from external clients will remain visible in the Activity view. When disabled, completed external items will be hidden to reduce clutter." />
    </div>
  </div>

  <div class="form-section monitoring-section">
    <h3>
      <PhBell /> Monitoring
    </h3>
    <div class="form-body">
      <CheckboxCard :modelValue="settings.authorMonitoringEnabled ?? true" @update:modelValue="updateAuthorMonitoringEnabled" title="Author &amp; Series Monitoring" description="Periodically check for new releases from authors and series in your library. New books are added as Wanted and searched automatically." />

      <div class="form-group" v-if="settings.authorMonitoringEnabled ?? true">
        <label>Check Interval (hours)</label>
        <input
          type="number"
          min="1"
          max="168"
          class="form-input"
          :value="settings.authorMonitoringIntervalHours ?? 24"
          @change="updateAuthorMonitoringIntervalHours(+($event.target as HTMLInputElement).value)"
        />
        <span class="form-help">How often to check for new releases. Default: 24 hours.</span>
      </div>

      <div class="form-group" v-if="settings.authorMonitoringEnabled ?? true">
        <label>RSS Feed URL <span class="optional">(optional)</span></label>
        <input
          type="url"
          class="form-input"
          placeholder="https://rss.app/feeds/..."
          :value="settings.authorMonitoringRssFeedUrl ?? ''"
          @change="updateAuthorMonitoringRssFeedUrl(($event.target as HTMLInputElement).value)"
        />
        <span class="form-help">
          Paste an RSS feed URL (e.g. from <a href="https://rss.app" target="_blank" rel="noopener">rss.app</a>) pointing to Audible new releases.
          Items are matched against monitored authors and series via ASIN lookup.
          Leave blank to use Audimeta author polling instead.
        </span>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import type { ApplicationSettings } from '@/types'
import { PhToggleLeft, PhBell } from '@phosphor-icons/vue'
// Checkbox controls are provided via `CheckboxCard` wrapper where used
import CheckboxCard from '@/components/settings/CheckboxCard.vue'

const props = defineProps<{ settings: Partial<ApplicationSettings> }>()
const emit = defineEmits<{
  'update:settings': [value: Partial<ApplicationSettings>]
}>()

function updateField(field: keyof ApplicationSettings, value: unknown) {
  const payload = { ...(props.settings || {}), [field]: value } as Partial<ApplicationSettings>
  emit('update:settings', payload)
}

function updateEnableMetadataProcessing(value: boolean) {
  updateField('enableMetadataProcessing', value)
}

function updateEnableCoverArtDownload(value: boolean) {
  updateField('enableCoverArtDownload', value)
}

function updateEnableNotifications(value: boolean) {
  updateField('enableNotifications', value)
}

function updateShowCompletedExternalDownloads(value: boolean) {
  updateField('showCompletedExternalDownloads', value)
}

function updateAuthorMonitoringEnabled(value: boolean) {
  updateField('authorMonitoringEnabled', value)
}

function updateAuthorMonitoringIntervalHours(value: number) {
  updateField('authorMonitoringIntervalHours', Math.max(1, value))
}

function updateAuthorMonitoringRssFeedUrl(value: string) {
  updateField('authorMonitoringRssFeedUrl', value)
}
</script>

<style scoped>
h3 {
  margin: 0 0 1.5rem 0;
  padding: 0;
  font-size: 1.1rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
}

/* Modal-like Features section */
.form-body { padding: 1.25rem; border-radius: 6px; border: 1px solid #333; box-shadow: 0 4px 14px rgba(0,0,0,0.6); background-color: #232323; }

.form-group { margin-bottom: 1.25rem }
.form-group label { margin-bottom:0.5rem; font-weight:500; color:#fff }
.form-help { display:block; margin-top:0.5rem; font-size:0.85rem; color:#adb5bd }
.form-help a { color: #adb5bd; text-decoration: underline; }
.optional { font-weight: 400; font-size: 0.85rem; color: #adb5bd; }
.form-input { width: 100%; padding: 0.5rem 0.75rem; background: #1a1a1a; border: 1px solid #444; border-radius: 4px; color: #fff; font-size: 0.9rem; }
.form-input[type="number"] { max-width: 120px; }
.monitoring-section { margin-top: 1.5rem; }
</style>
