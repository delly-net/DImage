/**
 * 授权接口:管理密码换取 JWT,对应后端 `POST /api/v1/auth/token`。
 */

import { HttpError, http } from './client'

/** POST /api/v1/auth/token 响应结构 */
export interface TokenResponse {
  /** JWT 访问令牌 */
  accessToken: string
  /** 令牌类型,固定为 Bearer */
  tokenType: string
  /** 有效期秒数 */
  expiresIn: number
  /** 过期时刻(ISO 8601) */
  expiresAt: string
}

/**
 * 用管理密码换取 JWT。
 *
 * 路径写站内相对路径:后端未配置 CORS,开发期依赖 Vite `server.proxy` 转发到后端,
 * 写绝对地址会触发跨域。
 *
 * `skipAuth: true` 关闭令牌注入与 401 全局登出 —— 本接口的 401 语义是「密码错误」,
 * 不是「登录态失效」。
 */
export function login(password: string): Promise<TokenResponse> {
  return http.post<TokenResponse>('/api/v1/auth/token', { password }, { skipAuth: true })
}

/**
 * 把登录异常转成可直接展示的中文文案。
 *
 * `HttpError.message` 已由 `client.ts` 按后端错误码映射(`invalid_password` → 「密码错误」、
 * `password_required` → 「请输入管理密码」),此处直接沿用;
 * 其余异常(网络中断、超时、响应体非 JSON)统一提示为服务不可达。
 */
export function resolveAuthErrorMessage(error: unknown): string {
  if (error instanceof HttpError) {
    return error.message
  }
  return '无法连接服务,请确认后端已启动后重试'
}
