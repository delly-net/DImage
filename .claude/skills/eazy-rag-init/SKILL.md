---
name: eazy-rag-init
description: 项目初始化（项目 ID: 3）——调用标准 /init 扫描并总结执行计划/注意事项/依赖关系更新到项目信息
effort: high
user-invocable: true
disable-model-invocation: false
agent: general-purpose
---

# 项目初始化

此技能用于对当前项目进行初始化：调用 Claude Code 标准 `/init` 进行标准扫描，并针对当前项目代码总结「执行计划」「注意事项」「依赖关系」三份 Markdown 文档，更新到 EAZY.RAG 知识库项目「小D图像」（项目 ID: 3）的项目信息中。

## 使用方法

```
/eazy-rag-init
```

或在聊天中输入「项目初始化」触发。

## 功能

1. **标准扫描**：调用标准 `/init` 对当前项目进行标准扫描，生成/更新 `CLAUDE.md` 项目说明文档。
2. **代码分析**：针对当前项目代码，分析并总结三份 Markdown 文档：
   - **执行计划（ExecutionSpecs）**：项目执行规范/执行计划，含技术栈、构建、测试、部署等关键步骤。
   - **注意事项（ExecutionNotes）**：项目注意事项，含易错点、既有约定、禁忌与坑。
   - **依赖关系（Dependencies）**：项目依赖关系清单，含外部依赖（框架/库/服务）与模块间依赖。
3. **更新项目信息**：调用 `mcp__eazy-rag__update_project`（projectId=3），将三份文档分别写入 `executionSpecs`/`executionNotes`/`dependencies` 字段（仅更新非空字段）。
4. **返回结果**：返回已更新的项目信息概要（执行计划/注意事项/依赖关系）。

## 注意事项

- 所有 MCP 调用必须携带正确的项目 ID：3。
- 更新项目信息前须经用户确认（update_project 会覆盖项目 MD 字段）。
- `/init` 为标准扫描，可能生成或修改 CLAUDE.md，执行前提示用户。