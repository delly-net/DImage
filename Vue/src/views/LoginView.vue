<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { resolveAuthErrorMessage } from '@/api/auth'
import { useAuthStore } from '@/stores/auth'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const password = ref('')
const errorMessage = ref('')
const submitting = ref(false)

/**
 * 解析登录成功后的回跳地址。
 *
 * 只接受站内相对路径:`//evil.com` 被浏览器解析为协议相对绝对地址,
 * `https://evil.com` 为完整外站地址,二者都必须拒绝,否则构成开放重定向。
 * 故要求以单个 `/` 开头 —— `^\/(?![/\\])` 同时挡掉 `//` 与 `/\`。
 */
function resolveRedirect(): string {
  const redirect = route.query.redirect
  const target = typeof redirect === 'string' ? redirect : ''

  return /^\/(?![/\\])/.test(target) ? target : '/'
}

async function submit(): Promise<void> {
  if (submitting.value) {
    return
  }

  // 空密码前端直接拦截,不发起请求,与后端 password_required 形成双重防线
  if (password.value === '') {
    errorMessage.value = '请输入管理密码'
    return
  }

  submitting.value = true
  errorMessage.value = ''

  try {
    await auth.login(password.value)
    // replace 而非 push:避免用户回退时又回到登录页
    await router.replace(resolveRedirect())
  } catch (error) {
    errorMessage.value = resolveAuthErrorMessage(error)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <main class="login">
    <h1>小D图像</h1>
    <p class="subtitle">云端图片绘制能力服务 · 管理端</p>

    <form class="card" @submit.prevent="submit">
      <h2>管理用户登录</h2>

      <label for="password">管理密码</label>
      <input
        id="password"
        v-model="password"
        type="password"
        name="password"
        autocomplete="current-password"
        :disabled="submitting"
      />

      <p class="error" aria-live="polite">{{ errorMessage }}</p>

      <button type="submit" :disabled="submitting">
        {{ submitting ? '登录中…' : '登录' }}
      </button>

      <p class="hint">密码由后端环境变量 PASSWORD 提供,登录态保存在本浏览器。</p>
    </form>
  </main>
</template>

<style scoped>
.login {
  max-width: 24rem;
  margin: 0 auto;
}

.subtitle {
  color: var(--color-text-muted, #6b7280);
}

.card {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  margin-top: 1.5rem;
  padding: 1.5rem;
  border: 1px solid var(--color-border, #e5e7eb);
  border-radius: 8px;
}

.card h2 {
  margin: 0 0 0.5rem;
  font-size: 1rem;
  font-weight: 600;
}

label {
  font-size: 0.875rem;
  color: var(--color-text-muted, #6b7280);
}

input {
  padding: 0.5rem 0.75rem;
  border: 1px solid var(--color-border, #e5e7eb);
  border-radius: 6px;
  background: transparent;
  color: inherit;
  font: inherit;
}

input:disabled {
  opacity: 0.6;
}

.error {
  min-height: 1.25rem;
  margin: 0;
  font-size: 0.875rem;
  color: #b91c1c;
}

button {
  margin-top: 0.25rem;
  padding: 0.5rem 1rem;
  border: 1px solid var(--color-border, #e5e7eb);
  border-radius: 6px;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

button:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}

.hint {
  margin: 0.25rem 0 0;
  font-size: 0.8125rem;
  color: var(--color-text-muted, #6b7280);
}
</style>
