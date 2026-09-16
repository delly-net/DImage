import './assets/main.css'

import { createApp } from 'vue'
import { createPinia } from 'pinia'

import App from './App.vue'
import router from './router'
import { setUnauthorizedHandler } from './api/client'
import { useAuthStore } from './stores/auth'

const app = createApp(App)

app.use(createPinia())
app.use(router)

// 401 统一处理:清理登录态并回登录页。
// 装配点必须落在 main.ts —— 它是同时持有 pinia 与 router 的唯一位置;
// client.ts 与 stores/auth.ts 均不感知 router,以免形成循环依赖。
const auth = useAuthStore()

setUnauthorizedHandler(() => {
  auth.clearSession()

  const current = router.currentRoute.value

  // 已在登录页时不再跳转:并发 401 会重复触发本回调,
  // 重复 router.push 会产生冗余历史记录与 NavigationDuplicated 类警告。
  if (current.name === 'login') {
    return
  }

  router.push({ name: 'login', query: { redirect: current.fullPath } })
})

app.mount('#app')
