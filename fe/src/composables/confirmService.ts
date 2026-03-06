import { ref } from 'vue'

type Resolver = (v: boolean) => void

// Singleton app-level confirm service
const visible = ref(false)
const title = ref('')
const message = ref('')
const confirmText = ref('Confirm')
const cancelText = ref('Cancel')
const danger = ref(false)
const checkboxLabel = ref('')
const checkboxWarning = ref('')
const checkboxChecked = ref(false)

let resolver: Resolver | null = null

export function showConfirm(
  msg: string,
  t?: string,
  options?: { confirmText?: string; cancelText?: string; danger?: boolean },
): Promise<boolean> {
  message.value = msg
  title.value = t || 'Confirm'
  confirmText.value = options?.confirmText ?? 'Confirm'
  cancelText.value = options?.cancelText ?? 'Cancel'
  danger.value = !!options?.danger
  visible.value = true
  return new Promise<boolean>((resolve) => {
    resolver = resolve
  })
}

export async function showDeleteConfirm(
  msg: string,
  t?: string,
): Promise<{ confirmed: boolean; deleteFiles: boolean }> {
  checkboxLabel.value = 'Also delete audio files from disk'
  checkboxWarning.value = 'The physical audio files will be permanently deleted from your file system.'
  checkboxChecked.value = false
  const ok = await showConfirm(msg, t, { danger: true, confirmText: 'Delete', cancelText: 'Cancel' })
  return { confirmed: ok, deleteFiles: checkboxChecked.value }
}

export function confirm(checked?: boolean) {
  checkboxChecked.value = checked ?? false
  if (resolver) resolver(true)
  resolver = null
  visible.value = false
  checkboxLabel.value = ''
  checkboxWarning.value = ''
}

export function cancel() {
  if (resolver) resolver(false)
  resolver = null
  visible.value = false
  checkboxLabel.value = ''
  checkboxWarning.value = ''
  checkboxChecked.value = false
}

export function useConfirmService() {
  return {
    visible,
    title,
    message,
    confirmText,
    cancelText,
    danger,
    checkboxLabel,
    checkboxWarning,
    checkboxChecked,
    showConfirm,
    showDeleteConfirm,
    confirm,
    cancel,
  }
}

// Default export convenience
export default {
  visible,
  title,
  message,
  confirmText,
  cancelText,
  danger,
  checkboxLabel,
  checkboxWarning,
  checkboxChecked,
  showConfirm,
  showDeleteConfirm,
  confirm,
  cancel,
  useConfirmService,
}
