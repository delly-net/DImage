/**
 * 应用级元信息常量(零依赖叶子模块)。
 *
 * 站点名在 `router/index.ts`(文档标题)与 `components/AppHeader.vue`(品牌区文案)
 * 两处消费,集中定义以避免同一名称出现第二个事实源。
 *
 * `index.html` 为静态 HTML、不经打包器,其中的站点名为不可避免的独立副本
 * (挂载前兜底),改动站点名时须一并同步该文件。
 */
export const SITE_NAME = '小D图像'
