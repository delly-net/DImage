<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { fetchHealth, type HealthResponse } from '@/api/health'
import { useAuthStore } from '@/stores/auth'

const router = useRouter()
const auth = useAuthStore()

type Status = 'loading' | 'success' | 'error'

const status = ref<Status>('loading')
const health = ref<HealthResponse | null>(null)
const errorMessage = ref('')

/** 令牌过期时刻的本地化展示,无法解析时原样回显 */
const expiresAtText = computed(() => {
  const raw = auth.expiresAt
  if (raw === null) {
    return '—'
  }

  const date = new Date(raw)
  return Number.isNaN(date.getTime()) ? raw : date.toLocaleString()
})

/** 调用后端健康检查,并把三种状态映射到界面 */
async function load() {
  status.value = 'loading'
  errorMessage.value = ''

  try {
    health.value = await fetchHealth()
    status.value = 'success'
  } catch (error) {
    health.value = null
    errorMessage.value = error instanceof Error ? error.message : String(error)
    status.value = 'error'
  }
}

/** 登出:清理登录态后回登录页;跳转职责在视图层,store 不依赖 router */
async function handleLogout(): Promise<void> {
  auth.logout()
  await router.push({ name: 'login' })
}

onMounted(load)
</script>

<template>
  <main class="home">
    <h1>小D图像</h1>
    <p class="subtitle">云端图片绘制能力服务 · 前后端联调状态</p>

    <section class="session">
      <p class="session-state">
        已登录 · 令牌有效期至
        <time :datetime="auth.expiresAt ?? undefined">{{ expiresAtText }}</time>
      </p>
      <button type="button" @click="handleLogout">退出登录</button>
    </section>

    <section class="card" aria-live="polite">
      <h2>后端服务状态</h2>

      <p v-if="status === 'loading'" class="state state--loading">正在检测…</p>

      <template v-else-if="status === 'success'">
        <p class="state state--ok">✓ 连通正常({{ health?.status }})</p>
        <dl>
          <dt>服务名</dt>
          <dd>{{ health?.service }}</dd>
          <dt>版本</dt>
          <dd>{{ health?.version }}</dd>
          <dt>服务端时间</dt>
          <dd>{{ health?.timestamp }}</dd>
        </dl>
      </template>

      <template v-else>
        <p class="state state--error">✗ 连接失败</p>
        <p class="error-detail">{{ errorMessage }}</p>
        <p class="hint">请确认后端已启动:dotnet run --project Api/DImage.Api</p>
      </template>

      <button type="button" :disabled="status === 'loading'" @click="load">重新检测</button>
    </section>
  </main>
</template>

<style scoped>
.home {
  max-width: 40rem;
  margin: 0 auto;
}

.subtitle {
  color: var(--color-text-muted, #6b7280);
}

.session {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem;
  align-items: center;
  justify-content: space-between;
  margin-top: 1.5rem;
  padding: 0.75rem 1rem;
  border: 1px solid var(--color-border, #e5e7eb);
  border-radius: 8px;
}

.session-state {
  margin: 0;
  font-size: 0.875rem;
  color: var(--color-text-muted, #6b7280);
}

.card {
  margin-top: 1.5rem;
  padding: 1.5rem;
  border: 1px solid var(--color-border, #e5e7eb);
  border-radius: 8px;
}

.card h2 {
  margin-top: 0;
  font-size: 1rem;
  font-weight: 600;
}

.state {
  font-weight: 600;
}

.state--ok {
  color: hsla(160, 100%, 25%, 1);
}

.state--error {
  color: #b91c1c;
}

.error-detail,
.hint {
  font-size: 0.875rem;
  color: var(--color-text-muted, #6b7280);
}

dl {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 0.25rem 1rem;
  margin: 0.75rem 0 1.25rem;
  font-size: 0.875rem;
}

dt {
  color: var(--color-text-muted, #6b7280);
}

dd {
  margin: 0;
}

button {
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
</style>
