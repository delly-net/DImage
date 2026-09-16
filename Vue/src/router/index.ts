import { createRouter, createWebHistory } from 'vue-router'
import HomeView from '../views/HomeView.vue'
import LoginView from '../views/LoginView.vue'
import { useAuthStore } from '@/stores/auth'

declare module 'vue-router' {
  interface RouteMeta {
    /** 公开路由:未登录也可访问,全局守卫对其放行 */
    public?: boolean
  }
}

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/',
      name: 'home',
      component: HomeView,
    },
    {
      path: '/login',
      name: 'login',
      component: LoginView,
      meta: { public: true },
    },
  ],
})

/**
 * 全局前置守卫:除标记 `meta.public` 的公开路由外,一律要求登录。
 *
 * 本文件单向依赖 auth store(`router → store`),store 不反向依赖 router,故无环。
 * 守卫只做界面态判断,不构成安全边界 —— 真正的鉴权由后端 JwtBearer 方案承担。
 */
router.beforeEach((to) => {
  const auth = useAuthStore()

  if (to.meta.public === true) {
    // 已登录再访问登录页无意义,直接回首页
    return to.name === 'login' && auth.isAuthenticated ? { path: '/' } : true
  }

  if (auth.isAuthenticated) {
    return true
  }

  return { name: 'login', query: { redirect: to.fullPath } }
})

export default router
