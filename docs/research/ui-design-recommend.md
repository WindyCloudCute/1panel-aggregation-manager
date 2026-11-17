## 设计系统建议：PanelFleet 多服务器管理台
**产品类别：** Smart Home/IoT Dashboard

### 🎨 推荐风格（按优先级）
- **首选：Modern Dark (Cinema Mobile)**（Mobile）
  - 理由：符合推理规则「Glassmorphism + Dark Mode (OLED)」，且匹配「均衡 / 现代」取向
  - 适用场景：Developer tools, pro productivity apps, fintech/trading dashboards, media/streaming platforms, AI tool interfaces, high-end gaming companion apps
  - 关键词：dark mode, cinematic, ambient light, glassmorphism, deep black, indigo, glow, blur, atmospheric, reanimated, haptic, premium, layered, frosted glass, linear gradient
  - 模式支持：浅色 supported / 深色 supported
- **次选：Data-Dense Dashboard**（BI/Analytics）
  - 理由：检索命中（相关度 7.09）
  - 适用场景：Business intelligence dashboards, financial analytics, enterprise reporting, operational dashboards, data warehousing
  - 关键词：Multiple charts/widgets, data tables, KPI cards, minimal padding, grid layout, space-efficient, maximum data visibility
  - 模式支持：浅色 supported / 深色 supported
- **备选：Dark Mode (OLED)**（General）
  - 理由：检索命中（相关度 6.77）
  - 适用场景：Night-mode apps, coding platforms, entertainment, eye-strain prevention, OLED devices, low-light
  - 关键词：Dark theme, low light, high contrast, deep black, midnight blue, eye-friendly, OLED, night mode, power efficient
  - 模式支持：浅色 not-recommended / 深色 supported

### 🎨 配色方案（HEX）
| 角色 | HEX |
| --- | --- |
| 主色 | `#1E40AF` |
| 主色前景 | `#FFFFFF` |
| 次色 | `#3B82F6` |
| 强调色(CTA) | `#D97706` |
| 背景 | `#F8FAFC` |
| 前景文字 | `#1E3A8A` |
| 卡片 | `#FFFFFF` |
| 弱化色 | `#E9EEF6` |
| 边框 | `#DBEAFE` |
| 危险色 | `#DC2626` |
| 焦点环 | `#1E40AF` |

*说明：Blue data + amber highlights [Accent adjusted from #F59E0B]*

### 🔤 字体搭配
- **标题：** Fira Code ｜ **正文：** Fira Sans
- **搭配名称：** Dashboard Data
- **气质：** dashboard, data, analytics, code, technical, precise
- **适用场景：** Dashboards, analytics, data visualization, admin panels
- **Google Fonts：** https://fonts.googleapis.com/css2?family=Fira+Code:wght@400;500;600;700&family=Fira+Sans:wght@300;400;500;600;700&display=swap
- **CSS 导入：** `@import url('https://fonts.googleapis.com/css2?family=Fira+Code:wght@400;500;600;700&family=Fira+Sans:wght@300;400;500;600;700&display=swap');`

### 🧭 落地页模式
- **模式：** Enterprise Gateway
- **区块顺序：** Hero (Video/Mission) > Solutions by Industry > Solutions by Role > Client Logos > Contact Sales
- **主 CTA 位置：** Contact Sales (Primary) + Login (Secondary)
- **转化优化：** Path selection (I am a...). Mega menu navigation. Trust signals prominent. Provide pause/stop for video and rotating logos; stop on focus and reduced motion. Logo carousel controls must be keyboard operable; pause moving media offscreen/hidden and render a static final state under reduced motion.

### ✨ 关键效果
Expo.out Bezier(0.16,1,0.3,1) easing; spring modals (damping:20 stiffness:90); haptic-linked press (Impact Light/Medium); animated ambient light blobs (Reanimated translateX/Y slow oscillation); BlurView glassmorphism headers/nav (intensity 20); scale press 0.97 → 1.0; avoid pure #000000 (OLED smear)

### 💡 选择理由
- **配色气质：** Dark + Status indicator colors
- **字体气质：** Clear + Functional typography
- **优先级：** HIGH
- **决策规则：**
  - 若「must have」→ constraint:real-time-controls,constraint:energy-monitoring

### ❌ 反模式（避免）
- Slow updates
- No automation

### ✅ UX 规范要点
- [低] Data Entry｜Bulk Actions；应该：Allow multi-select and bulk edit；避免：Single row actions only
- [中] Sustainability｜Auto-Play Video；应该：Prefer click-to-play; provide pause and captions; stop off-screen and honor reduced motion；避免：Auto-play high-resolution loops without pause or captions
- [严重] Accessibility｜Text Reflow and Spacing；应该：Use fluid sizes content-driven height and unitless line height；避免：Clip text in fixed-width or fixed-height boxes

### 🎚 设计旋钮
- 大胆度 Variance：7/10 — 均衡 / 现代
- 动效 Motion：8/10 — 复杂
- 密度 Density：7/10 — 标准

### 🎬 动效方案（GSAP）
**Page Transition**（Complex）— 触发：route change｜时长：500-800ms｜缓动：`expo.inOut`
```js
const state = Flip.getState('.hero-image'); navigate(); Flip.from(state, { duration: 0.6, ease: 'expo.inOut', absolute: true, zIndex: 100 });
```
- ✅ Verify the shared element exists in both DOM states before calling Flip.from to avoid a silent no-op
- ❌ Don't use shared-element transitions across more than one element pair per navigation; compounding Flips are hard to time correctly

### 📐 间距刻度
- `--space-xs` = `4px`
- `--space-sm` = `8px`
- `--space-md` = `16px`
- `--space-lg` = `24px`
- `--space-xl` = `32px`
- `--space-2xl` = `48px`
- `--space-3xl` = `64px`

### 🛠 实现清单
- [ ] No pure #000000 backgrounds
- [ ] LinearGradient base screen
- [ ] Animated ambient blobs (Reanimated, native driver)
- [ ] BlurView on tab bar and headers
- [ ] borderRadius 16 on all cards
- [ ] Haptic feedback on every Pressable
- [ ] Bezier(0.16,1,0.3,1) easing used
- [ ] Accent glow behind primary button
- [ ] No solid grey borders (rgba only)
- [ ] Bottom sheets replace all modals

