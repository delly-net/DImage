/**
 * 最小 HTTP 请求封装:基于原生 fetch,提供 baseURL 拼接、超时、JSON 解析、非 2xx 抛错、
 * Authorization 自动注入与 401 未授权回调。
 *
 * 本模块是依赖图的**叶子节点**:**不得** import Pinia store 或 router,
 * 否则会形成 `client.ts → stores/auth.ts → api/auth.ts → client.ts` 循环依赖
 * (表现为运行期 undefined 或初始化顺序错乱,且 TypeScript 未必报错)。
 * 401 的「清登录态 + 跳转登录页」职责,由 `main.ts` 经 `setUnauthorizedHandler` 注册回调承担。
 */

import { getAccessToken } from './authStorage'

/** 请求基础路径,取自 VITE_API_BASE_URL;默认同源相对路径 */
const baseURL: string = import.meta.env.VITE_API_BASE_URL ?? '/'

/** 默认超时时间(毫秒) */
const DEFAULT_TIMEOUT = 10000

/** 后端错误码到界面文案的映射,集中管理;未命中时回退到通用文案 */
const ERROR_CODE_MESSAGES: Record<string, string> = {
  invalid_password: '密码错误',
  password_required: '请输入管理密码',
}

/** 请求失败错误,携带 HTTP 状态码与后端错误码 */
export class HttpError extends Error {
  readonly status: number

  /** 后端错误体中的 `error` 字段;响应体缺失、非 JSON 或格式不符时为 undefined */
  readonly errorCode?: string

  constructor(status: number, message: string, errorCode?: string) {
    super(message)
    this.name = 'HttpError'
    this.status = status
    this.errorCode = errorCode
  }
}

/** 401 处理回调;由 `main.ts` 装配,本模块自身不做任何跳转 */
let unauthorizedHandler: (() => void) | null = null

/**
 * 注册 401 未授权回调(通常为「清理登录态 + 跳转登录页」),传 `null` 注销。
 * 未注册时 401 仅抛出 `HttpError`,不触发跳转。
 */
export function setUnauthorizedHandler(handler: (() => void) | null): void {
  unauthorizedHandler = handler
}

export interface RequestOptions {
  /** 超时时间(毫秒),覆盖默认值 */
  timeout?: number
  /** 请求体,自动 JSON 序列化 */
  body?: unknown
  /** 外部取消信号 */
  signal?: AbortSignal
  /** 附加请求头,优先级高于自动注入的 Authorization */
  headers?: Record<string, string>
  /**
   * 跳过令牌注入与 401 全局登出。
   *
   * 登录接口必须置 `true`:其 401 语义是「密码错误」而非「登录态失效」,
   * 若一并触发全局登出,用户在登录页输错密码就会跳转叠加登录页,形成重定向死循环。
   * 该项须同时关闭「令牌注入」与「401 处理」,只关一半等于没关。
   */
  skipAuth?: boolean
}

/** 拼接完整 URL,绝对地址直接返回 */
function resolveUrl(path: string): string {
  if (/^https?:\/\//i.test(path)) {
    return path
  }
  return `${baseURL.replace(/\/$/, '')}/${path.replace(/^\//, '')}`
}

/** 后端错误响应体;字段可能缺失或类型不符,故按 unknown 逐层校验 */
interface ErrorBody {
  error?: unknown
}

/**
 * 从非 2xx 响应中安全解析后端错误码。
 *
 * 401/403 也可能来自其他端点且响应体为空或非 JSON(如 MCP 端点的 401 由认证 handler 决定),
 * 故解析全程容错:失败一律降级为 `undefined`,不得因解析异常掩盖原始 HTTP 状态。
 */
async function parseErrorCode(response: Response): Promise<string | undefined> {
  const contentType = response.headers.get('Content-Type') ?? ''
  if (!contentType.includes('json')) {
    return undefined
  }

  try {
    const text = await response.text()
    if (text === '') {
      return undefined
    }

    const body = JSON.parse(text) as ErrorBody | null
    const errorCode = body?.error
    return typeof errorCode === 'string' ? errorCode : undefined
  } catch {
    // 空响应体、截断 JSON 或非预期结构:降级为无错误码,保留原始 HTTP 状态
    return undefined
  }
}

async function request<T>(method: string, path: string, options: RequestOptions = {}): Promise<T> {
  const { timeout = DEFAULT_TIMEOUT, body, signal, headers, skipAuth = false } = options

  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeout)
  signal?.addEventListener('abort', () => controller.abort(), { once: true })

  // 非 skipAuth 且本地存在有效令牌时自动注入;调用方显式传入的 headers 优先级最高
  const token = skipAuth ? null : getAccessToken()

  try {
    const response = await fetch(resolveUrl(path), {
      method,
      headers: {
        Accept: 'application/json',
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...(token === null ? {} : { Authorization: `Bearer ${token}` }),
        ...headers,
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: controller.signal,
    })

    if (!response.ok) {
      const errorCode = await parseErrorCode(response)

      if (response.status === 401 && !skipAuth) {
        unauthorizedHandler?.()
      }

      const message =
        (errorCode === undefined ? undefined : ERROR_CODE_MESSAGES[errorCode]) ??
        `请求失败:${method} ${path} 返回 HTTP ${response.status}`

      throw new HttpError(response.status, message, errorCode)
    }

    if (response.status === 204) {
      return undefined as T
    }

    return (await response.json()) as T
  } catch (error) {
    if (error instanceof HttpError) {
      throw error
    }
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new Error(`请求超时:${method} ${path} 超过 ${timeout}ms 未响应`)
    }
    throw error
  } finally {
    clearTimeout(timer)
  }
}

export const http = {
  get: <T>(path: string, options?: RequestOptions) => request<T>('GET', path, options),
  post: <T>(path: string, body?: unknown, options?: RequestOptions) =>
    request<T>('POST', path, { ...options, body }),
}
