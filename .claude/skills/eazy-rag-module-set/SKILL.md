---
name: eazy-rag-module-set
description: 设置上下文RAG知识库模块信息（项目 ID: 3）——记录/切换项目当前工作模块上下文
effort: medium
user-invocable: true
disable-model-invocation: false
agent: general-purpose
---

# 设置上下文RAG知识库模块信息

此技能用于设置 EAZY.RAG 知识库当前项目「小D图像」（项目 ID: 3）的当前工作模块上下文，使后续技能/操作默认针对该模块。

## 使用方法

```
/eazy-rag-module-set
```

或在聊天中输入「设置模块上下文」触发。

## 功能

1. **列出模块**：调用 `mcp__eazy-rag__list_modules`（projectId=3）获取项目模块列表。
2. **确定目标模块**：根据用户意图选择模块；不确定时用 AskUserQuestion 询问用户选择模块。
3. **设置上下文**：将所选模块写入本地上下文标记文件 `~/.eazyrag/context/3.json`（JSON：currentModuleId/currentModuleName/updatedAt）。
4. **返回结果**：返回设置的模块信息（模块名、描述、当前工作模块）。

## 注意事项

- 所有 MCP 调用必须携带正确的项目 ID：3。
- 上下文标记文件供后续技能/操作读取，作为默认目标模块。