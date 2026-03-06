<template>
  <Modal class="confirm-dialog" :visible="modelValue" :title="title || 'Confirm'" size="sm" @close="onCancel">
    <div class="confirm-body">
      <p>{{ stripHtmlAndNormalize(message) }}</p>
      <label v-if="checkboxLabel" class="confirm-checkbox">
        <input type="checkbox" v-model="localChecked" />
        {{ checkboxLabel }}
      </label>
      <p v-if="checkboxLabel && localChecked && checkboxWarning" class="confirm-warning">
        {{ checkboxWarning }}
      </p>
    </div>

    <template #footer>
      <button class="btn cancel" @click="onCancel">{{ cancelText }}</button>
      <button class="btn confirm" :class="{ danger }" @click="onConfirm">{{ confirmText }}</button>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { Modal } from '@/components/feedback'
import { stripHtmlAndNormalize } from '@/utils/textUtils'

defineProps({
  modelValue: { type: Boolean, required: true },
  title: { type: String, default: '' },
  message: { type: String, default: '' },
  confirmText: { type: String, default: 'Confirm' },
  cancelText: { type: String, default: 'Cancel' },
  danger: { type: Boolean, default: false },
  checkboxLabel: { type: String, default: '' },
  checkboxWarning: { type: String, default: '' },
})

const emit = defineEmits(['update:modelValue', 'confirm'])

const localChecked = ref(false)

function onConfirm() {
  emit('confirm', localChecked.value)
  emit('update:modelValue', false)
  localChecked.value = false
}

function onCancel() {
  emit('update:modelValue', false)
  localChecked.value = false
}
</script>

<style scoped>
.confirm-body p { color: #ddd; margin: 0 0 0.5rem 0; white-space: pre-wrap }

.confirm-checkbox {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-top: 0.75rem;
  color: #ccc;
  font-size: 0.9rem;
  cursor: pointer;
}

.confirm-checkbox input[type='checkbox'] {
  width: 15px;
  height: 15px;
  cursor: pointer;
  accent-color: #4dabf7;
}

.confirm-warning {
  margin-top: 0.5rem;
  font-size: 0.85rem;
  color: #ef4444;
}
</style>
