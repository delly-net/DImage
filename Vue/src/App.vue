<script setup lang="ts">
import { RouterView, useRoute } from 'vue-router'
import AppHeader from '@/components/AppHeader.vue'

/**
 * 全局壳:默认带头部,例外页面(如登录页)在路由 `meta.hideHeader` 显式声明。
 *
 * 采用「默认带壳、例外才标注」而非反向的「默认无壳」—— 新增业务页面默认获得头部,
 * 不会因漏配 meta 而静默丢失导航。
 */
const route = useRoute()
</script>

<template>
  <AppHeader v-if="route.meta.hideHeader !== true" />
  <div class="app-main">
    <RouterView />
  </div>
</template>
