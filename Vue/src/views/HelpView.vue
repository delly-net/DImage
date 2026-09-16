<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { fetchMcpTools, type McpToolsResponse } from '@/api/mcp'

type Status = 'loading' | 'success' | 'error'

const status = ref<Status>('loading')
const info = ref<McpToolsResponse | null>(null)
const errorMessage = ref('')

/**
 * 接入地址与凭据一律以占位符呈现,本页任何位置都不出现真实 TOKEN 值。
 *
 * 示例文本必须定义为 JS 字符串常量再经 `{{ }}` 插值渲染:其中的 `<TOKEN>`、
 * `<DImage 服务地址>` 若字面写进模板,会被 Vue 模板编译器当作未注册的 HTML
 * 标签解析,渲染残缺且不报错。同理,本页不使用 `v-html`。
 */
const PLACEHOLDER_BASE = 'https://<DImage 服务地址>'
const PLACEHOLDER_MCP_URL = `${PLACEHOLDER_BASE}/mcp`
const PLACEHOLDER_AUTH = 'Bearer <TOKEN>'

/** 两个客户端的 MCP 配置结构一致(HTTP 传输),差异仅在配置文件位置 */
const CLIENTS = [
  { name: 'Claude Code', configFile: '.mcp.json(项目根目录)' },
  { name: 'Claude Desktop', configFile: 'claude_desktop_config.json' },
]

const CONFIG_EXAMPLE = `{
  "mcpServers": {
    "dimage": {
      "type": "http",
      "url": "${PLACEHOLDER_MCP_URL}",
      "headers": { "Authorization": "${PLACEHOLDER_AUTH}" }
    }
  }
}`

/** 当前真实可用的连通性自检端点,可用于实际验证 TOKEN 是否配置正确 */
const VERIFY_PATH = 'POST /api/v1/mcp/verify'
const VERIFY_EXAMPLE = `curl -X POST "${PLACEHOLDER_BASE}/api/v1/mcp/verify" \\
  -H "Authorization: ${PLACEHOLDER_AUTH}"`

const tools = computed(() => info.value?.tools ?? [])

/** 拉取服务信息与工具清单,并把三种状态映射到界面 */
async function load() {
  status.value = 'loading'
  errorMessage.value = ''

  try {
    info.value = await fetchMcpTools()
    status.value = 'success'
  } catch (error) {
    info.value = null
    errorMessage.value = error instanceof Error ? error.message : String(error)
    status.value = 'error'
  }
}

onMounted(load)
</script>

