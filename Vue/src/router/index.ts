import { createRouter, createWebHistory } from 'vue-router'
import HomeView from '../views/HomeView.vue'
import HelpView from '../views/HelpView.vue'
import LoginView from '../views/LoginView.vue'
import { useAuthStore } from '@/stores/auth'
import { SITE_NAME } from '@/appInfo'

declare module 'vue-router' {
  interface RouteMeta {
    /** 公开路由:未登录也可访问,全局守卫对其放行 */
    public?: boolean
    /**
     * 不渲染全局头部(`App.vue` 中的 `AppHeader`)。
     *
     * 缺省为「渲染头部」,只有确需无壳的页面(如登录页)才显式声明为 `true`。
     */
    hideHeader?: boolean
    /**
     * **页面名**(非完整文档标题),文档标题为 `${title} - ${SITE_NAME}`。
     *
     * 缺省为「不声明」,此时文档标题回落为 `SITE_NAME` 本身(首页即此形态)。
     * 误把完整标题写进此字段会得到「小D图像 - 小D图像」这类重复。
     */
    title?: string
  }
}

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/',
      name: 'home',
      component: HomeView,
      // 不声明 meta.title:文档标题回落为站点名,首页即「小D图像」
    },
    {
      path: '/help',
      name: 'help',
      component: HelpView,
      // 不带 meta.public:受全局守卫保护,未登录访问将被拦截并带 redirect 跳登录页
      meta: { title: '帮助' },
    },
    {
      path: '/login',
      name: 'login',
      component: LoginView,
      meta: { public: true, hideHeader: true, title: '登录' },
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

/**
 * 全局后置钩子:按路由 `meta.title` 写入文档标题。
 *
 * 用 `afterEach` 而非 `beforeEach` —— 上面的守卫会重定向(未登录访问 `/help` → `/login`),
 * 前置钩子里的路由未必是最终落点,据其设置标题会先写错一次再被覆写。
 *
 * 形态:声明了 `meta.title` 的路由取 `${title} - ${SITE_NAME}`,未声明的(首页)直接取站点名。
 */
router.afterEach((to) => {
  const pageTitle = to.meta.title

  document.title = pageTitle ? `${pageTitle} - ${SITE_NAME}` : SITE_NAME
})

export default router
