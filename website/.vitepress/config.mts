import { defineConfig } from 'vitepress'

// https://vitepress.dev/reference/site-config
export default defineConfig({
  title: "RocoPilot",
  description: "洛克王国世界自动战斗与奇遇计数工具",
  base: '/RocoPilot/',
  lang: 'zh-CN',
  head: [['link', { rel: 'icon', href: '/RocoPilot/logo.png' }]],
  themeConfig: {
    // https://vitepress.dev/reference/default-theme-config
    logo: '/logo.png',
    nav: [
      { text: '首页', link: '/' },
      { text: '文档', link: '/document/' },
      { text: '下载', link: '/download/' }
    ],

    sidebar: {
      '/document/': [
        {
          text: '文档',
          items: [
            { text: '介绍', link: '/document/' },
            { text: '安装', link: '/document/install' },
            { text: '使用', link: '/document/use' },
            { text: '功能', link: '/document/features' },
            { text: 'FAQ', link: '/document/faq' }
          ]
        }
      ]
    },

    socialLinks: [
      { icon: 'github', link: 'https://github.com/FelixHenrikChristian/RocoPilot' }
    ],

    footer: {
      message: 'Released under the GPL-3.0 License.',
      copyright: 'Copyright © 2026-present Felix Henrik Christian'
    },

    search: {
      provider: 'local'
    }
  }
})
