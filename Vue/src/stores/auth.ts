/**
 * 登录态 store:令牌、过期时刻与派生状态。
 *
 * 本模块**不得** import router:401 的跳转职责集中在 `main.ts` 装配处。
 * 否则会形成 `router → views/LoginView.vue → stores/auth.ts → router` 循环依赖。
 * 同理,store 也不直接读写 localStorage,统一经零依赖的 `api/authStorage` 收口。
 */

import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { login as requestToken } from '@/api/auth'
import {
  clearSession as clearStoredSession,
  getAccessToken,
  getExpiresAt,
  setSession,
} from '@/api/authStorage'

export const useAuthStore = defineStore('auth', () => {
  const accessToken = ref<string | null>(null)
  const expiresAt = ref<string | null>(null)

  /** 是否已登录;令牌过期由 authStorage 在读取时判定并清理 */
  const isAuthenticated = computed(() => accessToken.value !== null)

  /** 从本地存储恢复会话(令牌缺失或已过期时保持未登录) */
  function restore(): void {
    accessToken.value = getAccessToken()
    expiresAt.value = accessToken.value === null ? null : getExpiresAt()
  }

  /** 用管理密码登录,成功后写入内存态与本地存储;失败时抛出,由调用方展示 */
  async function login(password: string): Promise<void> {
    const token = await requestToken(password)

    accessToken.value = token.accessToken
    expiresAt.value = token.expiresAt
    setSession(token.accessToken, token.expiresAt)
  }

  /** 静默清理登录态(内存 + 本地存储),不承担跳转职责,供 401 处理器与登出流程共用 */
  function clearSession(): void {
    accessToken.value = null
    expiresAt.value = null
    clearStoredSession()
  }

  /**
   * 登出:清理登录态。
   * 跳转登录页由调用方(视图)负责 —— store 不 import router,
   * 以免形成 `router → view → store → router` 环。
   */
  function logout(): void {
    clearSession()
  }

  // 建 store 时即恢复会话,使刷新页面保持登录
  restore()

  return { accessToken, expiresAt, isAuthenticated, login, logout, clearSession, restore }
})
