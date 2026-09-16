import { http } from './client'

/** GET /health 响应结构 */
export interface HealthResponse {
  /** 服务状态,正常时为 healthy */
  status: string
  /** 服务名 */
  service: string
  /** 服务版本 */
  version: string
  /** 服务端时间戳(ISO 8601) */
  timestamp: string
}

/** 调用后端健康检查接口 */
export function fetchHealth(): Promise<HealthResponse> {
  return http.get<HealthResponse>('/health')
}
