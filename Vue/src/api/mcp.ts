import { http } from './client'

/** 服务端基本信息 */
export interface McpServerInfo {
  /** 服务名 */
  name: string
  /** 服务版本 */
  version: string
  /** MCP 协议版本 */
  protocolVersion: string
}

/** 鉴权约定:客户端应使用的请求头名与方案 */
export interface McpAuthInfo {
  /** HTTP 鉴权方案,如 Bearer */
  scheme: string
  /** 承载凭据的请求头名,如 Authorization */
  headerName: string
}

/** 单个 MCP 工具的描述 */
export interface McpToolInfo {
  /** 工具名 */
  name: string
  /** 工具用途说明 */
  description: string
}

/** GET /api/v1/mcp/tools 响应结构 */
export interface McpToolsResponse {
  server: McpServerInfo
  auth: McpAuthInfo
  /** 当前服务发布的 MCP 工具;服务尚未发布工具时为空数组 */
  tools: McpToolInfo[]
}

/**
 * 调用后端 MCP 工具清单接口。
 *
 * 路径写站内相对路径,开发期由 Vite `server.proxy` 的 `/api` 规则转发到后端。
 * 本接口受 JWT 保护,未登录或会话失效时由 `client.ts` 触发全局登出。
 */
export function fetchMcpTools(): Promise<McpToolsResponse> {
  return http.get<McpToolsResponse>('/api/v1/mcp/tools')
}
