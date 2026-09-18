<script setup lang="ts">
import { RouterLink } from 'vue-router'
import { SITE_NAME } from '@/appInfo'

/**
 * 主导航栏目,数据驱动渲染。
 *
 * 新增栏目只需在此追加一项(名称须与路由 `name` 一致),模板无需改动。
 */
const NAV_ITEMS = [
  { name: 'home', label: '首页' },
  { name: 'help', label: '帮助' },
] as const
</script>

<template>
  <header class="app-header">
    <div class="app-header__inner">
      <RouterLink class="app-header__brand" :to="{ name: 'home' }">{{ SITE_NAME }}</RouterLink>

      <nav class="app-header__nav" aria-label="主导航">
        <RouterLink v-for="item in NAV_ITEMS" :key="item.name" :to="{ name: item.name }">
          {{ item.label }}
        </RouterLink>
      </nav>
    </div>
  </header>
</template>

<style scoped>
/* 通栏:背景与底边框铺满视口宽度,内容居中约束交给 __inner */
.app-header {
  width: 100%;
  background: var(--color-background-soft, #f8f8f8);
  border-bottom: 1px solid var(--color-border, #e5e7eb);
}

.app-header__inner {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem 1.5rem;
  align-items: center;
  justify-content: space-between;
  max-width: 1280px;
  margin: 0 auto;
  padding: 0.75rem 2rem;
}

.app-header__brand {
  font-weight: 600;
  color: var(--color-heading, #2c3e50);
}

.app-header__nav {
  display: flex;
  flex-wrap: wrap;
  gap: 0.25rem;
  align-items: center;
}

.app-header__nav a {
  padding: 0.25rem 0.75rem;
  border-radius: 6px;
  color: inherit;
}

/* 两个路由均为非嵌套平级记录,exact 匹配语义更准,避免 `/` 意外高亮 */
.app-header__nav a.router-link-exact-active {
  color: hsla(160, 100%, 37%, 1);
  font-weight: 600;
}
</style>
