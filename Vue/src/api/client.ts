/**
 * 最小 HTTP 请求封装:基于原生 fetch,提供 baseURL 拼接、超时、JSON 解析与非 2xx 抛错。
 * 后续若需要拦截器 / 重试机制,再评估是否引入 axios。
 */

/** 请求基础路径,取自 VITE_API_BASE_URL;默认同源相对路径 */
const baseURL: string = import.meta.env.VITE_API_BASE_URL ?? '/'

/** 默认超时时间(毫秒) */
const DEFAULT_TIMEOUT = 10000

/** 请求失败错误,携带 HTTP 状态码 */
export class HttpError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'HttpError'
    this.status = status
  }
}

export interface RequestOptions {
  /** 超时时间(毫秒),覆盖默认值 */
  timeout?: number
  /** 请求体,自动 JSON 序列化 */
  body?: unknown
  /** 外部取消信号 */
  signal?: AbortSignal
  /** 附加请求头 */
  headers?: Record<string, string>
}

/** 拼接完整 URL,绝对地址直接返回 */
function resolveUrl(path: string): string {
  if (/^https?:\/\//i.test(path)) {
    return path
  }
  return `${baseURL.replace(/\/$/, '')}/${path.replace(/^\//, '')}`
}

async function request<T>(method: string, path: string, options: RequestOptions = {}): Promise<T> {
  const { timeout = DEFAULT_TIMEOUT, body, signal, headers } = options

  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeout)
  signal?.addEventListener('abort', () => controller.abort(), { once: true })

  try {
    const response = await fetch(resolveUrl(path), {
      method,
      headers: {
        Accept: 'application/json',
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...headers,
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: controller.signal,
    })

    if (!response.ok) {
      throw new HttpError(response.status, `请求失败:${method} ${path} 返回 HTTP ${response.status}`)
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
