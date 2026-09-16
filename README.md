# DImage(小D图像)

MCP service providing cloud based image drawing

## 目录结构

| 路径 | 说明 |
|---|---|
| `Api/` | 后端服务(.NET 10 + Minimal API),解决方案 `Api/DImage.sln` |
| `Vue/` | 前端工程(Vue 3 + Vite + TypeScript),包管理器为 pnpm |

## 本地开发

### 启动后端

```bash
cd Api
dotnet build
dotnet run --project DImage.Api
```

后端固定监听 `http://localhost:5180`,可用接口:

| 接口 | 说明 |
|---|---|
| `GET /` | 服务元信息(服务名、版本、接口清单) |
| `GET /health` | 健康检查,返回 `{ status, service, version, timestamp }` |
| `GET /api/v1/ping` | 占位业务接口,返回 `{ message: "pong" }` |
| `GET /openapi/v1.json` | OpenAPI 文档,**仅开发环境**暴露 |

### 启动前端

```bash
cd Vue
pnpm install
pnpm dev
```

前端开发服务器固定监听 `http://localhost:5173`,打开后首页会展示后端服务状态。

开发期由 Vite 代理实现前后端同源:`/health` 与 `/api` 会被转发到 `http://localhost:5180`(见 `Vue/vite.config.ts`),因此无需配置后端 CORS。若调整了后端端口,`Api/DImage.Api/Properties/launchSettings.json` 与 `Vue/vite.config.ts` 中的代理目标需同时修改。

### 常用命令

```bash
# 后端 (Api/)
dotnet build
dotnet run --project DImage.Api
dotnet format

# 前端 (Vue/)
pnpm install
pnpm dev
pnpm build
pnpm lint
pnpm test
```
