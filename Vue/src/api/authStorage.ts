/**
 * 登录态本地存储:令牌与过期时刻在 localStorage 中的读写与过期判定。
 *
 * 本模块是**零依赖叶子模块**,供 `client.ts`(读取令牌注入请求头)与 `stores/auth.ts`
 * (读写会话)共享。若令牌读写内联在 store 中,`client.ts` 就得反向依赖 store,与
 * `client.ts → stores/auth.ts → api/auth.ts → client.ts` 形成循环。
 */

/** localStorage 键名常量,集中管理,禁止在业务代码中散落字符串字面量 */
export const STORAGE_KEYS = {
  /** 访问令牌(JWT) */
  accessToken: 'dimage.accessToken',
  /** 令牌过期时刻(ISO 8601) */
  expiresAt: 'dimage.expiresAt',
} as const

/** 安全读取 localStorage:隐私模式或安全策略拦截时降级为空 */
function readItem(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}

/** 安全写入 localStorage:配额超限或隐私模式时静默放弃持久化 */
function writeItem(key: string, value: string): void {
  try {
    localStorage.setItem(key, value)
  } catch {
    // 降级:本次会话内仍由 store 内存态维持登录,仅刷新后丢失
  }
}

/** 安全移除 localStorage 项 */
function removeItem(key: string): void {
  try {
    localStorage.removeItem(key)
  } catch {
    // 忽略:清理失败不影响内存态登出
  }
}

/** 过期时刻是否已失效;缺失或无法解析一律视为失效(宁可多要求一次登录) */
function isExpired(expiresAt: string | null): boolean {
  if (expiresAt === null) {
    return true
  }

  const timestamp = Date.parse(expiresAt)
  return Number.isNaN(timestamp) || timestamp <= Date.now()
}

/**
 * 读取访问令牌。
 * 令牌缺失或已过有效期时返回 `null`,并顺手清理本地残留,
 * 避免过期会话在界面态上被误判为已登录。
 */
export function getAccessToken(): string | null {
  const token = readItem(STORAGE_KEYS.accessToken)
  const expiresAt = readItem(STORAGE_KEYS.expiresAt)

  if (token !== null && !isExpired(expiresAt)) {
    return token
  }

  if (token !== null || expiresAt !== null) {
    clearSession()
  }

  return null
}

/** 写入登录会话:令牌与后端返回的过期时刻(ISO 8601) */
export function setSession(token: string, expiresAt: string): void {
  writeItem(STORAGE_KEYS.accessToken, token)
  writeItem(STORAGE_KEYS.expiresAt, expiresAt)
}

/** 清除登录会话 */
export function clearSession(): void {
  removeItem(STORAGE_KEYS.accessToken)
  removeItem(STORAGE_KEYS.expiresAt)
}

/** 读取令牌过期时刻(ISO 8601);无有效会话时返回 `null` */
export function getExpiresAt(): string | null {
  const expiresAt = readItem(STORAGE_KEYS.expiresAt)
  return isExpired(expiresAt) ? null : expiresAt
}