<template>
  <main class="help">
    <h1>帮助</h1>
    <p class="subtitle">MCP 接入配置与工具说明</p>

    <section class="card">
      <h2>服务信息</h2>

      <p v-if="status === 'loading'" class="state state--loading">正在加载…</p>

      <dl v-else-if="status === 'success'">
        <dt>服务名</dt>
        <dd>{{ info?.server.name }}</dd>
        <dt>服务版本</dt>
        <dd>{{ info?.server.version }}</dd>
        <dt>MCP 协议版本</dt>
        <dd>{{ info?.server.protocolVersion }}</dd>
        <dt>鉴权方案</dt>
        <dd>{{ info?.auth.scheme }}</dd>
        <dt>凭据请求头</dt>
        <dd>{{ info?.auth.headerName }}</dd>
      </dl>

      <p v-else class="state state--error">服务信息加载失败,详情见下方「支持的 MCP 工具」。</p>
    </section>

    <section class="card">
      <h2>MCP 配置方法</h2>

      <h3>1. 获取访问凭据</h3>
      <p>
        TOKEN 由部署方经环境变量提供,请向服务管理员索取。<strong>本页不展示凭据值。</strong>
      </p>

      <h3>2. 客户端接入配置</h3>
      <p class="hint">示例中的接入地址为占位符,请替换为部署方提供的实际地址。</p>

      <div v-for="client in CLIENTS" :key="client.name" class="client">
        <h4>{{ client.name }}</h4>
        <p class="hint">配置文件:{{ client.configFile }}</p>
        <pre>{{ CONFIG_EXAMPLE }}</pre>
      </div>

      <h3>3. 连通性自检</h3>
      <p>
        向 <code>{{ VERIFY_PATH }}</code> 发起请求并携带
        <code>{{ PLACEHOLDER_AUTH }}</code> 请求头,即可验证 TOKEN 是否配置正确。返回
        <code>valid: true</code> 表示凭据可用。
      </p>
      <pre>{{ VERIFY_EXAMPLE }}</pre>
    </section>

    <section class="card" aria-live="polite">
      <h2>支持的 MCP 工具</h2>

      <p v-if="status === 'loading'" class="state state--loading">正在加载…</p>

      <template v-else-if="status === 'success'">
        <ul v-if="tools.length > 0" class="tools">
          <li v-for="tool in tools" :key="tool.name">
            <strong>{{ tool.name }}</strong>
            <span class="hint">{{ tool.description }}</span>
          </li>
        </ul>

        <!-- 空态表述不依赖时间点:在任何部署阶段都成立,不会随服务上线而过期 -->
        <p v-else class="state state--empty">
          本页工具清单由服务端实时提供;若列表为空,表示服务尚未发布可用的 MCP 工具。
        </p>
      </template>

      <template v-else>
        <p class="state state--error">✗ 工具清单加载失败</p>
        <p class="error-detail">{{ errorMessage }}</p>
        <p class="hint">请确认后端已启动且登录态有效:dotnet run --project Api/DImage.Api</p>
      </template>

      <button type="button" :disabled="status === 'loading'" @click="load">重新检测</button>
    </section>
  </main>
</template>

<style scoped>
.help {
  max-width: 48rem;
  margin: 0 auto;

  /*
   * --color-text-muted 并未定义在 base.css 中,既有视图均以 var(..., #6b7280) 的 fallback 写法取值。
   * 该取值在深色背景(#181818)上对比度仅 3.67:1,不满足正文可读性。
   * 此处只在本页作用域内补一个深色取值,不把它补进 base.css —— 那会牵动全局样式。
   */
  --color-text-muted: #6b7280;
}

@media (prefers-color-scheme: dark) {
  .help {
    --color-text-muted: #9ca3af;
  }
}

.subtitle {
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

.card h3 {
  margin: 1.5rem 0 0.5rem;
  font-size: 0.9375rem;
  font-weight: 600;
}

.card h4 {
  margin: 0 0 0.25rem;
  font-size: 0.875rem;
  font-weight: 600;
}

.client {
  margin-top: 0.75rem;
}

.state {
  font-weight: 600;
}

.state--empty {
  color: var(--color-text-muted, #6b7280);
}

.state--error {
  color: #b91c1c;
}

/* 深色模式下暗红对比度不足,单独提亮;浅色模式沿用既有配色,与其他视图保持一致 */
@media (prefers-color-scheme: dark) {
  .state--error {
    color: #f87171;
  }
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
  margin: 0.75rem 0 0;
  font-size: 0.875rem;
}

dt {
  color: var(--color-text-muted, #6b7280);
}

dd {
  margin: 0;
  overflow-wrap: anywhere;
}

pre {
  margin: 0.5rem 0 0;
  padding: 0.75rem 1rem;
  overflow-x: auto;
  border: 1px solid var(--color-border, #e5e7eb);
  border-radius: 6px;
  background: var(--color-background-mute, #f2f2f2);
  font-size: 0.8125rem;
  line-height: 1.5;
}

code {
  padding: 0.1rem 0.35rem;
  border-radius: 4px;
  background: var(--color-background-mute, #f2f2f2);
  font-size: 0.875em;
}

.tools {
  margin: 0;
  padding-left: 1.25rem;
  font-size: 0.875rem;
}

.tools li + li {
  margin-top: 0.5rem;
}

.tools .hint {
  margin-left: 0.5rem;
}

button {
  margin-top: 1.25rem;
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
