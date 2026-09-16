# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目定位

DImage(小D图像)是一个**提供云端图片绘制能力的 MCP 服务**。仓库当前处于脚手架阶段:`Api/` 与 `Vue/` 为空目录,尚无源代码,只有 Skills、LICENSE、README 与 `.mcp.json`。

规划中的技术栈(已确认,尚未落地):
- `Api/` — .NET 10 + Minimal API
- `Vue/` — Vue 3 + Vite + TypeScript

本机工具链:.NET 10.0.401、Node v22.23.2、pnpm 11.21.0。前端使用 **pnpm**(无 `package-lock.json`/`yarn.lock`)。

## 目录约定

| 路径 | 用途 |
|---|---|
| `Api/` | 后端服务(.NET 10 Minimal API)。预期内含 `.sln` 与项目文件 |
| `Vue/` | 前端工程(Vue 3 + Vite + TS) |
| `.claude/skills/` | eazy-rag 系列技能定义,属工作流配置,**非业务代码** |

`.gitignore` 为 VisualStudio 模板 + `node_modules/`,同时覆盖 .NET 与 Node 两侧产物。

## 常用命令

以下命令按约定的技术栈编写,**在目录尚无工程文件时不会成功**:

```bash
# 后端 (Api/)
dotnet build                      # 构建
dotnet run --project Api          # 本地运行
dotnet test                       # 全部测试
dotnet test --filter "FullyQualifiedName~<TestName>"   # 单个测试
dotnet format                     # 格式化

# 前端 (Vue/)
pnpm install
pnpm dev                          # 开发服务器
pnpm build                        # 产物构建
pnpm test                         # 单元测试(Vitest)
pnpm test -- <file>               # 单个测试文件
pnpm lint
```

## 工作流:EAZY.RAG 任务驱动

本仓库通过 `.claude/skills/` 下的 eazy-rag 技能接入 EAZY.RAG 知识库(项目 ID **3**,项目名「小D图像」,模块「内核模块」ID 2)。`.mcp.json` 已配置 `eazy-rag` HTTP MCP 服务。

典型链路:

```
eazy-rag-task-plan  →  eazy-rag-task-do  →  eazy-rag-task-archive
   (生成执行计划)        (执行并回写结果)      (生成任务总结并归档)
```

- `eazy-rag-task-plan`:按实际任务要求生成 MD 执行计划,并在 RAG 库创建任务
- `eazy-rag-task-do`:以任务 Id 为参数读取最新执行计划并执行,回写执行结果并更新状态
- `eazy-rag-task-replan` / `eazy-rag-task-supply` / `eazy-rag-adjust`:分别用于重规划、补充说明、调整
- `eazy-rag-task-archive`:归档时更新项目的执行规范/注意事项/依赖关系,并生成任务总结
- `eazy-rag-init`:初始化项目,扫描代码并写入三份项目文档
- `eazy-rag-module-get` / `eazy-rag-module-set`:模块的读取与写入

**约束**:所有 `mcp__eazy-rag__*` 调用必须携带项目 ID `3`;任务状态流转为 `Created → Designing → CodeGenerating → Executed → Completed → Archived`。

**注意**:MCP 写入不可逆(如 `update_project` 会整体覆盖 MD 字段),调用前须经用户确认。

## 开发约定

- 提交信息使用中文,采用 `<类型>: <描述>` 格式(仓库既有:`新增:`、`docs:`)。
- 功能按模块划分:后端能力落 `Api/`,前端交互落 `Vue/`,两侧通过 HTTP 接口对接。
