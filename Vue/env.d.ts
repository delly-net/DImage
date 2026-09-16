/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** 后端基础路径:开发期走 Vite 代理,保持 / */
  readonly VITE_API_BASE_URL: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
