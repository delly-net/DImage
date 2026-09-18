import { http } from './client'

/** 单个可下载技能的定义 */
export interface SkillDefinition {
  /** 技能 key,用于技能内容的下载地址 */
  key: string
  /** 技能名,即落盘目录 `.claude/skills/{name}/` */
  name: string
  /** 技能用途说明 */
  description: string
}

/** GET /api/v1/skills/install-command 响应结构 */
export interface SkillInstallResponse {
  /** 服务对外根地址,由服务端配置提供 */
  baseUrl: string
  /** 安装脚本地址 */
  installUrl: string
  /** 用户在本地终端执行的安装命令 */
  installCommand: string
  /** MCP 接入配置安装脚本地址 */
  mcpInstallUrl: string
  /** 用户在本地终端执行的 MCP 接入配置安装命令 */
  mcpInstallCommand: string
  /** 执行该命令将安装的技能清单;服务未发布技能时为空数组 */
  skills: SkillDefinition[]
}

/**
 * 获取 Skill 安装命令、MCP 接入配置安装命令与技能清单。
 *
 * 路径写站内相对路径,开发期由 Vite `server.proxy` 的 `/api` 规则转发到后端。
 * 本接口受 JWT 保护,未登录或会话失效时由 `client.ts` 触发全局登出。
 * 服务端未配置对外地址时返回 400 与错误码 `skill_base_url_not_configured`
 * (地址优先级:环境变量 `API_BASE_URL` > 配置 `Service:BaseUrl`)。
 */
export function fetchSkillInstall(): Promise<SkillInstallResponse> {
  return http.get<SkillInstallResponse>('/api/v1/skills/install-command')
}
